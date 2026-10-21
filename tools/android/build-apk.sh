#!/usr/bin/env bash
# 用 aapt2 + javac + d8 + apksigner 手工打 Android APK（不需要 Android SDK / Gradle）。
#
# 为什么不用 Gradle：本机是 Termux，装 gradle(165MB)+AGP+完整 SDK 代价太大；
# 而这个 APK 无 AndroidX、无 Kotlin、无资源变体，纯框架 API，手工链路足够且可复现。
#
# 前置：
#   pkg install -y aapt2 d8 apksigner openjdk-21 zip
#   一个 android.jar（aapt2 link 与 javac -classpath 需要）：
#     curl -L -o ~/android.jar \
#       https://raw.githubusercontent.com/Sable/android-platforms/master/android-34/android.jar
#
# 用法：
#   tools/android/build-apk.sh [--unsigned]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJ="$ROOT/src/LogAgent.Android"
BUILD="$PROJ/build"
DIST="$ROOT/dist"
ANDROID_JAR="${ANDROID_JAR:-$HOME/android.jar}"
KEYSTORE="${LOGAGENT_KEYSTORE:-$HOME/.android/logagent-release.jks}"
UNSIGNED=0
[ "${1:-}" = "--unsigned" ] && UNSIGNED=1

die() { echo "错误：$*" >&2; exit 1; }

for t in aapt2 javac d8 apksigner zip; do
  command -v "$t" >/dev/null || die "缺少 $t（pkg install -y aapt2 d8 apksigner openjdk-21 zip）"
done
[ -f "$ANDROID_JAR" ] || die "缺少 android.jar：$ANDROID_JAR（见脚本头部注释）"
[ -f "$PROJ/assets/rootfs.tar.gz" ] || die "缺少 assets/rootfs.tar.gz：先跑 tools/android/build-rootfs.sh"
[ -f "$PROJ/assets/logagent.tar.gz" ] || die "缺少 assets/logagent.tar.gz：先跑 tools/android/build-payload.sh"

rm -rf "$BUILD"
mkdir -p "$BUILD/gen" "$BUILD/classes" "$BUILD/dex" "$DIST"

echo "==> 1/6 aapt2 compile（资源）"
if [ -d "$PROJ/src/main/res" ]; then
  aapt2 compile --dir "$PROJ/src/main/res" -o "$BUILD/res.zip"
  # 注意：编译产物是"输入"而不是"overlay"，必须当位置参数传给 link。
  # 用 -R 会走 overlay 语义，对非 overlay 包报 "does not override an existing resource"。
  RES_ARG=("$BUILD/res.zip")
else
  RES_ARG=()
fi

echo "==> 2/6 aapt2 link（清单 + 资源 + assets）"
aapt2 link \
  -o "$BUILD/base.apk" \
  -I "$ANDROID_JAR" \
  --manifest "$PROJ/src/main/AndroidManifest.xml" \
  -A "$PROJ/assets" \
  --java "$BUILD/gen" \
  --min-sdk-version 30 --target-sdk-version 34 \
  "${RES_ARG[@]}"

echo "==> 3/6 javac"
# --release 8：Android 的 dexer 对 8 支持最稳；不要同时用 -bootclasspath（互斥）
find "$BUILD/gen" "$PROJ/src/main/java" -name '*.java' > "$BUILD/sources.txt"
# 注意：这里不能吞掉 javac 的退出码。早期版本用 `| grep ... || true`，
# 结果编译报错仍然继续打包，产出了一个 class 不全的 APK。
if ! javac --release 8 \
      -classpath "$ANDROID_JAR" \
      -d "$BUILD/classes" \
      @"$BUILD/sources.txt" > "$BUILD/javac.log" 2>&1; then
  cat "$BUILD/javac.log" >&2
  die "javac 失败（见上面的编译错误）"
fi
# 只过滤掉"注:"这类信息行，警告保留给用户看
grep -v "^注: " "$BUILD/javac.log" || true
[ -d "$BUILD/classes/com" ] || die "javac 没有产出 class"

echo "==> 4/6 d8（dex）"
find "$BUILD/classes" -name '*.class' > "$BUILD/classes.txt"
d8 --min-api 30 --lib "$ANDROID_JAR" --output "$BUILD/dex" @"$BUILD/classes.txt"

