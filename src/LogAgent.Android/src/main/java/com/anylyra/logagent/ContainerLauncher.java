package com.anylyra.logagent;

import android.content.Context;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.zip.GZIPInputStream;

/**
 * 容器启动器：把 proroot / proot 的 argv 拼出来并交给 ProcessBuilder。
 *
 * <p>为什么可执行文件必须从 nativeLibraryDir 取：
 * Android 10+ 的 W^X 与 SELinux {@code app_data_file} 都禁止从应用私有目录 execve。
 * 实测：把 /system/bin/true 拷到 filesDir 再执行会失败。只有 APK 解出来的
 * {@code nativeLibraryDir}（SELinux 的 {@code system_app_data_file} execute 例外）可以。
 *
 * <p>为什么 rootfs 里的 ELF 反而不需要可执行权限：
 * proroot 用 LD_PRELOAD stub + 用户态 loader 装载 guest 程序，内核从不 execve 它们。
 */
final class ContainerLauncher {

    /** 引擎选择结果，UI 用来显示"proroot"还是"proot 兜底"。 */
    static final class Engine {
        final String id;
        final List<String> argv;
        Engine(String id, List<String> argv) { this.id = id; this.argv = argv; }
    }

    private final Context ctx;
    private final File baseDir;
    private final File rootfsDir;
    private final File appDir;
    private final File tmpDir;
    private final File configDir;

    ContainerLauncher(Context ctx) {
        this.ctx = ctx;
        this.baseDir = new File(ctx.getFilesDir(), "container");
        this.rootfsDir = new File(baseDir, "rootfs");
        this.appDir = new File(baseDir, "app");
        this.tmpDir = new File(baseDir, "tmp");
        this.configDir = new File(baseDir, "config");
    }

    File rootfsDir() { return rootfsDir; }
    File appDir() { return appDir; }
    File configDir() { return configDir; }

    /** 安装完成标记：两个包都解压完之后才写，用于区分"解压了一半"与"装好了"。 */
    private File installStamp() { return new File(baseDir, ".installed"); }

    boolean isInstalled() {
        // 不能只看两个关键文件在不在：rootfs 解压到一半（usr/bin/sh 已落盘、
        // 其余缺失）时也会成立，接着启动会在容器里报各种找不到命令的怪错。
        // 所以以解压全部完成后写的标记文件为准。
        return installStamp().isFile()
            && new File(rootfsDir, "usr/bin/sh").isFile()
            && new File(appDir, "LogAgent.Headless").isFile();
    }

    /** 首次安装：解压 assets 里的 rootfs 与 .NET 负载。返回 false 表示已有。 */
    boolean install(Progress progress) throws IOException {
        if (isInstalled()) return false;
        baseDir.mkdirs();
        tmpDir.mkdirs();
        configDir.mkdirs();

        // 上次可能解压到一半（用户杀进程 / 系统回收），残目录会让后续解压
        // 覆盖出混合状态。既然要重装，就整体清掉重来。
        deleteRecursively(installStamp());
        if (!new File(rootfsDir, "usr/bin/sh").isFile()) deleteRecursively(rootfsDir);
        if (!new File(appDir, "LogAgent.Headless").isFile()) deleteRecursively(appDir);

        extractAsset("rootfs.tar.gz", rootfsDir, progress);
        extractAsset("logagent.tar.gz", appDir, progress);
        extractProotHelpers();

        // guest 的 /tmp 必须可写；android 侧目录权限已经是 07xx，这里只保证存在
        new File(rootfsDir, "tmp").mkdirs();

        // 标记最后写：中途失败则下次仍会走重装
        try (FileOutputStream fos = new FileOutputStream(installStamp())) {
            fos.write(("v1\n" + rootfsDir.getAbsolutePath() + "\n").getBytes(java.nio.charset.StandardCharsets.UTF_8));
        }
        return true;
    }

    /** 递归删除（只用于我们自己的目录，符号链接不跟随）。 */
    private static void deleteRecursively(File f) {
        if (f == null || !f.exists()) return;
        if (f.isDirectory() && !isSymlink(f)) {
            File[] kids = f.listFiles();
            if (kids != null) for (File k : kids) deleteRecursively(k);
        }
        // 忽略删除失败：上层会靠标记文件决定是否需要重装
        f.delete();
    }

