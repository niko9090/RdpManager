#!/bin/sh
# Gira DENTRO il contenitore Alpine (amd64, oppure arm64 emulato da QEMU) lanciato da costruisci-binari.sh: prende
# wireguard-tools al commit atteso da git.zx2c4.com e costruisce «wg» statico (musl), senza wg-quick né bash.
# Cartelle: /build (questa cartella, sola lettura), /out (uscita). Architettura in $ARCH (amd64 o arm64).
set -eu
# shellcheck disable=SC1091 # versioni.env sta in /build nel contenitore
. /build/versioni.env

apk add --no-cache -q build-base linux-headers git >/dev/null
git clone -q --depth 1 --branch "$WIREGUARD_TOOLS_TAG" https://git.zx2c4.com/wireguard-tools /tmp/wireguard-tools
got=$(git -C /tmp/wireguard-tools rev-parse HEAD)
[ "$got" = "$WIREGUARD_TOOLS_COMMIT" ] || { echo "commit inatteso per wireguard-tools: $got (atteso $WIREGUARD_TOOLS_COMMIT)" >&2; exit 1; }

cd /tmp/wireguard-tools/src
# Niente percorsi della macchina di costruzione nel binario. CFLAGS e LDFLAGS dall'ambiente, non sulla riga di make:
# il Makefile ci aggiunge -isystem uapi/linux e RUNSTATEDIR, che sulla riga di comando andrebbero persi.
CFLAGS="-O2 -ffile-prefix-map=/tmp/wireguard-tools=." LDFLAGS="-static -s" make -s WIREGUARD_TOOLS_VERSION="${WIREGUARD_TOOLS_TAG#v}" wg
mkdir -p "/out/$ARCH"
cp wg "/out/$ARCH/wg"
file_info=$(./wg --version)
echo "$ARCH: $file_info"
gcc --version | head -n 1 > "/out/$ARCH/gcc-version.txt"
