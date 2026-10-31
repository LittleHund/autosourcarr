package autosourcarr.host

import io.github.oshai.kotlinlogging.KotlinLogging
import org.objectweb.asm.ClassReader
import org.objectweb.asm.ClassWriter
import org.objectweb.asm.Opcodes
import org.objectweb.asm.Type
import org.objectweb.asm.tree.AbstractInsnNode
import org.objectweb.asm.tree.ClassNode
import org.objectweb.asm.tree.FieldInsnNode
import org.objectweb.asm.tree.InsnList
import org.objectweb.asm.tree.MethodInsnNode
import org.objectweb.asm.tree.MethodNode
import org.objectweb.asm.tree.TypeInsnNode
import org.objectweb.asm.tree.VarInsnNode
import org.objectweb.asm.tree.analysis.Analyzer
import org.objectweb.asm.tree.analysis.AnalyzerException
import org.objectweb.asm.tree.analysis.BasicValue
import org.objectweb.asm.tree.analysis.Frame
import org.objectweb.asm.tree.analysis.SimpleVerifier
import java.net.URLClassLoader
import java.nio.file.FileSystems
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.extension

/**
 * Makes translated R8 code pass the JVM verifier without turning verification off. dex2jar types some registers
 * as Object where Dalvik's verifier knew the precise class (R8 drops casts that ART can prove). Each method is
 * checked with ASM's verifier against the real class hierarchy (the extension jar over the host's classes); where an
 * Object-typed value reaches a call or field that needs a specific class, a checkcast to that class is inserted
 * (operands above it are spilled to new locals and reloaded). A checkcast cannot change behaviour: the value has
 * that class at run time, otherwise Android would not have accepted the code. Methods that cannot be repaired this
 * way are left as they are and fail as before, with the reason in the log.
 */
object ExtensionBytecodeRepair {
    private val logger = KotlinLogging.logger {}
    private const val MAX_REPAIRS_PER_METHOD = 200

    data class Result(
        val classes: Int,
        val casts: Int,
        val unrepaired: List<String>,
    )

    fun repair(jar: Path): Result {
        var classes = 0
        var casts = 0
        val unrepaired = mutableListOf<String>()
        URLClassLoader(arrayOf(jar.toUri().toURL()), ExtensionBytecodeRepair::class.java.classLoader).use { loader ->
            FileSystems.newFileSystem(jar, null as ClassLoader?).use { fs ->
                val files = Files.walk(fs.getPath("/")).use { paths -> paths.filter { it.extension == "class" }.toList() }
                for (file in files) {
                    val node = ClassNode()
                    ClassReader(Files.readAllBytes(file)).accept(node, ClassReader.EXPAND_FRAMES)
                    var changed = 0
                    for (method in node.methods) {
                        if (method.instructions.size() == 0) continue
                        val (inserted, problem) = repairMethod(node, method, loader)
                        changed += inserted
                        if (problem != null) unrepaired += "${node.name}.${method.name}${method.desc}: $problem"
                    }
                    if (changed == 0) continue
                    val writer =
                        object : ClassWriter(COMPUTE_FRAMES or COMPUTE_MAXS) {
                            override fun getClassLoader(): ClassLoader = loader

                            override fun getCommonSuperClass(
                                type1: String,
                                type2: String,
                            ): String =
                                try {
                                    super.getCommonSuperClass(type1, type2)
                                } catch (_: Throwable) {
                                    "java/lang/Object"
                                }
                        }
                    node.accept(writer)
                    Files.write(file, writer.toByteArray())
                    classes++
                    casts += changed
                }
            }
        }
        if (unrepaired.isNotEmpty()) logger.warn { "Methods the verifier still rejects: ${unrepaired.take(5)}" }
        return Result(classes, casts, unrepaired)
    }

