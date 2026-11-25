#!/usr/bin/env bash
# Costruisce i binari statici del pacchetto per NAS (wireguard-go, wg, mvpn-helper) per amd64 e arm64, dentro Docker.
# Serve solo Docker (Docker Desktop su Windows va bene, anche da Git Bash o WSL; per arm64 usa l'emulazione QEMU che
# Docker Desktop ha già). Sorgenti solo da go.dev, proxy.golang.org (moduli Go, verificati con sum.golang.org) e
# git.zx2c4.com; versioni e commit in versioni.env.
#
#   bash server/nas-client/build/costruisci-binari.sh             -> scrive in server/nas-client/bin/
#   bash server/nas-client/build/costruisci-binari.sh --verifica  -> ricostruisce a parte e confronta con bin/SHA256SUMS
#   bash server/nas-client/build/costruisci-binari.sh --tidy      -> rigenera helper/go.mod e go.sum
set -euo pipefail
export MSYS_NO_PATHCONV=1 # Git Bash: non trasformare i percorsi dei volumi Docker

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
# shellcheck source=versioni.env disable=SC1091
. "$here/versioni.env"

# Docker su Windows vuole percorsi Windows per i volumi.
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }

mode=${1:-build}
if [ "$mode" = "--tidy" ]; then
    out=$(mktemp -d)
    docker run --rm -e MVPN_TIDY=1 -v "$(winpath "$here"):/build:ro" -v "$(winpath "$root/helper"):/helper:ro" \
        -v "$(winpath "$out"):/out" "$DEBIAN_IMAGE" sh /build/dentro-go.sh
    cp "$out/go.mod" "$out/go.sum" "$root/helper/"
    rm -rf "$out"
    echo "helper/go.mod e helper/go.sum aggiornati."
    exit 0
fi

out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
echo "### wireguard-go e mvpn-helper (Go $GO_VERSION)"
docker run --rm -v "$(winpath "$here"):/build:ro" -v "$(winpath "$root/helper"):/helper:ro" -v "$(winpath "$out"):/out" \
    "$DEBIAN_IMAGE" sh /build/dentro-go.sh
for arch in amd64 arm64; do
    echo "### wg ($arch)"
    docker run --rm --platform "linux/$arch" -e ARCH="$arch" -v "$(winpath "$here"):/build:ro" -v "$(winpath "$out"):/out" \
        "$ALPINE_IMAGE" sh /build/dentro-wg.sh
done

sums() { (cd "$1" && sha256sum amd64/wireguard-go amd64/wg amd64/mvpn-helper arm64/wireguard-go arm64/wg arm64/mvpn-helper) | sed 's/ [*]/  /'; } # formato «hash  file» anche da Git Bash

if [ "$mode" = "--verifica" ]; then
    if diff <(sums "$out") "$root/bin/SHA256SUMS"; then
        echo "Riproducibile: i binari ricostruiti coincidono con bin/SHA256SUMS."
        exit 0
    fi
    echo "I binari ricostruiti NON coincidono con bin/SHA256SUMS (vedi sopra)." >&2
    exit 1
fi

for arch in amd64 arm64; do
    mkdir -p "$root/bin/$arch"
    cp "$out/$arch/wireguard-go" "$out/$arch/wg" "$out/$arch/mvpn-helper" "$root/bin/$arch/"
    chmod 755 "$root/bin/$arch/"*
done
sums "$out" > "$root/bin/SHA256SUMS"
{
    echo "Binari del pacchetto marshmelloVPN per NAS (costruiti da build/costruisci-binari.sh, $(date -u +%Y-%m-%d))"
    echo "wireguard-go $WIREGUARD_GO_TAG (commit $WIREGUARD_GO_COMMIT), https://git.zx2c4.com/wireguard-go"
    echo "wireguard-tools $WIREGUARD_TOOLS_TAG (commit $WIREGUARD_TOOLS_COMMIT), https://git.zx2c4.com/wireguard-tools, solo «wg», statico musl"
    echo "mvpn-helper (helper/ di questo repository)"
    echo "Compilatore Go: $(cat "$out/go-version.txt") (https://go.dev/dl/, sha256 $GO_SHA256_AMD64)"
    echo "Compilatore C amd64: $(cat "$out/amd64/gcc-version.txt") ($ALPINE_IMAGE)"
    echo "Compilatore C arm64: $(cat "$out/arm64/gcc-version.txt") ($ALPINE_IMAGE, emulato con QEMU)"
    echo "Licenze: wireguard-go MIT, wireguard-tools GPL-2.0 (sorgenti al commit indicato sopra)."
} > "$root/bin/VERSIONI.txt"
cat "$root/bin/SHA256SUMS"
echo "Fatto: binari in $root/bin"
