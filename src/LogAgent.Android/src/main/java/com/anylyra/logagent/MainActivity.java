package com.anylyra.logagent;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

/**
 * 最小控制台：状态 + 启停 + 日志 + config.json 编辑 + 反向 WS 地址。
 *
 * <p>刻意不做的事：内嵌 WebView（Headless 没有 Web UI）、Provider 表单、NapCat 托管
 * （手机上跑 NapCat 不现实，NapCat 应在另一台机器上连入 6701）。
 */
public class MainActivity extends Activity {

    private TextView statusText;
    private TextView endpointText;
    private TextView logText;
    private EditText configText;
    private Button startBtn;
    private Button stopBtn;
    private ScrollView logScroll;
    private final Handler ui = new Handler(Looper.getMainLooper());

    private final ContainerService.Listener listener = new ContainerService.Listener() {
        @Override public void onLog(String line) {
            ui.post(() -> {
                logText.append(line + "\n");
                logScroll.post(() -> logScroll.fullScroll(View.FOCUS_DOWN));
            });
        }

        @Override public void onState(String state, String engine) {
            ui.post(() -> refreshStatus(state, engine));
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(buildUi());
        ContainerService.setListener(listener);

        for (String line : ContainerService.snapshot()) logText.append(line + "\n");
        refreshStatus(ContainerService.isRunning() ? "运行中" : "已停止", ContainerService.engine());
        loadConfig();
    }

    @Override
    protected void onDestroy() {
        ContainerService.setListener(null);
        super.onDestroy();
    }

    // ───────────────────────── UI ─────────────────────────

    private android.view.View buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(16);
        root.setPadding(pad, pad, pad, pad);

        statusText = new TextView(this);
        statusText.setTextSize(18);
        root.addView(statusText);

        endpointText = new TextView(this);
        endpointText.setTextSize(12);
        endpointText.setPadding(0, dp(4), 0, dp(8));
        root.addView(endpointText);

        LinearLayout buttons = new LinearLayout(this);
        buttons.setOrientation(LinearLayout.HORIZONTAL);

        startBtn = new Button(this);
        startBtn.setText("启动");
        startBtn.setOnClickListener(v -> startContainer());
        buttons.addView(startBtn, weight());

        stopBtn = new Button(this);
        stopBtn.setText("停止");
        stopBtn.setOnClickListener(v -> stopContainer());
        buttons.addView(stopBtn, weight());

        Button saveBtn = new Button(this);
        saveBtn.setText("保存配置");
        saveBtn.setOnClickListener(v -> saveConfig());
        buttons.addView(saveBtn, weight());

        root.addView(buttons);

        TextView cfgLabel = new TextView(this);
        cfgLabel.setText("config.json（Providers / OneBot11 见 docs/05）");
        cfgLabel.setPadding(0, dp(12), 0, dp(4));
        root.addView(cfgLabel);

        configText = new EditText(this);
        configText.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        configText.setTypeface(android.graphics.Typeface.MONOSPACE);
        configText.setTextSize(11);
        configText.setMinLines(6);
        configText.setMaxLines(10);
        root.addView(configText, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 0.45f));

        TextView logLabel = new TextView(this);
        logLabel.setText("容器日志");
        logLabel.setPadding(0, dp(12), 0, dp(4));
        root.addView(logLabel);

        logScroll = new ScrollView(this);
        logText = new TextView(this);
        logText.setTextSize(10);
        logText.setTypeface(android.graphics.Typeface.MONOSPACE);
        logScroll.addView(logText);
        root.addView(logScroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 0.55f));

        return root;
    }

    private LinearLayout.LayoutParams weight() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        lp.setMargins(dp(2), 0, dp(2), 0);
        return lp;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    private void refreshStatus(String state, String engine) {
        boolean running = ContainerService.isRunning();
        statusText.setText("状态：" + state + (running ? "（引擎 " + engine + "）" : ""));
        endpointText.setText("反向 WS：ws://" + localIp() + ":6701/onebot/v11/ws"
                + "\n在 NapCat 里新建反向 WebSocket Client 填此地址（Token 与 config.json 一致）");
        startBtn.setEnabled(!running);
        stopBtn.setEnabled(running);
    }

    /** 取本机局域网 IPv4（取不到就显示 127.0.0.1，用户自己知道要换）。 */
    private String localIp() {
        try {
            java.util.Enumeration<java.net.NetworkInterface> ifs = java.net.NetworkInterface.getNetworkInterfaces();
            while (ifs.hasMoreElements()) {
                java.net.NetworkInterface ni = ifs.nextElement();
                if (!ni.isUp() || ni.isLoopback()) continue;
                java.util.Enumeration<java.net.InetAddress> addrs = ni.getInetAddresses();
                while (addrs.hasMoreElements()) {
                    java.net.InetAddress a = addrs.nextElement();
                    if (a instanceof java.net.Inet4Address && !a.isLoopbackAddress()) {
                        return a.getHostAddress();
                    }
                }
            }
        } catch (Exception ignored) {
            // 拿不到就退回回环地址
        }
        return "127.0.0.1";
    }

    // ───────────────────────── 动作 ─────────────────────────

    private void startContainer() {
        Intent i = new Intent(this, ContainerService.class);
        startForegroundService(i);
    }

    private void stopContainer() {
        Intent i = new Intent(this, ContainerService.class).setAction("stop");
        startService(i);
    }

    private File configFile() {
        return new File(new File(new File(getFilesDir(), "container"), "config"), "config.json");
    }

    private void loadConfig() {
        File f = configFile();
        if (!f.exists()) {
            configText.setText("{\n  \"Providers\": [],\n  \"OneBot11\": { \"Enabled\": true, \"Host\": \"0.0.0.0\", \"Port\": 6701, \"Path\": \"/onebot/v11/ws\", \"Token\": \"\" }\n}\n");
            return;
        }
        try {
            configText.setText(new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8));
        } catch (Exception ex) {
            configText.setText("读取失败：" + ex.getMessage());
        }
    }

    private void saveConfig() {
        File f = configFile();
        try {
            f.getParentFile().mkdirs();
            Files.write(f.toPath(), configText.getText().toString().getBytes(StandardCharsets.UTF_8));
            Toast.makeText(this, "已保存；重启容器后生效", Toast.LENGTH_SHORT).show();
        } catch (Exception ex) {
            Toast.makeText(this, "保存失败：" + ex.getMessage(), Toast.LENGTH_LONG).show();
        }
    }
}
