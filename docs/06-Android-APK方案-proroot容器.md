# Android APK 方案：proroot 容器 + .NET 10 无头宿主

> 版本：v1.0（2026-10-20）｜ 状态：**方案已在本机（Android 14 / aarch64 / Termux，未 root）全链路验证**
> 适用：把 `LogAgent.Headless serve` 装进 APK，在手机上常驻跑 QQ 群助手

---

## 1. 结论先行

| 问题 | 答案 | 验证方式 |
|---|---|---|
| 能不能在手机 APK 里跑 .NET 10 无头服务？ | **能** | proroot + Ubuntu 24.04 arm64 rootfs 里 `selftest` 通过 ✅ |
| 需不需要 .NET 的 Android 版（`linux-bionic`）？ | **不需要** | 用官方 `linux-arm64`（glibc）产物，在容器内运行 |
| 本机能不能编出这个 APK？ | **能，且已验证** | aapt2 + javac + d8 + apksigner + 25MB android.jar，无需 Android SDK |
| 本机能不能编 .NET 产物？ | **能**（推翻 AGENTS.md §9 的旧结论） | 容器内 .NET SDK 10.0.100 直接 `dotnet publish -r linux-arm64` |
| APK 有没有打出来？ | **打出来了，v2+v3 签名通过** | `dist/LogAgent-android-arm64-0.9.0-android.apk`，70MB |
| 引擎选谁？ | **proroot 主 + proot 自动兜底** | 两者都实测跑通同一份产物 |

**硬约束（实测得出，不是推测）**：

1. **`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` 是必选项**，不是优化项。原因见 §4.3。
2. **必须 `-b /proc:/proc`**，否则 single-file 的 native 库解压路径解析失败 → `Failed to load JIT compiler`。
3. **`--link2symlink` 必须带**：Android app 私有目录禁 `link(2)`（实测 `ln` 报 `Cross-device link` / `EPERM`），不带会导致 `dpkg` 装机失败。
4. **可执行文件必须在 `nativeLibraryDir`**，不能放 `filesDir`（Android 10+ W^X + SELinux `app_data_file` 禁 exec）。

---

## 2. 为什么是「容器」而不是「Android 版 .NET」

.NET 没有官方的 Android 宿主（`linux-bionic` 是 Termux 社区打包的，且**没有安装 `linux-arm64` 的 runtime pack**，`--self-contained` 会失败或静默降级）。

所以形态必须是：**APK（Java/Java 壳）→ 启动原生 launcher（`libproroot.so`）→ 进入 glibc rootfs → 跑 `LogAgent.Headless`**。

