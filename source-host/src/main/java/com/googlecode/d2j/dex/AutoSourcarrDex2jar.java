/*
 * Based on com.googlecode.d2j.dex.Dex2jar from dex2jar 2.4.38 (https://github.com/ThexXTURBOXx/dex2jar),
 * Copyright (c) 2009-2012 Panxiaobo, Apache License 2.0. Changed for AutoSourcarr (source host):
 * only doTranslate is kept, with one extra IR pass (fixConstructorOwners) before NewTransformer.
 */
package com.googlecode.d2j.dex;

import com.googlecode.d2j.converter.IR2JConverter;
import com.googlecode.d2j.node.DexFileNode;
import com.googlecode.d2j.node.DexMethodNode;
import com.googlecode.d2j.reader.BaseDexFileReader;
import com.googlecode.d2j.reader.DexFileReader;
import com.googlecode.d2j.reader.MultiDexFileReader;
import com.googlecode.dex2jar.ir.IrMethod;
import com.googlecode.dex2jar.ir.stmt.LabelStmt;
import com.googlecode.dex2jar.ir.stmt.Stmt;
import com.googlecode.dex2jar.tools.Constants;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileSystem;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.spi.FileSystemProvider;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Random;
import java.util.Set;
import java.util.stream.Collectors;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;

import com.googlecode.d2j.Method;
import com.googlecode.dex2jar.ir.expr.InvokeExpr;
import com.googlecode.dex2jar.ir.expr.Local;
import com.googlecode.dex2jar.ir.expr.NewExpr;
import com.googlecode.dex2jar.ir.expr.Value;
import java.util.LinkedHashSet;

/**
 * Translates an extension's dex code to JVM classes the same way Suwayomi does, plus one fix: R8-optimised
 * extensions (Keiyoushi builds since 2026) may run "new X" followed by a superclass constructor
 * (for example Object.<init>), which Dalvik accepts and the JVM verifier rejects. dex2jar 2.4.38 then emits
 * "new Object" and the class fails with VerifyError. The constructor call is retargeted to X.<init> and the
 * missing constructors are recorded in {@link #constructorFixes} for {@code ExtensionConstructors} to add.
 */
public final class AutoSourcarrDex2jar {
    private final BaseDexFileReader reader;
    private final DexExceptionHandler exceptionHandler;
    private final int readerConfig;
    private final int v3Config;
    /** "Ltype;|Lowner;|param,param" for every retargeted constructor call. */
    public final Set<String> constructorFixes = new LinkedHashSet<>();

    /** Same options as Suwayomi's PackageTools.dex2jar: skip debug, topological sort, frames, keep names. */
    public AutoSourcarrDex2jar(BaseDexFileReader reader, DexExceptionHandler exceptionHandler) {
        this.reader = reader;
        this.exceptionHandler = exceptionHandler;
        this.readerConfig = DexFileReader.SKIP_DEBUG | DexFileReader.COMPUTE_FRAMES | DexFileReader.DONT_SANITIZE_NAMES;
        this.v3Config = V3.TOPOLOGICAL_SORT;
    }

    public void to(Path file) throws IOException {
        if (Files.exists(file) && Files.isDirectory(file)) {
            doTranslate(file, null);
        } else {
            try (FileSystem fs = createZip(file)) {
                doTranslate(fs.getPath("/"), null);
            }
        }
    }

    private static FileSystem createZip(Path output) throws IOException {
        Map<String, Object> env = new HashMap<>();
        env.put("create", "true");
        Files.deleteIfExists(output);
        Path parent = output.getParent();
        if (parent != null && !Files.exists(parent)) {
            Files.createDirectories(parent);
        }
        for (FileSystemProvider p : FileSystemProvider.installedProviders()) {
            String s = p.getScheme();
            if ("jar".equals(s) || "zip".equalsIgnoreCase(s)) {
                return p.newFileSystem(output, env);
            }
        }
        throw new IOException("cant find zipfs support");
    }