    private static boolean isSymlink(File f) {
        try {
            File canon = f.getParentFile() == null
                ? f
                : new File(f.getParentFile().getCanonicalFile(), f.getName());
            return !canon.getCanonicalFile().equals(canon.getAbsoluteFile());
        } catch (IOException e) {
            return true; // 判断不了就当作链接，避免跟随删除
        }
    }

    /**
     * 解出 proot 兜底引擎需要、但 APK 的 jniLibs 装不了的文件。
     *
     * <p>为什么装不了：APK 安装时只会解出 {@code lib*.so} 命名的库，
     * 而 proot 需要按 SONAME 精确命名的 {@code libtalloc.so.2}（不匹配 lib*.so）。
     * android-shmem 与 loader 已经在 jniLibs 里，这里只补 talloc。
     */
    private void extractProotHelpers() throws IOException {
        File helper = prootHelperDir();
        helper.mkdirs();
        try (InputStream in = ctx.getAssets().open("tools/libtalloc.so.2")) {
            File out = new File(helper, "libtalloc.so.2");
            try (FileOutputStream fos = new FileOutputStream(out)) {
                byte[] buf = new byte[1 << 16];
                int n;
                while ((n = in.read(buf)) > 0) fos.write(buf, 0, n);
            }
            out.setReadable(true, false);
            out.setExecutable(true, false);
        }
    }

    File prootHelperDir() { return new File(baseDir, "proot"); }

    /** 拼启动参数。{@code useFallback} 为 true 时走 proot。 */
    Engine build(String subcommand, boolean useFallback) {
        String nativeLib = ctx.getApplicationInfo().nativeLibraryDir;
        List<String> argv = new ArrayList<>();

        if (!useFallback) {
            File launcher = new File(nativeLib, "libproroot.so");
            argv.add(launcher.getAbsolutePath());
            argv.add("-r"); argv.add(rootfsDir.getAbsolutePath());
            argv.add("-0");
            // Android 禁 link(2)，dpkg/apt 与部分工具依赖硬链接语义
            argv.add("--link2symlink");
            argv.add("-w"); argv.add("/root");
        } else {
            File launcher = new File(nativeLib, "libproot.so");
            argv.add(launcher.getAbsolutePath());
            argv.add("--link2symlink");
            argv.add("-0");
            argv.add("--rootfs=" + rootfsDir.getAbsolutePath());
            argv.add("--cwd=/root");
        }

        // 这两条是硬需求（实测）：缺 /proc 时 .NET 报 "Failed to load JIT compiler"，
        // 缺 /dev 时偶发 "cryptographic operation" 失败。
        argv.add("-b"); argv.add("/proc:" + "/proc");
        argv.add("-b"); argv.add("/dev:" + "/dev");
        argv.add("-b"); argv.add(appDir.getAbsolutePath() + ":/app");
        argv.add("-b"); argv.add(configDir.getAbsolutePath() + ":/cfg");

        // 注意：这是给 /bin/sh -c 的脚本，多条语句必须用换行或 ; 分隔。
        // 早期版本用 String.join(" ", …) 拼成了一行，
        // 结果 "export PATH=… HOME=/root …" 被当成 export 的多个变量（含非法标识符），
        // 后续的 printf / exec 也没执行，引擎自检必然失败。
        String script = String.join("; ", new String[] {
            "export PATH=/usr/local/bin:/usr/bin:/bin",
            "export HOME=/root",
            "export LOGAGENT_CONFIG_DIR=/cfg",
            // rootfs 不带 ICU，且 proroot 解析不了 libicui18n 的间接符号
            "export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
            // rootfs 无 tzdata，用固定偏移（中国无夏令时）
            "export TZ=CST-8",
            // 容器内 DNS 必须指向 Android 的本地 resolver，写 8.8.8.8 会超时
            "printf 'nameserver 127.0.0.1\\n' > /etc/resolv.conf",
            "exec /app/LogAgent.Headless " + subcommand,
        });

        argv.add("/bin/sh");
        argv.add("-c");
        argv.add(script);
        return new Engine(useFallback ? "proot" : "proroot", argv);
    }

