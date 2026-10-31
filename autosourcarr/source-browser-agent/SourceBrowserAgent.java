package autosourcarr.browser;

import java.lang.instrument.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.security.ProtectionDomain;
import java.util.*;
import java.io.*;
import jdk.internal.org.objectweb.asm.*;

/** Two exact hooks for the pinned Suwayomi/JCEF runtime; no source or cookie traffic. */
public final class SourceBrowserAgent {
    static final String ORIGINAL = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static final Properties status = new Properties();
    private static Path statusFile;
    public static void premain(String options, Instrumentation instrumentation) throws Exception {
        String ua = System.getenv("AUTOSOURCARR_SOURCE_USER_AGENT");
        if (ua == null || ua.isBlank()) return;
        if (ua.length() > 1024 || ua.chars().anyMatch(c -> c < 32 || c > 126)) throw new IllegalArgumentException("Invalid source user agent");
        statusFile = Path.of(System.getenv("AUTOSOURCARR_SOURCE_UA_STATUS"));
        status.setProperty("revision", System.getenv("AUTOSOURCARR_SOURCE_UA_REVISION"));
        mark("agent", "loaded");
        // The ASM package ships with the JVM. Export only to this agent's module.
        instrumentation.redefineModule(Object.class.getModule(), Set.of(),
            Map.of("jdk.internal.org.objectweb.asm", Set.of(SourceBrowserAgent.class.getModule())),
            Map.of(), Set.of(), Map.of());
        System.setProperty("http.agent", ua);
        instrumentation.addTransformer(new Hooks(ua));
    }
    static synchronized void mark(String key, String value) {
        status.setProperty(key, value);
        try {
            Files.createDirectories(statusFile.getParent());
            Path pending = statusFile.resolveSibling(statusFile.getFileName()+".tmp");
            try (OutputStream out=Files.newOutputStream(pending)) { status.store(out, "AutoSourcarr source browser runtime"); }
            Files.move(pending,statusFile,StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException error) { System.err.println("AutoSourcarr browser status: "+error.getClass().getSimpleName()); }
    }
    static final class Hooks implements ClassFileTransformer {
        private final String ua;
        Hooks(String value) { ua=value; }
        @Override public byte[] transform(ClassLoader loader,String name,Class<?> redefined,ProtectionDomain domain,byte[] bytes) {
            boolean network="eu/kanade/tachiyomi/network/NetworkHelper".equals(name);
            boolean cef="org/cef/CefSettings".equals(name);
            if (!network && !cef) return null;
            String key=network?"network":"webview";
            try {
                int[] matches={0}; boolean[] hasField={false};
                ClassReader reader=new ClassReader(bytes);
                ClassWriter writer=new ClassWriter(reader,ClassWriter.COMPUTE_MAXS);
                reader.accept(new ClassVisitor(Opcodes.ASM8,writer) {
                    @Override public FieldVisitor visitField(int access,String field,String desc,String signature,Object value) {
                        if (field.equals("user_agent") && desc.equals("Ljava/lang/String;")) hasField[0]=true;
                        return super.visitField(access,field,desc,signature,value);
                    }
                    @Override public MethodVisitor visitMethod(int access,String method,String desc,String signature,String[] exceptions) {
                        MethodVisitor delegate=super.visitMethod(access,method,desc,signature,exceptions);
                        return new MethodVisitor(Opcodes.ASM8,delegate) {
                            @Override public void visitLdcInsn(Object value) {
                                if (network && ORIGINAL.equals(value)) { matches[0]++; super.visitLdcInsn(ua); }
                                else super.visitLdcInsn(value);
                            }
                            @Override public void visitInsn(int opcode) {
                                if (cef && method.equals("<init>") && desc.equals("()V") && opcode==Opcodes.RETURN) {
                                    matches[0]++; super.visitVarInsn(Opcodes.ALOAD,0);super.visitLdcInsn(ua);
                                    super.visitFieldInsn(Opcodes.PUTFIELD,name,"user_agent","Ljava/lang/String;");
                                }
                                super.visitInsn(opcode);
                            }
                        };
                    }
                },0);
                if (matches[0]!=1 || (cef && !hasField[0])) throw new IllegalStateException("Pinned runtime hook did not match");
                byte[] result=writer.toByteArray();mark(key,"applied");return result;
            } catch (Throwable error) { mark(key,"unsupported: "+error.getClass().getSimpleName());return null; }
        }
    }
}
