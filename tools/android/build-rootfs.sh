#!/usr/bin/env bash
# 构建 Android 版 LogAgent 的 Ubuntu rootfs（arm64，供 proroot/proot 容器使用）。
#
# 产出：assets/rootfs.tar.gz（约 28MB）
#
# 设计要点（均来自实测，不要"顺手优化"）：
#   1. 只用 ubuntu-base，不装 libicu —— .NET 侧靠 InvariantGlobalization 绕开 ICU，
#      装了 ICU 反而会踩 proroot 的 ldso_runtime_dlsym 缺陷（ucurr_forLocale_74 找不到）。
#   2. 必须注入 CA 证书与 Asia/Shanghai 时区：ubuntu-base 两者都没有，
#      缺 CA 会让模型 API 的 TLS 握手失败。
#   3. 必须清 apt 缓存：224MB → 108MB，压缩后 75MB → 28MB。
#   4. ubuntu-base 有 2 个硬链接（perl5.38.2 / uncompress），Android app 私有目录
#      不支持 link(2)，解压时会告警但并不影响运行，脚本里显式忽略。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CACHE="${LOGAGENT_ANDROID_CACHE:-$HOME/.cache/logagent-android}"
OUT="${1:-$ROOT/src/LogAgent.Android/assets}"
BASE_URL="${UBUNTU_BASE_URL:-https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.3-base-arm64.tar.gz}"

mkdir -p "$CACHE"

echo "==> 1/5 下载 ubuntu-base"
TARBALL="$CACHE/ubuntu-base-arm64.tar.gz"
if [ ! -f "$TARBALL" ]; then
  # -C - 断点续传：这条线路（cdimage.ubuntu.com）实测会中途 EOF，
  # 不带续传会每次从头重来；配合 --retry 直到 gzip 能完整解开为止。
  for attempt in 1 2 3 4 5; do
    curl -fL --http1.1 -C - --retry 3 --retry-delay 2 -o "$TARBALL" "$BASE_URL" || true
    if gzip -t "$TARBALL" 2>/dev/null; then break; fi
    echo "    第 $attempt 次下载未完整，重试…"
  done
  gzip -t "$TARBALL" || { echo "下载失败：$BASE_URL"; exit 1; }
fi
echo "    $(du -h "$TARBALL" | cut -f1)  $(sha256sum "$TARBALL" | cut -d' ' -f1)"

echo "==> 2/5 解压到临时目录"
WORK="$CACHE/rootfs-build"
rm -rf "$WORK"
mkdir -p "$WORK"
# ubuntu-base 里有 2 个硬链接（perl5.38.2 / uncompress）。Android app 私有目录禁 link(2)，
# 解压时必然报 "Cannot hard link"，这两条失败不影响运行（实测 selftest 通过），显式忽略。
if ! tar -xzf "$TARBALL" -C "$WORK" 2>"$CACHE/tar.log"; then
  grep -q "Cannot hard link" "$CACHE/tar.log" || { cat "$CACHE/tar.log" >&2; exit 1; }
  echo "    ⚠ $(grep -c 'Cannot hard link' "$CACHE/tar.log") 个硬链接未能还原（预期内，不影响运行）"
fi

echo "==> 3/5 注入 CA 证书 + 时区"
# CA：ubuntu-base 不带 ca-certificates。这里从宿主的已安装包复制，
# 避免在容器里跑 apt（容器内 dpkg 会因 link(2) 失败）。
# Termux 的 CA 在 $PREFIX/etc/tls/cert.pem；Debian/Ubuntu 在 /etc/ssl/certs/ca-certificates.crt
CA_SRC="${CA_BUNDLE:-}"
if [ -z "$CA_SRC" ]; then
  for cand in /etc/ssl/certs/ca-certificates.crt "${PREFIX:-/usr}/etc/tls/cert.pem"; do
    [ -f "$cand" ] && { CA_SRC="$cand"; break; }
  done
fi
if [ -z "$CA_SRC" ] || [ ! -f "$CA_SRC" ]; then
  echo "    ⚠ 未找到 CA 证书（$CA_SRC），TLS 会失败。可用 CA_BUNDLE=... 指定。"
else
  mkdir -p "$WORK/etc/ssl/certs"
  cp "$CA_SRC" "$WORK/etc/ssl/certs/ca-certificates.crt"
  echo "    已注入 CA：$(du -h "$CA_SRC" | cut -f1)"
fi

# 时区：ubuntu-base 无 tzdata，/etc/localtime 是 UTC
# Termux 默认不带 tzdata；Ubuntu/Debian 宿主有。都没有时退回固定偏移（CST-8，无 DST）。
TZ_SRC="${ZONEINFO_SRC:-/usr/share/zoneinfo/Asia/Shanghai}"
if [ -f "$TZ_SRC" ]; then
  mkdir -p "$WORK/usr/share/zoneinfo/Asia"
  cp -a "$TZ_SRC" "$WORK/usr/share/zoneinfo/Asia/"
  ln -sf /usr/share/zoneinfo/Asia/Shanghai "$WORK/etc/localtime"
  echo "    已注入时区：Asia/Shanghai"
else
  # TZ=Asia/Shanghai 需要 zoneinfo；没有就用 POSIX TZ 串，效果等价（中国无夏令时）
  printf 'CST-8\n' > "$WORK/etc/timezone"
  echo "    ⚠ 无 zoneinfo，改用固定偏移：TZ=CST-8（运行时按此设置）"
fi

echo "==> 4/5 精简"
# 这几项占 116MB，删掉后 224MB → 108MB（压缩后 75MB → 28MB）
rm -rf "$WORK"/var/cache/apt/* \
       "$WORK"/var/lib/apt/lists/* \
       "$WORK"/var/log/* \
       "$WORK"/var/tmp/* \
       "$WORK"/etc/ssl/certs/ca-certificates.crt.dpkg-dist
chmod 1777 "$WORK/tmp" 2>/dev/null || true
echo "    精简后：$(du -sh "$WORK" | cut -f1)"

echo "==> 5/5 打包"
mkdir -p "$OUT"
# 排除运行时才挂载的目录，避免把宿主机的 /proc /sys /dev 打进包
tar -C "$WORK" -cf - \
    --exclude=./proc/* --exclude=./sys/* --exclude=./dev/* \
    . | gzip -9 > "$OUT/rootfs.tar.gz"
echo "    产出：$OUT/rootfs.tar.gz  $(du -h "$OUT/rootfs.tar.gz" | cut -f1)"
echo "    sha256：$(sha256sum "$OUT/rootfs.tar.gz" | cut -d' ' -f1)"
