package com.anylyra.logagent;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * 最小 tar 解析器（POSIX ustar + GNU 长名 + PAX）。
 *
 * <p>为什么不用现成的库：APK 里不能引第三方 jar（会破坏"无 AndroidX、纯框架 API"的构建约束，
 * 也就失去了用 aapt2+javac+d8 手工打包的能力）。这里只实现解包一个我们自己在 CI 里打的包，
 * 所以支持面刻意收窄：普通文件、目录、符号链接、硬链接（降级为复制）、GNU 'L'/'K' 长名、PAX 'x'。
 *
 * <p>安全：所有路径都按 {@code ./a/b} 归一化，出现 {@code ..} 或绝对路径直接拒绝，
 * 防止归档里藏路径穿越（我们自己的包不需要，但解压器不该假设输入可信）。
 */
final class TarExtractor {

    private static final int BLOCK = 512;

    /** 每写完一个数据块回调一次已解压字节数（用于 UI 进度）。 */
    interface SizeListener { void onBytes(long total); }

    private TarExtractor() { }

    /**
     * 读取 {@code in} 里的全部 tar 条目并解到 {@code dest}。
     * 每次读满一个 512 字节头块，数据按 512 对齐跳过。
     */
    static void extract(InputStream in, File dest, SizeListener listener) throws IOException {
        byte[] header = new byte[BLOCK];
        String pendingLongName = null;
        long written = 0;
        long lastReport = 0;

        while (true) {
            if (!readFully(in, header)) return;
            if (isZeroBlock(header)) {
                // 归档结束标记：再读一个块，理论上也是全 0
                return;
            }

            String name = pendingLongName != null ? pendingLongName : cString(header, 0, 100);
            pendingLongName = null;
            if (name.isEmpty()) return;

            // ustar 的 prefix 字段（257..345）：只有 magic 是 "ustar\0" 时有效
            String magic = cString(header, 257, 6);
            if (magic.startsWith("ustar")) {
                String prefix = cString(header, 345, 155);
                if (!prefix.isEmpty()) name = prefix + "/" + name;
            }

            int mode = (int) parseOctal(header, 100, 8);
            long size = parseOctal(header, 124, 12);
            char type = (char) (header[156] & 0xFF);

            switch (type) {
                case 'L': {                       // GNU 长文件名：下一个头里是数据
                    byte[] data = readData(in, size);
                    pendingLongName = cString(data, 0, data.length).trim();
                    continue;                     // 该条目本身不落盘
                }
                case 'K': {                       // GNU 长链接名：读掉数据即可
                    readData(in, size);
                    continue;
                }
                case 'x': {                       // PAX 扩展头：读掉并尝试取 path
                    byte[] data = readData(in, size);
                    String paxPath = paxPath(data);
                    skipPadding(in, size);
                    if (paxPath != null) pendingLongName = paxPath;
                    continue;
                }
                case 'g': {                       // PAX 全局头
                    readData(in, size);
                    skipPadding(in, size);
                    continue;
                }
                default:
                    break;
            }

            File target = resolve(dest, name);

            if (type == '5') {                    // 目录
                target.mkdirs();
            } else if (type == '2') {             // 符号链接
                String link = cString(header, 157, 100);
                target.getParentFile().mkdirs();
                if (target.exists()) target.delete();
                // Android 允许在 app 私有目录建符号链接（proot 的 link2symlink 也依赖这点）
                boolean ok = false;
                try {
                    android.system.Os.symlink(link, target.getAbsolutePath());
                    ok = true;
                } catch (Exception ignored) {
                    // 落到下面的兜底
                }
                if (!ok) {
                    // 拿不到链接目标就建个空文件，避免后续 stat 报 ENOENT；
                    // rootfs 里失败的那几条只会是 /var/lock 这类与运行无关的项
                    target.createNewFile();
                }
            } else if (type == '1') {             // 硬链接：Android 目录禁 link(2)，复制
                String link = cString(header, 157, 100);
                File src = resolve(dest, link);
                target.getParentFile().mkdirs();
                if (src.isFile()) copyFile(src, target); else target.createNewFile();
            } else {                              // 普通文件（'0' 或 '\0'）及其它类型按文件处理
                target.getParentFile().mkdirs();
                if (size == 0) {
                    if (target.exists()) target.delete();
                    target.createNewFile();
                } else {
                    long remain = size;
                    try (OutputStream out = new FileOutputStream(target)) {
                        byte[] buf = new byte[1 << 16];
                        while (remain > 0) {
                            int want = (int) Math.min(buf.length, remain);
                            int n = in.read(buf, 0, want);
                            if (n < 0) throw new IOException("tar 数据被截断：" + name);
                            out.write(buf, 0, n);
                            remain -= n;
                            written += n;
                            if (listener != null && (written - lastReport) >= (4 << 20)) {
                                lastReport = written;
                                listener.onBytes(written);
                            }
                        }
                    }
                    skipPadding(in, size);
                }
            }

            // 权限：tar 里存的是 guest 的 0644/0755，Android 上直接映射
            boolean exec = (mode & 0111) != 0;
            target.setReadable(true, false);
            target.setWritable(true, true);
            if (target.isFile()) target.setExecutable(exec, false);
        }
    }

