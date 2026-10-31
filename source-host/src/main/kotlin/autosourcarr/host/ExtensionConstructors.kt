package autosourcarr.host

import org.objectweb.asm.ClassReader
import org.objectweb.asm.ClassVisitor
import org.objectweb.asm.ClassWriter
import org.objectweb.asm.Opcodes
import org.objectweb.asm.Type
import java.nio.file.FileSystems
import java.nio.file.Files
import java.nio.file.Path

/**
 * Second half of the R8 constructor fix (see com.googlecode.d2j.dex.AutoSourcarrDex2jar): every class whose
 * "new X" was retargeted to X.<init>(args) gets that constructor if it lacks one. The added constructor only
 * calls the same constructor of its superclass, which is exactly what the dex code did; when the superclass is
 * part of the extension and lacks it too, it gets one the same way, up to the class R8 originally called.
 */
object ExtensionConstructors {
    fun add(
        jar: Path,
        fixes: Set<String>,
    ): Int {
        if (fixes.isEmpty()) return 0
        var added = 0
        FileSystems.newFileSystem(jar, null as ClassLoader?).use { fs ->
            fun classPath(name: String) = fs.getPath("/$name.class")

            fun ensure(
                internal: String,
                stopAt: String,
                descriptor: String,
                depth: Int,
            ) {
                if (depth > 16 || internal == stopAt) return
                val path = classPath(internal)
                if (!Files.exists(path)) return
                val reader = ClassReader(Files.readAllBytes(path))
                var has = false
                reader.accept(
                    object : ClassVisitor(Opcodes.ASM9) {
                        override fun visitMethod(
                            access: Int,
                            name: String?,
                            desc: String?,
                            signature: String?,
                            exceptions: Array<out String>?,
                        ): org.objectweb.asm.MethodVisitor? {
                            if (name == "<init>" && desc == descriptor) has = true
                            return null
                        }
                    },
                    ClassReader.SKIP_CODE,
                )
                val superName = reader.superName ?: "java/lang/Object"
                ensure(superName, stopAt, descriptor, depth + 1)
                if (has) return
                val writer = ClassWriter(reader, ClassWriter.COMPUTE_MAXS)
                reader.accept(writer, 0)
                val method = writer.visitMethod(Opcodes.ACC_PUBLIC or Opcodes.ACC_SYNTHETIC, "<init>", descriptor, null, null)
                method.visitCode()
                method.visitVarInsn(Opcodes.ALOAD, 0)
                var slot = 1
                for (argument in Type.getArgumentTypes(descriptor)) {
                    method.visitVarInsn(argument.getOpcode(Opcodes.ILOAD), slot)
                    slot += argument.size
                }
                method.visitMethodInsn(Opcodes.INVOKESPECIAL, superName, "<init>", descriptor, false)
                method.visitInsn(Opcodes.RETURN)
                method.visitMaxs(0, 0)
                method.visitEnd()
                writer.visitEnd()
                Files.write(path, writer.toByteArray())
                added++
            }

            for (fix in fixes) {
                val (type, owner, params) = fix.split('|', limit = 3)
                val descriptor = "(" + params.split(',').filter { it.isNotEmpty() }.joinToString("") + ")V"
                ensure(Type.getType(type).internalName, Type.getType(owner).internalName, descriptor, 0)
            }
        }
        return added
    }
}