    private fun repairMethod(
        owner: ClassNode,
        method: MethodNode,
        loader: ClassLoader,
    ): Pair<Int, String?> {
        var inserted = 0
        while (inserted < MAX_REPAIRS_PER_METHOD) {
            val verifier =
                HierarchyVerifier(
                    Type.getObjectType(owner.name),
                    owner.superName?.let { Type.getObjectType(it) },
                    owner.interfaces.map { Type.getObjectType(it) },
                    owner.access and Opcodes.ACC_INTERFACE != 0,
                ).apply { setClassLoader(loader) }
            val analyzer = Analyzer(verifier)
            try {
                analyzer.analyze(owner.name, method)
                return inserted to null
            } catch (error: AnalyzerException) {
                val insn = error.node ?: return inserted to (error.message ?: "verifier error")
                val frame = analyzer.frames.getOrNull(method.instructions.indexOf(insn)) ?: return inserted to (error.message ?: "verifier error")
                if (!castOperand(method, insn, frame, verifier)) return inserted to (error.message ?: "verifier error")
                inserted++
            } catch (error: Throwable) {
                // A class the hierarchy cannot load: nothing to repair here.
                return inserted to (error.message ?: error.javaClass.simpleName)
            }
        }
        return inserted to "too many repairs"
    }

    /** Expected reference types of the operands an instruction pops, deepest first (null = not a reference check). */
    private fun expected(insn: AbstractInsnNode): List<Type?>? =
        when (insn) {
            is MethodInsnNode -> {
                val args = Type.getArgumentTypes(insn.desc).toList()
                val receiver = if (insn.opcode == Opcodes.INVOKESTATIC) emptyList() else listOf(Type.getObjectType(insn.owner))
                (receiver + args).map { if (it.sort == Type.OBJECT || it.sort == Type.ARRAY) it else null }
            }
            is FieldInsnNode ->
                when (insn.opcode) {
                    Opcodes.GETFIELD -> listOf(Type.getObjectType(insn.owner))
                    Opcodes.PUTFIELD -> listOf(Type.getObjectType(insn.owner), Type.getType(insn.desc).takeIf { it.sort == Type.OBJECT || it.sort == Type.ARRAY })
                    Opcodes.PUTSTATIC -> listOf(Type.getType(insn.desc).takeIf { it.sort == Type.OBJECT || it.sort == Type.ARRAY })
                    else -> null
                }
            else -> null
        }

    private fun castOperand(
        method: MethodNode,
        insn: AbstractInsnNode,
        frame: Frame<BasicValue>,
        verifier: HierarchyVerifier,
    ): Boolean {
        val wanted = expected(insn) ?: return false
        val count = wanted.size
        if (frame.stackSize < count) return false
        val values = (0 until count).map { frame.getStack(frame.stackSize - count + it) }
        val index =
            wanted.indices.firstOrNull { i ->
                val type = wanted[i] ?: return@firstOrNull false
                val actual = values[i].type ?: return@firstOrNull false
                if (actual.sort != Type.OBJECT && actual.sort != Type.ARRAY) return@firstOrNull false
                if (actual.internalName == "null") return@firstOrNull false
                !verifier.assignable(values[i], BasicValue(type))
            } ?: return false
        val target = wanted[index]!!
        // Spill the operands above the bad one, cast it, and put them back.
        val above = values.subList(index + 1, count)
        val patch = InsnList()
        val slots = mutableListOf<Pair<Int, Type>>()
        var next = method.maxLocals
        for (value in above.asReversed()) {
            val type = value.type ?: return false
            patch.add(VarInsnNode(type.getOpcode(Opcodes.ISTORE), next))
            slots += next to type
            next += type.size
        }
        patch.add(TypeInsnNode(Opcodes.CHECKCAST, target.internalName))
        for ((slot, type) in slots.asReversed()) patch.add(VarInsnNode(type.getOpcode(Opcodes.ILOAD), slot))
        method.maxLocals = next
        method.instructions.insertBefore(insn, patch)
        return true
    }

    /** ASM's verifier over the real hierarchy, with its subtype check made public. */
    class HierarchyVerifier(
        current: Type,
        superType: Type?,
        interfaces: List<Type>,
        isInterface: Boolean,
    ) : SimpleVerifier(Opcodes.ASM9, current, superType, interfaces, isInterface) {
        fun assignable(
            value: BasicValue,
            expected: BasicValue,
        ): Boolean = isSubTypeOf(value, expected)
    }
}
