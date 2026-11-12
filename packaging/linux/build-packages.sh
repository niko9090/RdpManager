#!/usr/bin/env bash
# Costruisce .deb, .rpm e AppImage (linux-x64) dalla pubblicazione self-contained di RdpManager.Linux.
# Uso: build-packages.sh <cartella-pubblicazione> <versione> <cartella-uscita> <cartella-strumenti>
# Richiede: dpkg-deb, rpmbuild, mksquashfs (squashfs-tools), curl. Su Windows lo lancia scripts/build-linux.ps1 dentro WSL.
set -euo pipefail

PUBLISH=$(readlink -f "$1")
VERSION=$2
OUT=$(readlink -f "$3")
TOOLS=$(readlink -f "$4")
HERE=$(cd "$(dirname "$0")" && pwd)
BIN=RdpManager.Linux

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Versione non valida: $VERSION" >&2; exit 1; }
[[ -f "$PUBLISH/$BIN" ]] || { echo "Manca $PUBLISH/$BIN" >&2; exit 1; }
for t in dpkg-deb rpmbuild mksquashfs curl; do
  command -v "$t" > /dev/null || { echo "Manca il comando $t" >&2; exit 1; }
done
mkdir -p "$OUT" "$TOOLS"

# Lavoro su un filesystem Linux (non /mnt/c): servono permessi e collegamenti simbolici veri.
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Albero di installazione comune: /opt/rdpmanager, /usr/bin/rdpmanager, .desktop, icona.
ROOT="$WORK/root"
mkdir -p "$ROOT/opt/rdpmanager" "$ROOT/usr/bin" "$ROOT/usr/share/applications" "$ROOT/usr/share/mime/packages" "$ROOT/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH/." "$ROOT/opt/rdpmanager/"
find "$ROOT/opt/rdpmanager" -name '*.pdb' -delete
ln -s "/opt/rdpmanager/$BIN" "$ROOT/usr/bin/rdpmanager"
install -m 644 "$HERE/rdpmanager.desktop" "$ROOT/usr/share/applications/rdpmanager.desktop"
install -m 644 "$HERE/rdpmanager-rdpmconfig.xml" "$ROOT/usr/share/mime/packages/rdpmanager-rdpmconfig.xml"
install -m 644 "$HERE/rdpmanager.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/rdpmanager.png"
find "$ROOT" -type d -exec chmod 755 {} +
find "$ROOT/opt/rdpmanager" -type f -exec chmod 644 {} +
chmod 755 "$ROOT/opt/rdpmanager/$BIN"
# Le librerie native .so si caricano anche senza bit di esecuzione, ma per ordine le teniamo eseguibili.
find "$ROOT/opt/rdpmanager" -type f -name '*.so*' -exec chmod 755 {} +
SIZE_KB=$(du -sk "$ROOT" | cut -f1)

# ---------- .deb ----------
DEB="$OUT/rdpmanager_${VERSION}_amd64.deb"
DEBROOT="$WORK/deb"
mkdir -p "$DEBROOT"
cp -a "$ROOT/." "$DEBROOT/"
mkdir -p "$DEBROOT/DEBIAN"
cat > "$DEBROOT/DEBIAN/control" <<EOF
Package: rdpmanager
Version: $VERSION
Architecture: amd64
Maintainer: niko9090 <noreply@github.com>
Installed-Size: $SIZE_KB
Section: net
Priority: optional
Homepage: https://github.com/niko9090/RdpManager
Depends: freerdp3-x11 | freerdp2-x11, libsecret-1-0, libx11-6, libfontconfig1, libice6, libsm6, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66
Recommends: wireguard-tools
Description: Gestore di connessioni Desktop remoto (RDP) in schede
 RdpManager organizza le connessioni RDP in cartelle e le apre in schede,
 usando FreeRDP. Programma autonomo: non richiede .NET installato.
EOF
rm -f "$DEB"
dpkg-deb --root-owner-group -Zxz -b "$DEBROOT" "$DEB" > /dev/null

# ---------- .rpm ----------
RPMTOP="$WORK/rpm"
mkdir -p "$RPMTOP"/{BUILD,RPMS,SPECS,SRPMS,SOURCES}
cat > "$RPMTOP/SPECS/rdpmanager.spec" <<EOF
Name:           rdpmanager
Version:        $VERSION
Release:        1
Summary:        Gestore di connessioni Desktop remoto (RDP) in schede
License:        Proprietary
URL:            https://github.com/niko9090/RdpManager
BuildArch:      x86_64
AutoReqProv:    no
Requires:       freerdp
Requires:       libsecret
Requires:       libicu
Requires:       krb5-libs
Requires:       (openssl-libs < 1:4 or openssl3-libs)
Requires:       libX11
Requires:       libICE
Requires:       libSM
Requires:       fontconfig
Recommends:     wireguard-tools

