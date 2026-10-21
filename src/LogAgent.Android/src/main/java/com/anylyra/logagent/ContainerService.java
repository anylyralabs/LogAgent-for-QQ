package com.anylyra.logagent;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;

/**
 * 前台服务：托管容器进程，保证锁屏/切后台时不被杀。
 *
 * <p>为什么必须是前台服务而不是普通线程：Android 8+ 后台进程随时被回收；
 * 而 LogAgent 是「群里 @ 一下就要回」的常驻服务，被杀了就漏消息。
 *
 * <p>为什么还要 WAKE_LOCK：Doze 会冻结 CPU，QQ 消息到达时我们无法及时响应。
 * 这里只拿 PARTIAL（不点亮屏幕），随服务一起释放。
 */
public class ContainerService extends Service {

    /** UI 订阅日志用的静态回调（单进程应用，比 binder 简单且够用）。 */
    interface Listener {
        void onLog(String line);
        void onState(String state, String engine);
    }

    private static final String CHANNEL_ID = "logagent-runtime";
    private static final int NOTIF_ID = 0x10;
    private static final int MAX_LINES = 2000;

    private static final Deque<String> sLog = new ArrayDeque<>();
    private static volatile Listener sListener;
    private static volatile boolean sRunning;
    private static volatile String sEngine = "-";

    private Process process;
    private PowerManager.WakeLock wakeLock;
    private File logFile;

    static void setListener(Listener l) { sListener = l; }
    static boolean isRunning() { return sRunning; }
    static String engine() { return sEngine; }

    static List<String> snapshot() {
        synchronized (sLog) { return new ArrayList<>(sLog); }
    }

    static void append(String line) {
        synchronized (sLog) {
            sLog.addLast(line);
            while (sLog.size() > MAX_LINES) sLog.removeFirst();
        }
        Listener l = sListener;
        if (l != null) l.onLog(line);
    }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : null;
        if ("stop".equals(action)) {
            stopContainer();
            stopForeground(true);
            stopSelf();
            return START_NOT_STICKY;
        }

        startForeground(NOTIF_ID, buildNotification("启动中…"));
        new Thread(this::startContainer, "logagent-start").start();
        // 不 sticky：进程被系统杀掉后不要自动重启（用户可能就是想停它）
        return START_NOT_STICKY;
    }

    private void startContainer() {
        if (sRunning) return;
        ContainerLauncher launcher = new ContainerLauncher(this);
        try {
            if (!launcher.isInstalled()) {
                append("[安装] 首次启动，解压环境（约 150MB，需要几十秒）…");
                launcher.install(bytes -> append("[安装] 已解压 " + (bytes >> 20) + "MB"));
                append("[安装] 完成");
            }

            // 先用 --help 快速探一次：能把帮助打出来说明 JIT/loader 都正常。
            // 失败时把容器输出原样打到日志，否则用户只能看到"失败"两个字，没法定位。
            ContainerLauncher.CheckResult probe = launcher.selfCheck(false, 40_000);
            boolean useFallback = !probe.ok;
            if (useFallback) {
                append("[引擎] proroot 自检失败，原因是：");
                append(indent(probe.output));
                ContainerLauncher.CheckResult fallback = launcher.selfCheck(true, 40_000);
                if (!fallback.ok) {
                    append("[引擎] proot 自检也失败，原因是：");
                    append(indent(fallback.output));
                    append("[引擎] 两种引擎都不可用，无法启动");
                    publishState("启动失败", "-");
                    return;
                }
                append("[引擎] 已降级为 proot");
            } else {
                append("[引擎] proroot 就绪");
            }

            ContainerLauncher.Engine engine = launcher.build("serve", useFallback);
            sEngine = engine.id;
            logFile = new File(launcher.configDir(), "container.log");
            ProcessBuilder pb = new ProcessBuilder(engine.argv).redirectErrorStream(true);
            launcher.applyHostEnvironment(pb);
            process = pb.start();
            sRunning = true;
            publishState("运行中", engine.id);
            updateNotification("运行中（" + engine.id + "）");

            acquireWakeLock();

            try (BufferedReader r = new BufferedReader(
                    new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = r.readLine()) != null) {
                    append(line);
                }
            }

            int code = process.waitFor();
            append("[退出] 容器进程结束，exit=" + code);
        } catch (Exception ex) {
            append("[错误] " + ex.getClass().getSimpleName() + "：" + ex.getMessage());
        } finally {
            sRunning = false;
            sEngine = "-";
            publishState("已停止", "-");
            releaseWakeLock();
            process = null;
        }
    }

    /** 把多行输出缩进后再进日志区，避免和正常日志混在一起。 */
    private static String indent(String text) {
        if (text == null || text.isEmpty()) return "    （无输出）";
        StringBuilder sb = new StringBuilder();
        for (String line : text.split("\n")) sb.append("    ").append(line).append('\n');
        return sb.toString().trim();
    }

    private void stopContainer() {
        Process p = process;
        if (p != null) {
            // 容器内 serve 注册了 SIGTERM 处理器（会切段会话再退出），
            // 先礼后兵：destroy 发 SIGTERM，5 秒不退再强杀。
            p.destroy();
            try {
                if (!p.waitFor(5, java.util.concurrent.TimeUnit.SECONDS)) p.destroyForcibly();
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
                p.destroyForcibly();
            }
        }
        sRunning = false;
        publishState("已停止", "-");
    }

    private void publishState(String state, String engine) {
        sEngine = engine;
        Listener l = sListener;
        if (l != null) l.onState(state, engine);
        updateNotification(state);
    }

    // ───────────────────────── 通知 / 唤醒锁 ─────────────────────────

    private void acquireWakeLock() {
        if (wakeLock != null && wakeLock.isHeld()) return;
        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (pm == null) return;
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "LogAgent:runtime");
        wakeLock.setReferenceCounted(false);
        wakeLock.acquire();
    }

    private void releaseWakeLock() {
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        wakeLock = null;
    }

    private Notification buildNotification(String text) {
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null && nm.getNotificationChannel(CHANNEL_ID) == null) {
            NotificationChannel ch = new NotificationChannel(
                    CHANNEL_ID, "LogAgent 运行状态", NotificationManager.IMPORTANCE_LOW);
            ch.setDescription("容器运行状态与停止入口");
            nm.createNotificationChannel(ch);
        }

        Intent open = new Intent(this, MainActivity.class);
        PendingIntent content = PendingIntent.getActivity(this, 0, open,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);

        Intent stop = new Intent(this, ContainerService.class).setAction("stop");
        PendingIntent stopPi = PendingIntent.getService(this, 1, stop,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);

        return new Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("LogAgent")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_sys_download_done)
                .setContentIntent(content)
                .addAction(new Notification.Action.Builder(
                        null, "停止", stopPi).build())
                .setOngoing(true)
                .build();
    }

    private void updateNotification(String text) {
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) nm.notify(NOTIF_ID, buildNotification(text));
    }

    @Override
    public void onDestroy() {
        stopContainer();
        releaseWakeLock();
        sListener = null;
        super.onDestroy();
    }
}