    /**
     * R8 skips constructors it proves trivial: "new X" may be initialised with a superclass constructor, and a
     * constructor may call a constructor further up than its direct superclass. Dalvik allows both, the JVM allows
     * neither. Both calls are retargeted (to X.<init>, or to the direct superclass) and recorded so the missing
     * forwarding constructors are added after translation.
     */
    static void fixConstructorOwners(IrMethod irMethod, Set<String> fixes, Map<String, String> parentsByName) {
        Map<Local, String> created = new HashMap<>();
        Set<Local> self = new java.util.HashSet<>();
        boolean constructor = "<init>".equals(irMethod.name);
        for (Stmt p : irMethod.stmts) {
            if (p.st == Stmt.ST.IDENTITY && p.getOp1().vt == Value.VT.LOCAL && p.getOp2().vt == Value.VT.THIS_REF) {
                self.add((Local) p.getOp1());
                continue;
            }
            if (p.st == Stmt.ST.ASSIGN && p.getOp1().vt == Value.VT.LOCAL && p.getOp2().vt == Value.VT.LOCAL && self.contains((Local) p.getOp2())) {
                self.add((Local) p.getOp1());
                continue;
            }
            if (p.st == Stmt.ST.ASSIGN && p.getOp1().vt == Value.VT.LOCAL && p.getOp2().vt == Value.VT.NEW) {
                created.put((Local) p.getOp1(), ((NewExpr) p.getOp2()).type);
                continue;
            }
            if (constructor && p.st == Stmt.ST.VOID_INVOKE && p.getOp() instanceof InvokeExpr) {
                InvokeExpr call = (InvokeExpr) p.getOp();
                if (call.vt == Value.VT.INVOKE_SPECIAL && "<init>".equals(call.getName()) && call.getOps().length > 0
                        && call.getOps()[0].vt == Value.VT.LOCAL && self.contains((Local) call.getOps()[0])) {
                    String own = toInternalClassName(irMethod.owner);
                    String target = toInternalClassName(call.getOwner());
                    String parent = parentsByName.get(own);
                    if (parent != null && !target.equals(own) && !target.equals(parent)) {
                        fixes.add("L" + parent + ";|" + call.getOwner() + "|" + String.join(",", call.getProto().getParameterTypes()));
                        call.method = new Method("L" + parent + ";", "<init>", call.getProto());
                    }
                    continue;
                }
            }
            if (p.st != Stmt.ST.VOID_INVOKE || !(p.getOp() instanceof InvokeExpr)) continue;
            InvokeExpr ie = (InvokeExpr) p.getOp();
            if (ie.vt != Value.VT.INVOKE_SPECIAL || !"<init>".equals(ie.getName()) || ie.getOps().length == 0) continue;
            Value target = ie.getOps()[0];
            String type = null;
            if (target.vt == Value.VT.LOCAL) type = created.remove((Local) target);
            else if (target.vt == Value.VT.NEW) type = ((NewExpr) target).type;
            if (type == null || type.equals(ie.getOwner())) continue;
            fixes.add(type + "|" + ie.getOwner() + "|" + String.join(",", ie.getProto().getParameterTypes()));
            ie.method = new Method(type, "<init>", ie.getProto());
        }
    }

    /** The class itself, then its superclasses up to java/lang/Object (interfaces count as Object, as for the JVM). */
    static java.util.List<String> superChain(String type, Map<String, String> parentsByName) {
        java.util.List<String> chain = new java.util.ArrayList<>();
        String current = type;
        while (current != null && chain.size() < 64) {
            chain.add(current);
            if ("java/lang/Object".equals(current)) return chain;
            String parent = parentsByName.get(current);
            if (parent == null) {
                try {
                    Class<?> loaded = Class.forName(current.replace('/', '.'), false, AutoSourcarrDex2jar.class.getClassLoader());
                    Class<?> superclass = loaded.isInterface() ? Object.class : loaded.getSuperclass();
                    parent = superclass == null ? null : superclass.getName().replace('.', '/');
                } catch (Throwable unknown) {
                    parent = null;
                }
            }
            current = parent;
        }
        if (!chain.contains("java/lang/Object")) chain.add("java/lang/Object");
        return chain;
    }

    private static String toInternalClassName(String key) {
        if (key.endsWith(";")) key = key.substring(1, key.length() - 1);
        return key;
    }