echo "==> 5/6 组装未签名 APK"
cp "$BUILD/base.apk" "$BUILD/unsigned.apk"
# jniLibs 由 aapt2 link 不会带上（那是 Gradle 的活），这里手工塞进 lib/arm64-v8a/
for so in "$PROJ"/jniLibs/arm64-v8a/*.so; do
  (cd "$PROJ/jniLibs/arm64-v8a" && zip -q "$BUILD/unsigned.apk" -j "$(basename "$so")" >/dev/null)
done
# 上面 -j 会把文件放在 zip 根，必须改到 lib/arm64-v8a/ 下
python3 - "$BUILD/unsigned.apk" "$PROJ/jniLibs/arm64-v8a" <<'PY'
import sys, zipfile, os, shutil
apk, libdir = sys.argv[1], sys.argv[2]
tmp = apk + ".tmp"
with zipfile.ZipFile(apk) as src, zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as dst:
    for item in src.infolist():
        name = item.filename
        if os.path.basename(name) in os.listdir(libdir) and "/" not in name:
            continue  # 丢掉刚才 -j 塞在根目录的那份
        dst.writestr(item, src.read(name))
    for so in sorted(os.listdir(libdir)):
        if not so.endswith(".so"):
            continue
        # native lib 必须 STORED（不压缩）：系统按 mmap 直接映射，压缩会导致装不上
        with open(os.path.join(libdir, so), "rb") as f:
            info = zipfile.ZipInfo("lib/arm64-v8a/" + so)
            info.compress_type = zipfile.ZIP_STORED
            info.external_attr = (0o100755 << 16)
            dst.writestr(info, f.read())
shutil.move(tmp, apk)
print("    已装入 native 库：%d 个" % len([f for f in os.listdir(libdir) if f.endswith('.so')]))
PY
(cd "$BUILD/dex" && zip -q "$BUILD/unsigned.apk" classes*.dex)
echo "    未签名 APK：$(du -h "$BUILD/unsigned.apk" | cut -f1)"

VERSION="$(grep -oE 'android:versionName="[^"]+"' "$PROJ/src/main/AndroidManifest.xml" | head -1 | cut -d'"' -f2)"
OUT_APK="$DIST/LogAgent-android-arm64-${VERSION}.apk"

if [ "$UNSIGNED" -eq 1 ]; then
  cp "$BUILD/unsigned.apk" "$OUT_APK"
  echo "==> 6/6 跳过签名"
else
  echo "==> 6/6 签名"
  if [ ! -f "$KEYSTORE" ]; then
    die "缺少 keystore：$KEYSTORE
  生成（一次性，请备份并妥善保管，丢了就无法升级覆盖安装）：
    keytool -genkeypair -v -keystore $KEYSTORE -alias logagent \\
      -keyalg RSA -keysize 4096 -validity 10000 -storetype PKCS12 \\
      -dname 'CN=LogAgent, O=AnyLyraLabs, C=CN'"
  fi
  : "${LOGAGENT_KEYSTORE_PASSWORD:?需要环境变量 LOGAGENT_KEYSTORE_PASSWORD}"
  # 显式开 v2+v3：minSdk 30 本可只靠 v3，但 v2 覆盖更广的校验器
  # （部分 ROM 的安装器 / 第三方校验工具只认 v2）。
  apksigner sign \
    --ks "$KEYSTORE" \
    --ks-pass "env:LOGAGENT_KEYSTORE_PASSWORD" \
    --key-pass "env:LOGAGENT_KEYSTORE_PASSWORD" \
    --v1-signing-enabled false \
    --v2-signing-enabled true \
    --v3-signing-enabled true \
    --out "$OUT_APK" \
    "$BUILD/unsigned.apk"
  apksigner verify --min-sdk-version 30 --verbose "$OUT_APK" | head -8
fi

sha256sum "$OUT_APK" | sed "s|$DIST/||" > "$OUT_APK.sha256"
echo
echo "产出：$OUT_APK  $(du -h "$OUT_APK" | cut -f1)"
echo "校验：$(cat "$OUT_APK.sha256")"