这与 DSH-APP/DSHA 的架构同源（参考：[DSHA](https://github.com/DSH-APP/DSHA)），区别在于 DSHA 跑 Node，我们跑 .NET。

---

## 3. 目标架构

```
┌───────────────────── APK (com.anylyra.logagent) ─────────────────────┐
│  Java 最小控制台 (MainActivity)                                       │
│    · 容器状态 / 启停 / 日志滚动 / config.json 编辑 / 6701 地址        │
│                                                                      │
│  lib/arm64-v8a/  ← nativeLibraryDir（唯一允许 exec 的位置）           │
│    libproroot.so · -runtime.so · -linker.so · -bridge.so · -stub-loader.so
│    libprootloader · libtalloc.so.2 · libandroid-shmem.so   ← proot 兜底
│                                                                      │
│  assets/                                                             │
│    rootfs.tar.gz     Ubuntu 24.04 base（无 ICU，~28MB）              │
│    logagent.tar.gz   单文件 LogAgent.Headless + libe_sqlite3.so       │
│                                                                      │
│  filesDir/                                                           │
│    rootfs/            解压一次，之后只读                             │
│    app/               LogAgent.Headless                              │
│    config/config.json ← LOGAGENT_CONFIG_DIR                          │
│    tmp/               BXROOT/PROROOT_TMP_DIR                         │
└──────────────────────────────────────────────────────────────────────┘
                              ↓ 启动
   PROROOT_TMP_DIR=… libproroot.so -r rootfs -0 --link2symlink \
     -b /proc:/proc -b /dev:/dev -b app:/app -w /root \
     /bin/sh -c 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 TZ=Asia/Shanghai \
                 HOME=/root /app/LogAgent.Headless serve'
```

---

## 4. 实测证据

环境：Android 14（SDK 36）/ aarch64 / 8 核 / 4KB 页 / Termux 无 root。

### 4.1 基础能力

| 测试 | 结果 |
|---|---|
| proroot v1.2.8 启动 Ubuntu 24.04 base | ✅ `uname -a` 正常，`id` = `uid=0(root)` |
| `apt-get update` + `apt-get install` | ✅ 装过 `libicu74` / `gcc` / `python3` / `curl` |
| 容器内 DNS | ✅ 需 `nameserver 127.0.0.1`（Android 的 DNS 走本地 resolver；写 `8.8.8.8` 会超时） |
| 容器内 HTTPS | ✅ `curl https://api.deepseek.com` → 401（TLS 握手成功） |
| 端口回连宿主 | ✅ 容器内 `serve` 监听 6701，Termux 侧 `connect 127.0.0.1:6701` 成功 |

### 4.2 .NET 运行时

| 测试 | 结果 |
|---|---|
| `dotnet --info`（10.0.13 官方 linux-arm64） | ✅ |
| **托管代码执行**（`dotnet <app>.dll`） | ✅ 但需满足 §4.3 两个条件 |
| **.NET SDK 10.0.100 编译** | ✅ 容器内 `dotnet build` / `dotnet publish -r linux-arm64` 成功 |
| **LogAgent.Headless 实编** | ✅ 单文件 111MB（arm64），容器内 `--help` / `config path` / `doctor` / `selftest` 全通过 |
| `serve` 常驻 + 优雅退出 | ✅ 收到 SIGTERM 后「切段会话 → 停止连接 → 退出」 |
| `selftest`（内嵌假模型 + 沙箱 + 路由） | ✅ 连续 4 次稳定通过 |

### 4.3 两个必须解决的坑（关键）

**坑 1：`ldso_runtime dlsym: symbol 'ucurr_forLocale_74' not found`**

proroot 的 `ldso_runtime_dlsym` 对「间接依赖里的符号」解析不完整：`libSystem.Globalization.Native.so` 依赖 `libicui18n.so.74`，而 `ucurr_forLocale_74` 实际定义在它的依赖 `libicuuc.so.74` 里。glibc 能解析，proroot 的 stub 不能。

**解决**：`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`（绕开 ICU）。副作用是文化相关的格式化退化为 invariant，对本项目无影响（日志里全是自己的 `DateTime.ToString("HH:mm")` 和英文日志）。

**坑 2：`Fatal error. Failed to load JIT compiler`**

single-file 模式下运行时会把 native 库解压到临时目录，但若容器没有 `/proc`，解压路径解析失败。

**解决**：**必须** `-b /proc:/proc`（`-b /dev:/dev` 建议一并带上，否则偶发 `cryptographic operation` 错误）。

> 验证矩阵（同一份产物）：
>
> | 参数 | 结果 |
> |---|---|
> | 无 ICU + 无 invariant | ❌ `Couldn't find a valid ICU package` |
> | 无 ICU + invariant | ✅ |
> | 有 ICU + invariant + 无 `/proc` | ❌ `Failed to load JIT compiler` |
> | 有 ICU + invariant + `/proc` | ✅ |

---

## 5. 引擎策略：proroot 主 + proot 兜底

### 5.1 为什么不是纯 proroot

proroot 是**闭源专有**软件（[coderredlab/proroot](https://github.com/coderredlab/proroot)，作者已转向 proroom，v1.2.8 停在 2026-06）。.NET 的 JIT 加载路径缺陷我们改不了，只能靠上面的两个参数绕过；一旦遇到新机型/新版本 .NET 出问题，**没有修复路径**。

### 5.2 为什么不是纯 proot

proot 是 GPLv2（[termux/proot](https://github.com/termux/proot)），随包分发需要提供对应源码；且 ptrace 开销在「沙箱 `run_shell` + SQLite」这种高频场景上明显更慢（DSHA 记录 proroot 快 ~58%，虽然那是 Node 场景）。

### 5.3 兜底设计（采纳）

```
启动 → 尝试 proroot → 若 selftest 失败（固定错误串或超时）→ 标记 proroot 不可用
     → 本次及之后都用 proot → UI 显示「已降级为 proot」
```

判定只认**明确的失败**（`[proroot]` + `SIGSEGV` / `Failed to load JIT` / 超时 15s），不做「结果未知就重试」——避免业务已开始写入时重放。

proot 兜底的完整性已验证：同一份产物在 proot 下 `selftest` ✅、`serve` 6701 可达 ✅、TLS 到 DeepSeek 返回 401 ✅。

组件体积：proot 4 件套（`proot` 255KB + `loader` 18KB + `libtalloc.so.2` 33KB + `libandroid-shmem.so` 14KB）= **约 320KB**，代价可忽略。

---

## 6. 构建通道：本机 + CI 双通道

> 选型理由：本机做快速迭代，CI 做可复现正式包；两者共用同一份组装脚本。

### 6.1 本机通道（Termux，已验证）

**前置**（一次性，约 700MB 磁盘）：

```bash
pkg install -y proot aapt2 d8 apksigner openjdk-21 zip
# 25MB android.jar（aapt2 link 需要）
curl -L -o ~/android.jar \
  https://raw.githubusercontent.com/Sable/android-platforms/master/android-34/android.jar
```

**准备 Ubuntu rootfs**（一次性，可缓存在 `~/.cache/logagent-android/`）：

```bash
# 官方 base（30MB）
curl -L -o ubuntu-base.tar.gz \
  https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.3-base-arm64.tar.gz
mkdir -p rootfs && tar -xzf ubuntu-base.tar.gz -C rootfs

# 补 CA 证书（TLS 必需）+ tzdata 的 Asia/Shanghai（可选）
# 注意：容器里 apt 装 ca-certificates 会因 dpkg 的 link(2) 失败，
# 直接从宿主机的已验证 rootfs 拷进即可（也可在容器内 --link2symlink 后手动 dpkg -i）
cp /path/to/verified/ca-certificates.crt rootfs/etc/ssl/certs/
cp -a /path/to/verified/zoneinfo/Asia rootfs/usr/share/zoneinfo/

# 精简（关键：清缓存能从 224MB 降到 108MB）
rm -rf rootfs/var/cache/apt/* rootfs/var/lib/apt/lists/* rootfs/var/log/*

# 打包
tar -C rootfs -cf - --exclude=./proc --exclude=./sys --exclude=./dev . | gzip -9 > rootfs.tar.gz
# 实测：108MB 目录 → 28MB tar.gz
```

> **必须用 `-r` 解压而不是直接拷**：Android 的 app 私有目录不支持硬链接，`ubuntu-base` 里有 2 个硬链接（`perl5.38.2`、`uncompress`），直接 `tar -x` 会报 `Cannot hard link`。这两条失败**不影响运行**（实测 selftest 正常），但会让 `apt-get install` 失败。容器内操作用 `--link2symlink` 即可。

**编译 .NET 产物**（容器内）：

```bash
cd LogAgent-for-QQ
PROOT_TMP_DIR=$HOME/.cache/la/proot-tmp \
PROOT_LOADER=$HOME/.cache/la/prootloader \
LD_LIBRARY_PATH=$HOME/.cache/la/lib \
env -u LD_PRELOAD proot -r ~/.cache/la/rootfs -0 --link2symlink \
  -b /proc -b /dev \
  -b ~/.cache/la/sdk:/opt/sdk -b $PWD:/work \
  -w / /bin/sh -c '
    export PATH=/usr/local/bin:/usr/bin:/bin \
           DOTNET_ROOT=/opt/sdk HOME=/root TMPDIR=/tmp \
           DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
    cd /work && /opt/sdk/dotnet publish src/LogAgent.Headless/LogAgent.Headless.csproj \
      -c Release -r linux-arm64 --self-contained true \
      -p:PublishSingleFile=true -p:InvariantGlobalization=true \
      -o /work/publish/android-arm64'
```

实测耗时：首次约 13 分钟（含 SDK 冷启动 + NuGet 还原），后续增量约 2 分钟。

**打 APK**：

```bash
# 1) 编译资源
aapt2 compile --dir app/src/main/res -o build/res.zip
# 2) 链接
aapt2 link -o build/base.apk -I ~/android.jar \
  --manifest app/src/main/AndroidManifest.xml \
  -A app/src/main/assets -R build/res.zip --java build/gen
# 3) 编译 Java
javac --release 8 -classpath ~/android.jar -d build/classes $(find build/gen app/src/main/java -name '*.java')
# 4) dex
d8 --min-api 30 --lib ~/android.jar --output build/dex $(find build/classes -name '*.class')
# 5) 组装 + 签名
cp build/base.apk build/unsigned.apk
(cd build/dex && zip -q ../unsigned.apk classes.dex)
apksigner sign --ks ~/.android/release.keystore --out LogAgent-android-arm64.apk build/unsigned.apk
apksigner verify --min-sdk-version 30 LogAgent-android-arm64.apk
```

> `aapt2` 产出的 `resources.arsc` 天然是 `STORED` + 4 字节对齐，**不需要 zipalign**（实测 948→948 偏移不变）。

### 6.2 CI 通道

新增 `.github/workflows/android-apk.yml`（**不改动现有 `ci.yml` / `build-artifacts.yml`**）：

```yaml
name: Android APK
on:
  push:
    tags: ['v*']
  workflow_dispatch:

jobs:
  apk:
    runs-on: ubuntu-24.04-arm        # 原生 arm64，免 qemu
    steps:
      - uses: actions/checkout@v4

      - name: 构建 rootfs（ubuntu-base + CA + 精简）
        run: bash tools/android/build-rootfs.sh
        # 下载 ubuntu-base 30MB → 解压 → 注入 CA/tzdata → 精减 → gzip 到 assets/

      - name: 发布 Linux arm64 单文件
        run: |
          dotnet publish src/LogAgent.Headless/LogAgent.Headless.csproj \
            -c Release -r linux-arm64 --self-contained true \
            -p:PublishSingleFile=true -p:InvariantGlobalization=true \
            -p:DebugType=none -o build/app

      - name: 打包 assets
        run: bash tools/android/build-assets.sh   # app/ → assets/logagent.tar.gz

      - name: 安装 Android 构建工具
        run: sudo apt-get install -y aapt2 d8 apksigner default-jdk

      - name: 构建 APK
        run: bash tools/android/build-apk.sh

      - uses: actions/upload-artifact@v4
        with:
          name: logagent-android-arm64
          path: |
            dist/LogAgent-*.apk
            dist/LogAgent-*.apk.sha256
```

**关键选型**：用 `ubuntu-24.04-arm` runner 而不是 x64 + qemu——qemu 下 `dotnet publish` 会慢 10 倍以上，且 AOT/R2R 路径可能与真机不同。

---

## 7. APK 规格

| 项 | 值 | 依据 |
|---|---|---|
| 包名 | `com.anylyra.logagent` | — |
| `minSdk` | 30（Android 11） | `specialUse` 前台服务类型需 API 34；取 30 兼顾覆盖面 |
| `targetSdk` | 34 | 未到 35 可避开更严的 FGS 限制 |
| ABI | `arm64-v8a` | 与 rootfs/.NET 产物一致 |
| 权限 | `INTERNET`、`FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_SPECIAL_USE`、`POST_NOTIFICATIONS` | 常驻 + 网络 |
| 前台服务 | `specialUse` + `PARTIAL_WAKE_LOCK` | 防止 Doze 杀进程 |
| 签名 | 自建 keystore（不入库） | `~/.android/release.keystore` |
| 实测体积 | **70MB**（rootfs 28MB + app 42MB + native 950KB） | 已打出的 APK |

---

## 8. Java 壳要做的最小控制台

单一 Activity，四块：

1. **状态卡**：`未安装 / 运行中（proroot）/ 运行中（proot 兜底）/ 已停止` + 已运行时长
2. **启停按钮**：`startForegroundService` → `ProcessBuilder` 起 `libproroot.so`
3. **日志滚动**：`appendText`，保留最近 2000 行；`proroot` 的 stderr 合并进来
4. **配置**：`config.json` 的文本编辑 + 「保存并重启」；底部显示 `ws://<手机IP>:6701/onebot/v11/ws`

**不做的事**（刻意）：内嵌 WebView（无 Web UI）、文件管理器、Provider 表单、NapCat 托管（手机上跑 NapCat 不现实，NapCat 应在另一台机器上连入）。

---

## 8.5 已产出的东西（可直接检查）

| 路径 | 内容 | 是否入仓库 |
|---|---|---|
| `src/LogAgent.Android/src/main/java/.../ContainerLauncher.java` | 拼 argv、解压 assets、引擎自检 | ✅ |
| `.../ContainerService.java` | 前台服务 + WAKE_LOCK + 日志环形缓冲 | ✅ |
| `.../MainActivity.java` | 状态 / 启停 / 日志滚动 / config 编辑 / WS 地址 | ✅ |
| `.../TarExtractor.java` | 最小 tar 解析（ustar/GNU 长名/PAX/符号链接/硬链接降级） | ✅ |
| `src/LogAgent.Android/jniLibs/arm64-v8a/*.so` | proroot 5 件 + proot 3 件（共 950KB） | ✅ |
| `src/LogAgent.Android/assets/rootfs.tar.gz` | Ubuntu 24.04 base + CA + 精简（28MB） | ❌ 生成物 |
| `src/LogAgent.Android/assets/logagent.tar.gz` | 单文件 LogAgent + libe_sqlite3（42MB） | ❌ 生成物 |
| `src/LogAgent.Android/assets/tools/libtalloc.so.2` | proot 依赖（见下） | ❌ 生成物 |
| `dist/LogAgent-android-arm64-0.9.0-android.apk` | 70MB，v2+v3 已签 | ❌ 生成物 |

**一个容易踩的坑（实测）**：APK 安装时**只解出 `lib*.so` 命名的库**。proot 需要的
`libtalloc.so.2`（按 SONAME 精确命名）不匹配这个模式，装完在 `nativeLibraryDir` 里根本不存在。
所以它只能放 `assets/tools/`，首次启动解到 `filesDir/container/proot/`，再用 `LD_LIBRARY_PATH` 加载。
`libprootloader` 同理，但它的名字（作为文件路径传给 `PROOT_LOADER`）不受 SONAME 约束，
所以直接放 jniLibs、命名为 `libprootloader.so` 即可。

---

## 8.6 首版真机试运行暴露的两个 bug（已修）

第一版装到手机上，日志显示 `[安装] 完成` 之后直接「proroot 自检失败 → proot 自检也失败」。
两个问题都不是环境问题，而是 Java 层的写法错误：

### Bug 1：`String.join(" ", …)` 把 shell 脚本拼成了一行

```java
// 错误写法
String script = String.join(" ", new String[] {
    "export PATH=/usr/local/bin:/usr/bin:/bin",
    "HOME=/root",
    "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
    "printf 'nameserver 127.0.0.1\n' > /etc/resolv.conf",
    "exec /app/LogAgent.Headless --help",
});
```

拼出来的是一整行 `export PATH=… HOME=/root DOTNET_…=1 printf … exec …`，
`export` 会把后面全当成变量名（含非法标识符），后面的 `printf` / `exec` **根本没执行**，
子进程立马退出 → 自检必然失败。

**修法**：改用 `"; "` 分隔，每条语句独立 `export`。已在 Termux 里用真实 proroot/proot
验证同一份脚本串可正常输出 `LogAgent 无头宿主`。

### Bug 2：自检失败时用户看不到原因

原来的 `selfCheck` 只返回 `boolean`，子进程的 stderr 全被丢弃，UI 上只剩"失败"两个字。

**修法**：`selfCheck` 改为返回 `CheckResult { ok, output }`，把容器输出（最多 8KB）带回主进程，
失败时原样缩进打进日志区。这样下次再出问题能直接看到 `Couldn't find a valid ICU package`
或 `Failed to load JIT compiler` 这类原文，不用靠猜。

> 教训：容器方案里「子进程输出」就是唯一的诊断通道，任何包装层吞掉它都会让故障变成黑盒。

### 顺带修掉的图标问题

第一版用了 1×1 的占位 PNG，桌面显示空白。改为从 `src/LogAgent.App/Assets/logo.png`
（256×256）用 ImageMagick 生成五档密度（48/72/96/144/192），已确认打进 APK 的
`res/mipmap-*-v4/ic_launcher.png`。

---

## 9. 风险与未验证项

| 风险 | 说明 | 缓解 |
|---|---|---|
| ~~proot 是 GPLv2~~ | **已解决**：`linux` 分支整体改为 GPLv2 发布，proot/loader 的分发义务自然满足。 | 见 `docs/07-第三方组件与许可证.md` §1.3 |
| **talloc 是 GPLv3，与 GPLv2-only 有兼容性张力** | 本项目 GPLv2-only + talloc GPLv3，严格解释下组合分发有冲突；但二者是独立可执行文件（mere aggregation），实际风险低 | **需你拍板**：①改 GPLv2-or-later；②APK 不带 proot 兜底。详见 `docs/07-第三方组件与许可证.md` §1.2 |
| proroot 闭源且停止更新 | 作者已转向 proroom，issue 无人处理（如 [#16 LinuxQQ ICU 崩溃](https://github.com/coderredlab/proroot/issues/16)，与本方案的 ICU 坑同源） | proot 兜底 + 把 proroot 关在开关后 |
| proroot 的 `--link2symlink` 行为 | 实测未观察到 l2s 目录生成，但 `dpkg` 仍失败——说明 proroot 的 l2s 与 proot 实现不同 | rootfs 预先在 CI 构建好，运行时不需要 apt |
| targetSdk 30+ 的 16KB 页要求 | 设备当前是 4KB 页；实测产物 PT_LOAD 对齐是 65536，proroot 的 5 个 .so 是 16384/65536，**均满足** | 已核对，无需 zipalign |
| 沙箱在 Android 上的默认白名单 | `PosixShell.DefaultWhitelist` 含 `find`/`grep`/`python3` 等，rootfs 里 `python3` **不存在** | 首次启动时 `doctor` 会提示；可在 config 里裁剪 |
| 时区 | ubuntu-base 无 tzdata，`/etc/localtime` 是 UTC | 启动时注入 `TZ=Asia/Shanghai` 并拷 `zoneinfo/Asia`（已验证生效） |
| **未验证**：真机安装 | APK 已构建并签名，但**没有真正 install 过**（本机是 Termux，没有 `pm install` 权限） | P2 阶段装一次，重点看：assets 解压耗时、nativeLibraryDir 能否 exec、前台服务存活 |
| **未验证**：Android 16KB 页真机 | 设备是 4KB 页 | 需在 16KB 页设备上复验 |
| **未验证**：低内存设备 | 本机 7.3GB；容器 + .NET 约需 300-400MB | 需在 4GB 设备上测 |

---

## 10. 落地步骤

| 阶段 | 内容 | 产出 |
|---|---|---|
| **P0** | 本机打通构建链路（rootfs 脚本 + 打包脚本） | `tools/android/*.sh` 可本机复现 |
| **P1** | Java 最小控制台 + APK 组装 | 可安装 APK，UI 能起停容器并看日志 |
| **P2** | 真机验收（截屏 + 日志） | 验收记录写入本文档 |
| **P3** | CI 工作流 + Release 挂载 | tag 出包 |
| **P4** | 首次启动体验（解压进度、向导等价流程） | 首启可用 |

---

## 11. 与 AGENTS.md 的冲突（需同步修订）

`AGENTS.md §9` 写「本机（Android/Termux）**没有** .NET SDK，无法本地编译」——**已被实测推翻**。建议改为：

> 本机可通过 Termux 的 `proot` + Ubuntu 24.04 rootfs + .NET SDK 10.0.100 编译 arm64 产物（首次约 13 分钟）；Win-x64/WPF 仍只能靠 CI。

同时建议新增一节「Android 打包」指向本文档。

---

## 12. 参考

- [coderredlab/proroot](https://github.com/coderredlab/proroot) —— 闭源，零 ptrace 的 proot 替代
- [termux/proot](https://github.com/termux/proot) —— GPLv2，兜底引擎
- [DSH-APP/DSHA](https://github.com/DSH-APP/DSHA) —— 同架构参考（Node 版）
- [qiannianhuanxiang/bxroot](https://github.com/qiannianhuanxiang/bxroot) —— proroot 的 MIT 开源替代（实测在 Termux 下 `execve` 失败，**未采用**；若后续 proroot 彻底不可用，可优先评估）
- [Ubuntu Base 24.04 arm64](https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/)
- [.NET 10 linux-arm64 runtime](https://dotnetcli.azureedge.net/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-arm64.tar.gz)