    public void doTranslate(final Path dist, final ByteArrayOutputStream baos) {

        DexFileNode fileNode = new DexFileNode();
        try {
            reader.accept(fileNode, readerConfig | DexFileReader.IGNORE_READ_EXCEPTION);
        } catch (Exception ex) {
            exceptionHandler.handleFileException(ex);
        }

        Map<String, String> parentsByName = fileNode.clzs.stream()
                .filter(c -> c.superClass != null)
                .collect(Collectors.toMap(
                        c -> toInternalClassName(c.className),
                        c -> toInternalClassName(c.superClass)));

        ClassVisitorFactory cvf = new ClassVisitorFactory() {
            @Override
            public ClassVisitor create(final String name) {
                // If we choose to recompute the stack map frames, we need a special impl
                final ClassWriter cw = (readerConfig & DexFileReader.COMPUTE_FRAMES) == 0
                        ? new ClassWriter(ClassWriter.COMPUTE_MAXS)
                        : new ClassWriter(ClassWriter.COMPUTE_FRAMES) {
                    @Override
                    protected String getCommonSuperClass(String type1, String type2) {
                        // AutoSourcarr: dex2jar 2.4.38 never compared type2 itself with type1's ancestors, so a merge of a
                        // class with its own superclass became Object and the JVM rejected the frames. Both chains are
                        // walked in full (extension classes from the dex, others from the host class path).
                        if (type1.equals(type2)) return type1;
                        Set<String> parentsOfType1 = new HashSet<>(superChain(type1, parentsByName));
                        for (String candidate : superChain(type2, parentsByName)) {
                            if (parentsOfType1.contains(candidate)) return candidate;
                        }
                        return "java/lang/Object";
                    }
                };
                final LambadaNameSafeClassAdapter rca = new LambadaNameSafeClassAdapter(cw,
                        (readerConfig & DexFileReader.DONT_SANITIZE_NAMES) != 0);
                return new ClassVisitor(Constants.ASM_VERSION, rca) {
                    @Override
                    public void visitEnd() {
                        super.visitEnd();
                        String className = rca.getClassName();
                        byte[] data;
                        try {
                            // FIXME handle 'java.lang.RuntimeException: Method code too large!'
                            data = cw.toByteArray();
                        } catch (Exception ex) {
                            System.err.printf("ASM failed to generate .class file: %s%n", className);
                            exceptionHandler.handleFileException(ex);
                            return;
                        }
                        try {
                            if (baos != null) {
                                byte[] classNameBytes = className.getBytes(StandardCharsets.UTF_8);
                                baos.write(ByteBuffer.allocate(4).putInt(classNameBytes.length).array());
                                baos.write(classNameBytes);
                                baos.write(ByteBuffer.allocate(4).putInt(data.length).array());
                                baos.write(data);
                            }
                        } catch (IOException e) {
                            e.printStackTrace(System.err);
                        }
                        try {
                            if (dist != null) {
                                Path dist1 = dist.resolve(className + ".class");
                                Path parent = dist1.getParent();
                                if (parent != null && !Files.exists(parent)) {
                                    Files.createDirectories(parent);
                                }
                                Files.write(dist1, data);
                            }
                        } catch (IOException e) {
                            e.printStackTrace(System.err);
                        }
                    }
                };
            }
        };

        new ExDex2Asm(exceptionHandler) {
            public void convertCode(DexMethodNode methodNode, MethodVisitor mv, ClzCtx clzCtx) {
                if ((readerConfig & DexFileReader.SKIP_CODE) != 0 && methodNode.method.getName().equals("<clinit>")) {
                    // also skip clinit
                    return;
                }
                super.convertCode(methodNode, mv, clzCtx);
            }

            @Override
            public void optimize(IrMethod irMethod) {
                T_CLEAN_LABEL.transform(irMethod);
                /*if (0 != (v3Config & V3.TOPOLOGICAL_SORT)) {
                    // T_topologicalSort.transform(irMethod);
                }*/
                T_DEAD_CODE.transform(irMethod);
                T_REMOVE_LOCAL.transform(irMethod);
                T_REMOVE_CONST.transform(irMethod);
                T_ZERO.transform(irMethod);
                if (T_NPE.transformReportChanged(irMethod)) {
                    T_DEAD_CODE.transform(irMethod);
                    T_REMOVE_LOCAL.transform(irMethod);
                    T_REMOVE_CONST.transform(irMethod);
                }
                // AutoSourcarr: R8 may initialise a new X with a superclass constructor; see fixConstructorOwners.
                fixConstructorOwners(irMethod, constructorFixes, parentsByName);
                T_NEW.transform(irMethod);
                T_FILL_ARRAY.transform(irMethod);
                T_AGG.transform(irMethod);
                T_MULTI_ARRAY.transform(irMethod);
                T_VOID_INVOKE.transform(irMethod);
                if (0 != (v3Config & V3.PRINT_IR)) {
                    int i = 0;
                    for (Stmt p : irMethod.stmts) {
                        if (p.st == Stmt.ST.LABEL) {
                            LabelStmt labelStmt = (LabelStmt) p;
                            labelStmt.displayName = "L" + i++;
                        }
                    }
                    System.out.println(irMethod);
                }
                {
                    // https://github.com/pxb1988/dex2jar/issues/477
                    // dead code found in unssa, clean up
                    T_DEAD_CODE.transform(irMethod);
                    T_REMOVE_LOCAL.transform(irMethod);
                    T_REMOVE_CONST.transform(irMethod);
                }
                T_TYPE.transform(irMethod);
                T_UNSSA.transform(irMethod);
                T_IR_2_J_REG_ASSIGN.transform(irMethod);
                T_TRIM_EX.transform(irMethod);
            }

            @Override
            public void ir2j(IrMethod irMethod, MethodVisitor mv, ClzCtx clzCtx) {
                new IR2JConverter()
                        .optimizeSynchronized(0 != (V3.OPTIMIZE_SYNCHRONIZED & v3Config))
                        .clzCtx(clzCtx)
                        .ir(irMethod)
                        .asm(mv)
                        .convert();
            }
        }.convertDex(fileNode, cvf);

    }

}