%global debug_package %{nil}
%global __os_install_post %{nil}
%global _binary_payload w9.xzdio
%global _build_id_links none

%description
RdpManager organizza le connessioni RDP in cartelle e le apre in schede,
usando FreeRDP. Programma autonomo: non richiede .NET installato.

%install
mkdir -p %{buildroot}
cp -a $ROOT/. %{buildroot}/

%post
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
update-mime-database /usr/share/mime >/dev/null 2>&1 || :
gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor >/dev/null 2>&1 || :

%postun
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
update-mime-database /usr/share/mime >/dev/null 2>&1 || :
gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor >/dev/null 2>&1 || :

%files
%defattr(-,root,root,-)
/opt/rdpmanager
/usr/bin/rdpmanager
/usr/share/applications/rdpmanager.desktop
/usr/share/mime/packages/rdpmanager-rdpmconfig.xml
/usr/share/icons/hicolor/256x256/apps/rdpmanager.png
EOF
rpmbuild -bb --quiet --define "_topdir $RPMTOP" --define "_rpmdir $RPMTOP/RPMS" "$RPMTOP/SPECS/rdpmanager.spec"
RPM_BUILT=$(find "$RPMTOP/RPMS" -name '*.rpm' | head -n 1)
[[ -n "$RPM_BUILT" ]] || { echo "rpmbuild non ha prodotto il pacchetto" >&2; exit 1; }
RPM="$OUT/rdpmanager-${VERSION}-1.x86_64.rpm"
cp -f "$RPM_BUILT" "$RPM"

# ---------- AppImage ----------
APPDIR="$WORK/RdpManager.AppDir"
mkdir -p "$APPDIR/opt"
cp -a "$ROOT/opt/rdpmanager" "$APPDIR/opt/rdpmanager"
install -m 644 "$HERE/rdpmanager.desktop" "$APPDIR/rdpmanager.desktop"
sed -i 's|^Exec=.*|Exec=AppRun %F|' "$APPDIR/rdpmanager.desktop"
mkdir -p "$APPDIR/usr/share/mime/packages"
install -m 644 "$HERE/rdpmanager-rdpmconfig.xml" "$APPDIR/usr/share/mime/packages/rdpmanager-rdpmconfig.xml"
install -m 644 "$HERE/rdpmanager.png" "$APPDIR/rdpmanager.png"
ln -s rdpmanager.png "$APPDIR/.DirIcon"
# Avalonia carica libICE.so.6 e libSM.so.6, che su un Ubuntu minimale (22.04/24.04) mancano: le porto nell'AppImage
# accanto al binario (.NET cerca lì per prime). Le prendo dal sistema di costruzione, altrimenti scarico i pacchetti.
LIBDIR_SYS=/usr/lib/x86_64-linux-gnu
for lib in libICE.so.6 libSM.so.6; do
  if [[ ! -e "$LIBDIR_SYS/$lib" ]]; then
    ( cd "$WORK" && apt-get download libice6 libsm6 > /dev/null 2>&1 && for d in libice6_*.deb libsm6_*.deb; do dpkg-deb -x "$d" "$WORK/libs"; done ) || true
    [[ -e "$WORK/libs$LIBDIR_SYS/$lib" ]] && LIBDIR_SYS="$WORK/libs$LIBDIR_SYS"
  fi
  [[ -e "$LIBDIR_SYS/$lib" ]] || { echo "Manca $lib per l'AppImage (apt-get install libice6 libsm6)" >&2; exit 1; }
  cp -L "$LIBDIR_SYS/$lib" "$APPDIR/opt/rdpmanager/$lib"
  chmod 755 "$APPDIR/opt/rdpmanager/$lib"
done
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
PATH="$PATH:/sbin:/usr/sbin"
# Senza ICU .NET termina subito («Couldn't find a valid ICU package»): in quel caso modalità invariante.
if [ -z "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" ] && ! ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi
exec "$HERE/opt/rdpmanager/RdpManager.Linux" "$@"
EOF
chmod 755 "$APPDIR/AppRun"

TOOL="$TOOLS/appimagetool-x86_64.AppImage"
if [[ ! -x "$TOOL" ]]; then
  curl -fsSL --retry 3 -o "$TOOL.part" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  mv "$TOOL.part" "$TOOL"
  chmod 755 "$TOOL"
fi
APPIMAGE="$OUT/RdpManager-${VERSION}-x86_64.AppImage"
rm -f "$APPIMAGE"
ARCH=x86_64 "$TOOL" --appimage-extract-and-run --no-appstream "$APPDIR" "$APPIMAGE" > "$WORK/appimagetool.log" 2>&1 \
  || { cat "$WORK/appimagetool.log" >&2; exit 1; }
chmod 755 "$APPIMAGE"

ls -l "$DEB" "$RPM" "$APPIMAGE"
