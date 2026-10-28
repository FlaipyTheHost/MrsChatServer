#!/usr/bin/env bash
# Gera os pacotes de release a partir da saída do `dotnet publish`.
#
#   uso: empacotamento/empacotar.sh <versão> <pasta-publicar> <pasta-saída>
#
#   <pasta-publicar>/win-x64    saída do publish para Windows
#   <pasta-publicar>/linux-x64  saída do publish para Linux
#
# Gera em <pasta-saída>:
#   <App>-<versão>-win-x64.zip        Windows (extrair e rodar o .exe)
#   <pacote>-<versão>-linux-x64.tar.gz Linux portátil
#   <pacote>_<versão>_amd64.deb        Debian, Ubuntu, Mint...
#   <pacote>-<versão>-1.x86_64.rpm     Fedora, RHEL, openSUSE...
#   SHA256SUMS.txt
#
# Precisa de: zip, tar, dpkg-deb e rpmbuild (no Ubuntu: apt install rpm).
set -euo pipefail

# ------------------------------------------------------------ dados do pacote
APP="MrsChatServer"                # nome do executável
PACOTE="mrschatserver"             # nome do pacote .deb/.rpm
COMANDO="mrschatserver"            # comando instalado em /usr/bin
RESUMO="Servidor de salas do MrsChat, com criptografia e painel de central"
DESCRICAO="Servidor do MrsChat: hospeda uma sala para clientes MrsChat na rede local ou na internet
(túnel público embutido, funciona atrás de CGNAT). Criptografia obrigatória com chave de
identidade própria e painel com CPU, memória, tráfego e operadores conectados."
URL="https://github.com/FlaipyTheHost/MrsChatServer"
LICENCA="GPL-3.0"
MANTENEDOR="${MANTENEDOR:-FlaipyTheHost <flaipythehost@users.noreply.github.com>}"
# "link": /usr/bin/<comando> aponta para o executável.
# "pasta": /usr/bin/<comando> é um script que entra em ~/.config/<App> antes de rodar
#          (para o servidor guardar a chave e o .conf sempre no mesmo lugar).
LANCADOR="pasta"
# Dependências do binário autocontido do .NET (sem ICU: o app usa InvariantGlobalization).
DEPENDE_DEB="libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl1.1"
DEPENDE_RPM="glibc, libgcc, libstdc++, zlib, openssl-libs"
# ---------------------------------------------------------------------------

[ $# -eq 3 ] || { echo "uso: $0 <versão> <pasta-publicar> <pasta-saída>" >&2; exit 2; }
VERSAO="$1"
PUBLICAR="$(cd "$2" && pwd)"
SAIDA="$(mkdir -p "$3" && cd "$3" && pwd)"
RAIZ="$(cd "$(dirname "$0")/.." && pwd)"

[[ "$VERSAO" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.]+)?$ ]] || { echo "versão inválida: $VERSAO" >&2; exit 2; }
for d in win-x64 linux-x64; do
    [ -d "$PUBLICAR/$d" ] || { echo "faltou $PUBLICAR/$d (rode o dotnet publish antes)" >&2; exit 2; }
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Arquivos que vão junto do programa (sem símbolos de depuração).
copiar_programa() { # origem destino
    mkdir -p "$2"
    (cd "$1" && find . -type f ! -name '*.pdb' ! -name '*.dbg' -print0 | xargs -0 -I{} cp --parents {} "$2/")
}

# --------------------------------------------------------------- Windows .zip
nome_zip="$APP-$VERSAO-win-x64"
copiar_programa "$PUBLICAR/win-x64" "$TMP/$nome_zip"
cp "$RAIZ/README.md" "$TMP/$nome_zip/"
cp "$RAIZ/LICENSE" "$TMP/$nome_zip/LICENSE.txt"
(cd "$TMP" && zip -qr9 "$SAIDA/$nome_zip.zip" "$nome_zip")

# ------------------------------------------------------------- Linux .tar.gz
nome_tar="$PACOTE-$VERSAO-linux-x64"
copiar_programa "$PUBLICAR/linux-x64" "$TMP/$nome_tar"
chmod 755 "$TMP/$nome_tar/$APP"
cp "$RAIZ/README.md" "$RAIZ/LICENSE" "$TMP/$nome_tar/"
tar -C "$TMP" --owner=0 --group=0 -czf "$SAIDA/$nome_tar.tar.gz" "$nome_tar"

