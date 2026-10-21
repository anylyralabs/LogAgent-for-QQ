#!/usr/bin/env bash
# 构建 Android 版 LogAgent 的 .NET 负载（arm64 单文件），并打进 APK 的 assets。
#
# 前提：本机已按 docs/06-Android-APK方案-proroot容器.md §6.1 准备好
#   - proot（pkg install proot）与已验证的 Ubuntu rootfs（rootfs.tar.gz）
#   - .NET SDK linux-arm64 解包在 $LOGAGENT_ANDROID_CACHE/dotnet-sdk
#
# 用法：
#   tools/android/build-payload.sh                # 只产出 app/*.tar.gz
#   tools/android/build-payload.sh --skip-publish  # 复用已有 publish/ 产物
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CACHE="${LOGAGENT_ANDROID_CACHE:-$HOME/.cache/logagent-android}"
SDK="$CACHE/dotnet-sdk"
WORK="$CACHE/rootfs-work"
PROOT_TMP="$CACHE/proot-tmp"
OUT="$ROOT/src/LogAgent.Android/assets"
SKIP_PUBLISH=0
[ "${1:-}" = "--skip-publish" ] && SKIP_PUBLISH=1

die() { echo "错误：$*" >&2; exit 1; }

command -v proot >/dev/null || die "缺少 proot：pkg install proot"
[ -d "$SDK/sdk" ]   || die "缺少 .NET SDK：把 dotnet-sdk-10.0.xxx-linux-arm64.tar.gz 解到 $SDK"
[ -d "$WORK/usr" ]  || die "缺少 rootfs：先跑 tools/android/build-rootfs.sh"

mkdir -p "$PROOT_TMP"

# proot 自带 loader 在 $PREFIX/libexec/proot/，但 Android 不允许从 filesDir exec；
# 这里把 loader/talloc/shmem 拷到缓存目录并用 PROOT_LOADER/LD_LIBRARY_PATH 指过去，
# 模拟 APK 里 nativeLibraryDir 的形态（已验证等价）。
PROOT_LIB="$CACHE/proot-lib"
if [ ! -f "$PROOT_LIB/libprootloader" ]; then
  mkdir -p "$PROOT_LIB"
  cp "$(command -v proot)" "$PROOT_LIB/proot"
  cp "${PREFIX:-/usr}/libexec/proot/loader" "$PROOT_LIB/libprootloader" 2>/dev/null \
    || cp /usr/libexec/proot/loader "$PROOT_LIB/libprootloader"
  cp "${PREFIX:-/usr}/lib/libtalloc.so.2" "$PROOT_LIB/" 2>/dev/null || true
  cp "${PREFIX:-/usr}/lib/libandroid-shmem.so" "$PROOT_LIB/" 2>/dev/null || true
  chmod +x "$PROOT_LIB/proot" "$PROOT_LIB/libprootloader"
fi

# 在容器内跑命令的统一入口。
# 注意三个必须项（都是实测得出）：
#   -b /proc  —— 缺了 .NET 会报 Failed to load JIT compiler
#   -b /dev   —— 缺了偶发 cryptographic operation 失败
#   --link2symlink —— Android 目录禁 link(2)
in_container() {
  env -u LD_PRELOAD \
      PROOT_TMP_DIR="$PROOT_TMP" \
      PROOT_NO_SECCOMP=1 \
      PROOT_LOADER="$PROOT_LIB/libprootloader" \
      LD_LIBRARY_PATH="$PROOT_LIB" \
      "$PROOT_LIB/proot" -r "$WORK" -0 --link2symlink \
        -b /proc -b /dev \
        -b "$SDK:/opt/sdk" -b "$ROOT:/work" -b "$CACHE/nuget:/root/.nuget" \
        -w / /bin/sh -c "$1"
}

if [ "$SKIP_PUBLISH" -eq 0 ]; then
  echo "==> 1/3 容器内 dotnet publish（首次含还原，约 2-13 分钟）"
  mkdir -p "$CACHE/nuget" "$WORK/tmp"
  chmod 1777 "$WORK/tmp" 2>/dev/null || true
  in_container '
    export PATH=/usr/local/bin:/usr/bin:/bin \
           DOTNET_ROOT=/opt/sdk HOME=/root TMPDIR=/tmp \
           DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
           DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    cd /work && /opt/sdk/dotnet publish src/LogAgent.Headless/LogAgent.Headless.csproj \
      -c Release -r linux-arm64 --self-contained true \
      -p:PublishSingleFile=true -p:InvariantGlobalization=true \
      -p:DebugType=none -o /work/publish/android-arm64
  ' || die "dotnet publish 失败"
else
  echo "==> 1/3 跳过 publish，复用 $ROOT/publish/android-arm64"
fi

PUBLISH="$ROOT/publish/android-arm64"
[ -f "$PUBLISH/LogAgent.Headless" ] || die "缺少 $PUBLISH/LogAgent.Headless"

echo "==> 2/3 校验产物"
# InvariantGlobalization 必须是 true：rootfs 里没装 ICU，
# 且 proroot 的 ldso_runtime_dlsym 解析不了 libicui18n 的间接依赖符号。
if grep -q "libicuuc\|libicui18n" <(strings -a "$PUBLISH/LogAgent.Headless" | head -2000); then
  echo "    ⚠ 产物引用了 ICU；确认发布时带了 -p:InvariantGlobalization=true"
fi
SIZE=$(du -h "$PUBLISH/LogAgent.Headless" | cut -f1)
echo "    LogAgent.Headless：$SIZE"
# 单文件模式下 libe_sqlite3.so 是外置的（PublishSingleFile 不打包本地 .so 依赖）
[ -f "$PUBLISH/libe_sqlite3.so" ] && echo "    libe_sqlite3.so：$(du -h "$PUBLISH/libe_sqlite3.so" | cut -f1)"

echo "==> 3/3 打包 assets"
mkdir -p "$OUT"
# 只打运行必需的文件：pdb / appsettings.Development.json 不打
tar -C "$PUBLISH" -czf "$OUT/logagent.tar.gz" \
    --exclude='*.pdb' \
    --exclude='appsettings.Development.json' \
    .
echo "    产出：$OUT/logagent.tar.gz  $(du -h "$OUT/logagent.tar.gz" | cut -f1)"
echo "    sha256：$(sha256sum "$OUT/logagent.tar.gz" | cut -d' ' -f1)"
