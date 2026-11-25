#!/usr/bin/env bash
# Tutte le prove del pacchetto per NAS (serve Docker):
#   1. shellcheck su tutti gli script;
#   2. prove delle funzioni di marshmellovpn.sh con busybox ash (come la shell minima) e con bash (la /bin/sh di DSM);
#   3. prove di mvpn-helper (go test, nel contenitore di costruzione: lo fa costruisci-binari.sh; qui con --tutto);
#   4. prova end-to-end (esegui-e2e.sh).
#   bash server/nas-client/test/esegui-test.sh [--senza-e2e]
set -uo pipefail
export MSYS_NO_PATHCONV=1
QUI=$(cd "$(dirname "$0")" && pwd)
PKG=$(cd "$QUI/.." && pwd)
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }
P=$(winpath "$PKG")
esito=0

echo "### shellcheck"
if docker run --rm -v "$P:/p:ro" -w /p koalaman/shellcheck:stable -s sh \
        marshmellovpn.sh installa-nas.sh disinstalla-nas.sh avvio-dsm.sh test/test-unita.sh test/e2e/server-wg.sh \
        test/e2e/nas-prova.sh build/dentro-go.sh build/dentro-wg.sh \
   && docker run --rm -v "$P:/p:ro" -w /p koalaman/shellcheck:stable \
        build/costruisci-binari.sh test/esegui-test.sh test/esegui-e2e.sh; then
    echo "shellcheck: pulito (12 script)"
else
    echo "shellcheck: problemi (vedi sopra)"; esito=1
fi

echo "### prove delle funzioni (busybox ash)"
docker run --rm -v "$P:/p:ro" alpine:3.22 sh /p/test/test-unita.sh || esito=1
echo "### prove delle funzioni (bash)"
docker run --rm -v "$P:/p:ro" debian:bookworm-slim bash /p/test/test-unita.sh || esito=1

echo "### mvpn-helper: binari presenti e coerenti con SHA256SUMS"
if (cd "$PKG/bin" && sha256sum -c SHA256SUMS); then :; else esito=1; fi
docker run --rm -v "$P:/p:ro" alpine:3.22 /p/bin/amd64/mvpn-helper versione || esito=1
docker run --rm --platform linux/arm64 -v "$P:/p:ro" alpine:3.22 sh -c '/p/bin/arm64/mvpn-helper versione && /p/bin/arm64/wg --version && /p/bin/arm64/wireguard-go --version' || esito=1

if [ "${1:-}" != "--senza-e2e" ]; then
    echo "### end-to-end"
    bash "$QUI/esegui-e2e.sh" || esito=1
fi
[ $esito = 0 ] && echo "TUTTE LE PROVE PASSATE" || echo "QUALCHE PROVA È FALLITA"
exit $esito