# ------------------------------------------ árvore comum para o .deb e o .rpm
#   /usr/lib/<pacote>/          programa
#   /usr/bin/<comando>          lançador
#   /usr/share/doc/<pacote>/    README, licença e .conf de exemplo
arvore="$TMP/arvore"
copiar_programa "$PUBLICAR/linux-x64" "$arvore/usr/lib/$PACOTE"
chmod 755 "$arvore/usr/lib/$PACOTE/$APP"
mkdir -p "$arvore/usr/bin" "$arvore/usr/share/doc/$PACOTE"
# O .conf do pacote vira exemplo: as configurações do usuário ficam em ~/.config/<App>/.
if [ -f "$arvore/usr/lib/$PACOTE/$APP.conf" ]; then
    mv "$arvore/usr/lib/$PACOTE/$APP.conf" "$arvore/usr/share/doc/$PACOTE/$APP.conf.exemplo"
fi
cp "$RAIZ/README.md" "$arvore/usr/share/doc/$PACOTE/"
cp "$RAIZ/LICENSE" "$arvore/usr/share/doc/$PACOTE/copyright"

if [ "$LANCADOR" = "pasta" ]; then
    cat > "$arvore/usr/bin/$COMANDO" <<EOF
#!/bin/sh
# Roda o $APP dentro de ~/.config/$APP, onde ficam o $APP.conf e a chave do servidor.
dir="\${XDG_CONFIG_HOME:-\$HOME/.config}/$APP"
mkdir -p "\$dir" && cd "\$dir" || exit 1
exec /usr/lib/$PACOTE/$APP "\$@"
EOF
    chmod 755 "$arvore/usr/bin/$COMANDO"
else
    ln -s "../lib/$PACOTE/$APP" "$arvore/usr/bin/$COMANDO"  # relativo: funciona também em raízes alternativas
fi

# ----------------------------------------------------------------------- .deb
deb="$TMP/deb"
cp -a "$arvore" "$deb"
mkdir -p "$deb/DEBIAN"
tamanho_kb=$(du -sk --exclude=DEBIAN "$deb" | cut -f1)
{
    echo "Package: $PACOTE"
    echo "Version: $VERSAO"
    echo "Architecture: amd64"
    echo "Maintainer: $MANTENEDOR"
    echo "Installed-Size: $tamanho_kb"
    echo "Depends: $DEPENDE_DEB"
    echo "Section: net"
    echo "Priority: optional"
    echo "Homepage: $URL"
    echo "Description: $RESUMO"
    echo "$DESCRICAO" | sed 's/^/ /'
} > "$deb/DEBIAN/control"
dpkg-deb --root-owner-group -Zxz --build "$deb" "$SAIDA/${PACOTE}_${VERSAO}_amd64.deb" > /dev/null

# ----------------------------------------------------------------------- .rpm
# O rpm não aceita "-" na versão (ex.: 2.4.0-beta vira 2.4.0~beta).
versao_rpm="${VERSAO//-/\~}"
top="$TMP/rpmbuild"
mkdir -p "$top"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
cat > "$top/SPECS/$PACOTE.spec" <<EOF
Name:           $PACOTE
Version:        $versao_rpm
Release:        1
Summary:        $RESUMO
License:        $LICENCA
URL:            $URL
BuildArch:      x86_64
Requires:       $DEPENDE_RPM
# O executável é um arquivo único do .NET: não pode ser "stripado" nem ter dependências deduzidas.
AutoReqProv:    no
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

%description
$DESCRICAO

%install
cp -a "$arvore/." "%{buildroot}/"

%files
/usr/lib/$PACOTE
/usr/bin/$COMANDO
%doc /usr/share/doc/$PACOTE
EOF
rpmbuild --quiet -bb --define "_topdir $top" "$top/SPECS/$PACOTE.spec"
cp "$top"/RPMS/x86_64/*.rpm "$SAIDA/"

# ------------------------------------------------------------------- checksums
(cd "$SAIDA" && sha256sum -- *.zip *.tar.gz *.deb *.rpm > SHA256SUMS.txt)
echo "Pacotes gerados em $SAIDA:"
(cd "$SAIDA" && ls -l)
