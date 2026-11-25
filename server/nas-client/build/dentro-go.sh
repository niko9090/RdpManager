#!/bin/sh
# Gira DENTRO il contenitore Debian lanciato da costruisci-binari.sh: scarica Go da go.dev (checksum verificato),
# prende wireguard-go al commit atteso da git.zx2c4.com e costruisce wireguard-go e mvpn-helper statici per amd64 e arm64.
# Cartelle: /build (questa cartella, sola lettura), /helper (sorgenti dell'helper), /out (uscita).
set -eu
# shellcheck disable=SC1091 # versioni.env sta in /build nel contenitore
. /build/versioni.env

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl git >/dev/null

echo "== Go $GO_VERSION da go.dev"
curl -fsSL -o /tmp/go.tgz "https://go.dev/dl/go${GO_VERSION}.linux-amd64.tar.gz"
echo "$GO_SHA256_AMD64  /tmp/go.tgz" | sha256sum -c -
tar -C /opt -xzf /tmp/go.tgz
export PATH="/opt/go/bin:$PATH" GOTOOLCHAIN=local GOFLAGS=-mod=readonly GOPROXY=https://proxy.golang.org GOSUMDB=sum.golang.org
export CGO_ENABLED=0 GOCACHE=/tmp/gocache GOMODCACHE=/tmp/gomod
go version

# Prima volta (o dopo un cambio di dipendenze): MVPN_TIDY=1 rigenera go.sum dell'helper.
if [ "${MVPN_TIDY:-0}" = 1 ]; then
    cp -r /helper /tmp/helper-tidy
    (cd /tmp/helper-tidy && GOFLAGS='' go mod tidy && cp go.mod go.sum /out/)
    echo "go.mod e go.sum aggiornati in /out"
    exit 0
fi

echo "== wireguard-go $WIREGUARD_GO_TAG"
git clone -q --depth 1 --branch "$WIREGUARD_GO_TAG" https://git.zx2c4.com/wireguard-go /tmp/wireguard-go
got=$(git -C /tmp/wireguard-go rev-parse HEAD)
[ "$got" = "$WIREGUARD_GO_COMMIT" ] || { echo "commit inatteso per wireguard-go: $got (atteso $WIREGUARD_GO_COMMIT)" >&2; exit 1; }

cp -r /helper /tmp/helper
echo "== prove dell'helper"
(cd /tmp/helper && go vet ./... && go test -count=1 ./...)

for arch in amd64 arm64; do
    mkdir -p "/out/$arch"
    echo "== $arch"
    (cd /tmp/wireguard-go && GOOS=linux GOARCH=$arch go build -trimpath -buildvcs=false -ldflags "-s -w -buildid=" -o "/out/$arch/wireguard-go" .)
    (cd /tmp/helper && GOOS=linux GOARCH=$arch go build -trimpath -buildvcs=false -ldflags "-s -w -buildid=" -o "/out/$arch/mvpn-helper" .)
done
go version > /out/go-version.txt