    /** 给 ProcessBuilder 准备环境（宿主侧，不是 guest 侧）。 */
    void applyHostEnvironment(ProcessBuilder pb) {
        pb.environment().put("PROROOT_TMP_DIR", tmpDir.getAbsolutePath());
        pb.environment().put("BXROOT_TMP_DIR", tmpDir.getAbsolutePath());
        pb.environment().put("PROOT_TMP_DIR", tmpDir.getAbsolutePath());
        // 两个引擎都从 nativeLibraryDir 找兄弟 .so；loader 路径显式给出更稳
        pb.environment().put("PROROOT_LIB_PATH", new File(ctx.getApplicationInfo().nativeLibraryDir, "libproroot-runtime.so").getAbsolutePath());
        pb.environment().put("PROROOT_STUB_LOADER", new File(ctx.getApplicationInfo().nativeLibraryDir, "libproroot-stub-loader.so").getAbsolutePath());
        String nativeLib = ctx.getApplicationInfo().nativeLibraryDir;
        // loader 在 jniLibs（名字是 libprootloader.so，这是文件路径不是 SONAME，loading 走 path）
        pb.environment().put("PROOT_LOADER", new File(nativeLib, "libprootloader.so").getAbsolutePath());
        // talloc 必须按 SONAME 找 libtalloc.so.2，只能从 helper 目录加载
        pb.environment().put("LD_LIBRARY_PATH", nativeLib + ":" + prootHelperDir().getAbsolutePath());
        // Termux 遗留的 LD_PRELOAD 会把 guest 的 loader 搞坏
        pb.environment().remove("LD_PRELOAD");
    }

    /**
     * 自检结果：成功与否 + 容器里的全部输出。
     *
     * <p>为什么要带回输出：自检失败时，用户能看到的只有"失败"两个字，
     * 而真正的原因（找不到 loader、ptrace 被拒、ICU 报错…）都在子进程的 stderr 里。
     * 把输出一并返回，UI 直接打到日志区，省掉一次盲猜。
     */
    static final class CheckResult {
        final boolean ok;
        final String output;
        CheckResult(boolean ok, String output) { this.ok = ok; this.output = output; }
    }

    /** 快速自检：跑 {@code --help}，exit=0 且输出里出现 "LogAgent" 才算启动成功。 */
    CheckResult selfCheck(boolean useFallback, long timeoutMs) {
        StringBuilder sb = new StringBuilder();
        try {
            Engine e = build("--help", useFallback);
            ProcessBuilder pb = new ProcessBuilder(e.argv).redirectErrorStream(true);
            applyHostEnvironment(pb);
            Process p = pb.start();

            Thread reader = new Thread(() -> {
                try (BufferedReader r = new BufferedReader(
                        new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
                    String line;
                    while ((line = r.readLine()) != null) {
                        synchronized (sb) {
                            if (sb.length() < 8000) sb.append(line).append('\n');
                        }
                    }
                } catch (IOException ignored) {
                    // 进程被 kill 时读流会抛，正常现象
                }
            }, "selfcheck-read");
            reader.setDaemon(true);
            reader.start();

            boolean finished = p.waitFor(timeoutMs, java.util.concurrent.TimeUnit.MILLISECONDS);
            if (!finished) {
                p.destroyForcibly();
                return new CheckResult(false, withTail(sb, "自检超时（" + timeoutMs + "ms）"));
            }
            reader.join(2000);
            int code = p.exitValue();
            synchronized (sb) {
                boolean ok = code == 0 && sb.indexOf("LogAgent") >= 0;
                return new CheckResult(ok, ok ? sb.toString() : withTail(sb, "exit=" + code));
            }
        } catch (Exception ex) {
            return new CheckResult(false, "启动异常：" + ex.getClass().getSimpleName() + "：" + ex.getMessage() + "\n" + sb);
        }
    }

    private static String withTail(StringBuilder sb, String tail) {
        synchronized (sb) {
            if (sb.length() == 0) return tail;
            return sb + "\n[自检] " + tail;
        }
    }

    // ───────────────────────── 解压 ─────────────────────────
    /** 解压进度：已写入字节数（UI 用它除以预期总量算百分比）。 */
    interface Progress { void report(long bytesWritten); }

    /**
     * 解压 assets 里的 .tar.gz 到 {@code dest}。
     * 两个包都是我们自己打的（GNU tar + gzip），条目数少、无稀疏文件，流式解足够。
     */
    private void extractAsset(String assetName, File dest, Progress progress) throws IOException {
        dest.mkdirs();
        try (InputStream raw = ctx.getAssets().open(assetName);
             GZIPInputStream gz = new GZIPInputStream(raw, 1 << 16)) {
            TarExtractor.extract(gz, dest, progress::report);
        }
    }
}