    // ───────────────────────── 细节 ─────────────────────────

    /** 归一化并限制在 {@code dest} 内。 */
    private static File resolve(File dest, String rawName) throws IOException {
        String n = rawName.replace('\\', '/');
        while (n.startsWith("./")) n = n.substring(2);
        n = n.replaceAll("^/+", "");
        if (n.isEmpty() || n.equals(".")) return dest;

        File target = new File(dest, n);
        String canonicalRoot = dest.getCanonicalPath();
        String canonicalTarget = target.getCanonicalPath();
        if (!canonicalTarget.equals(canonicalRoot) && !canonicalTarget.startsWith(canonicalRoot + File.separator)) {
            throw new IOException("归档包含越界路径：" + rawName);
        }
        return target;
    }

    /** 读满 {@code buf}；流提前结束返回 false（正常结尾）。 */
    private static boolean readFully(InputStream in, byte[] buf) throws IOException {
        int off = 0;
        while (off < buf.length) {
            int n = in.read(buf, off, buf.length - off);
            if (n < 0) return off != 0;   // 半个头块出现说明归档损坏
            off += n;
        }
        return true;
    }

    private static boolean isZeroBlock(byte[] block) {
        for (byte b : block) if (b != 0) return false;
        return true;
    }

    /** tar 的字符串字段：NUL 截断。 */
    private static String cString(byte[] buf, int off, int len) {
        int end = off;
        while (end < off + len && end < buf.length && buf[end] != 0) end++;
        return new String(buf, off, Math.max(0, end - off), StandardCharsets.UTF_8);
    }

    /** tar 的八进制数字字段（可能以空格/NUL 结尾）。 */
    private static long parseOctal(byte[] buf, int off, int len) {
        long v = 0;
        for (int i = off; i < off + len; i++) {
            byte b = buf[i];
            if (b == 0 || b == ' ') continue;
            if (b < '0' || b > '7') break;
            v = (v << 3) + (b - '0');
        }
        return v;
    }

    private static byte[] readData(InputStream in, long size) throws IOException {
        byte[] data = new byte[(int) size];
        int off = 0;
        while (off < data.length) {
            int n = in.read(data, off, data.length - off);
            if (n < 0) throw new IOException("tar 头数据被截断");
            off += n;
        }
        return data;
    }

    private static void skipPadding(InputStream in, long size) throws IOException {
        // 不能用 InputStream.skipNBytes：APK 的 Java 层约束在 --release 8（Java 8 无此方法）
        long pad = (BLOCK - (size % BLOCK)) % BLOCK;
        while (pad > 0) {
            long skipped = in.skip(pad);
            if (skipped <= 0) {
                // skip 可能返回 0；退化为读一个字节，避免死循环
                if (in.read() < 0) return;
                pad--;
            } else {
                pad -= skipped;
            }
        }
    }

    /** 从 PAX 记录（"len key=value\n"）里取 path。 */
    private static String paxPath(byte[] data) {
        String text = new String(data, StandardCharsets.UTF_8);
        for (String line : text.split("\n")) {
            int sp = line.indexOf(' ');
            if (sp < 0) continue;
            String kv = line.substring(sp + 1);
            if (kv.startsWith("path=")) return kv.substring(5);
        }
        return null;
    }

    private static void copyFile(File src, File dst) throws IOException {
        try (InputStream in = new java.io.FileInputStream(src);
             OutputStream out = new FileOutputStream(dst)) {
            byte[] buf = new byte[1 << 16];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
        }
    }
}
