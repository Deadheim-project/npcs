#!/usr/bin/env bash
#
# Publica o mod no servidor Deadheim (DatHost) por FTP.
#
# Existe como script, e nao como uma linha de curl solta, por dois motivos: da para
# ler exatamente o que vai ser escrito antes de autorizar, e uma unica regra de
# permissao cobre o deploy inteiro em vez de liberar curl para qualquer destino.
#
# A senha NAO mora aqui. Vem de ~/.deadheim-netrc (formato netrc padrao, 600), ou do
# caminho em $DEADHEIM_NETRC.
#
# Uso:
#   bash tools/deploy-server.sh              # so lista o que faria (padrao)
#   bash tools/deploy-server.sh --apply      # envia o mod
#   bash tools/deploy-server.sh --apply --remove-kg   # e apaga o KG Marketplace
#   bash tools/deploy-server.sh --check-whitelist     # so compara plugins x whitelist
#   bash tools/deploy-server.sh --apply --sync-whitelist  # e corrige a whitelist
#
# Sobre a whitelist: o AzuAntiCheat guarda em BepInEx/config/AzuAntiCheat_Whitelist/
# uma COPIA de cada .dll que o cliente pode ter, e le essa pasta so no boot. Subir mod
# novo para BepInEx/plugins/ sem atualizar a copia deixa as duas versoes divergentes --
# o servidor roda a nova e o anticheat continua exigindo a velha, entao todo mundo toma
# kick com "Missing Mod(s)" listando versoes que ninguem tem mais (foi o que aconteceu
# em 19/08/2026 com o Jotunn e o EpicMMOSystem). Por isso o deploy agora sempre confere.
#
set -euo pipefail

HOST="loboda.dathost.net"
NETRC="${DEADHEIM_NETRC:-$HOME/.deadheim-netrc}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PAYLOAD="$ROOT/dist/server-upload/NpcValheim"
REMOTE="ftp://$HOST/BepInEx/plugins/NpcValheim"
KG_PLUGIN="ftp://$HOST/BepInEx/plugins/KGvalheim-Marketplace_And_Server_NPCs_Revamped"
PLUGINS="BepInEx/plugins"
WHITELIST="BepInEx/config/AzuAntiCheat_Whitelist"

APPLY=0; REMOVE_KG=0; SYNC_WL=0; CHECK_WL_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --apply)           APPLY=1 ;;
    --remove-kg)       REMOVE_KG=1 ;;
    --sync-whitelist)  SYNC_WL=1 ;;
    --check-whitelist) CHECK_WL_ONLY=1 ;;
    *) echo "argumento desconhecido: $arg" >&2; exit 2 ;;
  esac
done

[ -f "$NETRC" ] || { echo "faltando $NETRC (machine $HOST login <user> password <pass>)" >&2; exit 1; }
if [ "$CHECK_WL_ONLY" != 1 ]; then
  [ -d "$PAYLOAD" ] || { echo "faltando $PAYLOAD -- rode antes: python tools/package.py" >&2; exit 1; }
fi

ftp() { curl --netrc-file "$NETRC" -sS --max-time 600 "$@"; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Lista varias pastas numa unica conexao de FTP: sao 40+ mods dos dois lados, e um curl
# por pasta leva minutos. O "#FIM <url>" que o -w escreve depois de cada transferencia
# diz de qual pasta era o bloco anterior.
list_dirs() {
  local base="$1"; shift
  [ "$#" -gt 0 ] || return 0
  local urls=() d
  for d in "$@"; do urls+=("ftp://$HOST/$base/$d/"); done
  ftp -w '#FIM %{url_effective}
' "${urls[@]}" | tr -d ''
}

# Reduz a saida acima a linhas "pasta<TAB>arquivo.dll<TAB>tamanho".
dll_sizes() {
  awk -v base="$1" '
    $1 == "#FIM" {
      dir = $2
      sub(".*/" base "/", "", dir)
      sub("/$", "", dir)
      for (i = 1; i <= n; i++) print dir "	" name[i] "	" size[i]
      n = 0
      next
    }
    $NF ~ /\.dll$/ { n++; name[n] = $NF; size[n] = $5 }
  '
}

remote_subdirs() {
  ftp "ftp://$HOST/$1/" | tr -d '' | awk '/^d/ { print $NF }'
}

# Compara os .dll da whitelist com os de plugins. Imprime uma linha por divergencia e
# devolve, em WL_DRIFT, o que precisa ser copiado (pasta/arquivo).
WL_DRIFT=()
whitelist_report() {
  local dirs plug white
  mapfile -t dirs < <(remote_subdirs "$WHITELIST")
  [ "${#dirs[@]}" -gt 0 ] || { echo "  (nenhuma pasta na whitelist -- o AzuAntiCheat esta instalado?)"; return 0; }

  white="$(list_dirs "$WHITELIST" "${dirs[@]}" | dll_sizes "$WHITELIST")"
  plug="$(list_dirs "$PLUGINS"   "${dirs[@]}" | dll_sizes "$PLUGINS")"

  WL_DRIFT=()
  local dir file size psize key
  while IFS=$'	' read -r dir file size; do
    [ -n "$dir" ] || continue
    key="$dir/$file"
    psize="$(printf '%s
' "$plug" | awk -F'	' -v k="$key" '$1 "/" $2 == k { print $3 }')"
    if [ -z "$psize" ]; then
      echo "  $key: esta na whitelist e NAO em plugins (mod removido? confira a mao)"
    elif [ "$psize" != "$size" ]; then
      echo "  $key: DIVERGENTE  whitelist=$size  plugins=$psize"
      WL_DRIFT+=("$key")
    fi
  done <<< "$white"

  [ "${#WL_DRIFT[@]}" -eq 0 ] && echo "  whitelist bate com plugins (${#dirs[@]} pastas)"
  return 0
}

# Copia de plugins para a whitelist cada .dll divergente. Sobrescreve em vez de deixar
# .bak do lado: o anticheat le a pasta inteira, uma copia velha ali confunde.
whitelist_sync() {
  local key dir file
  for key in "${WL_DRIFT[@]}"; do
    dir="${key%/*}"; file="${key##*/}"
    ftp -o "$WORK/$file" "ftp://$HOST/$PLUGINS/$key"
    ftp -T "$WORK/$file" "ftp://$HOST/$WHITELIST/$key"
    rm -f "$WORK/$file"
    echo "  copiado $key"
  done
}

if [ "$CHECK_WL_ONLY" = 1 ]; then
  echo "servidor : $HOST"
  echo "conferindo BepInEx/config/AzuAntiCheat_Whitelist x BepInEx/plugins:"
  whitelist_report
  [ "${#WL_DRIFT[@]}" -eq 0 ] || {
    echo
    echo "corrija com: bash tools/deploy-server.sh --apply --sync-whitelist"
    echo "(e reinicie o servidor: a whitelist so e lida no boot)"
    exit 1
  }
  exit 0
fi

count=$(find "$PAYLOAD" -type f | wc -l)
echo "servidor : $HOST"
echo "enviando : $PAYLOAD  ($count arquivos)"
echo "destino  : BepInEx/plugins/NpcValheim"
[ "$REMOVE_KG" = 1 ] && echo "removendo: KGvalheim-Marketplace_And_Server_NPCs_Revamped"

if [ "$APPLY" != 1 ]; then
  echo
  echo "(simulacao -- nada foi escrito. repita com --apply)"
  exit 0
fi

sent=0
while IFS= read -r file; do
  rel="${file#$PAYLOAD/}"
  ftp --ftp-create-dirs -T "$file" "$REMOTE/$rel"
  sent=$((sent + 1))
  [ $((sent % 50)) -eq 0 ] && echo "  ... $sent/$count"
done < <(find "$PAYLOAD" -type f)
echo "  enviados $sent/$count"

if [ "$REMOVE_KG" = 1 ]; then
  echo "removendo o KG..."
  # Apaga arquivo a arquivo: o FTP nao remove uma pasta que ainda tem conteudo.
  for f in $(ftp -l "$KG_PLUGIN/" | tr -d '\r'); do
    ftp -Q "-DELE /BepInEx/plugins/KGvalheim-Marketplace_And_Server_NPCs_Revamped/$f" "$KG_PLUGIN/" || true
    echo "  apagado $f"
  done
  ftp -Q "-RMD /BepInEx/plugins/KGvalheim-Marketplace_And_Server_NPCs_Revamped" "ftp://$HOST/BepInEx/plugins/" || true
  echo "  pasta do plugin removida (os .cfg em config/Marketplace ficam, sao o backup vivo do conteudo)"
fi

echo
echo "conferindo o que ficou la:"
ftp -l "$REMOTE/" | tr -d '\r' | sed 's/^/  /'

echo
echo "conferindo a whitelist do AzuAntiCheat:"
whitelist_report
if [ "${#WL_DRIFT[@]}" -gt 0 ]; then
  if [ "$SYNC_WL" = 1 ]; then
    whitelist_sync
    echo "  whitelist sincronizada -- REINICIE o servidor, ela so e lida no boot"
  else
    echo
    echo "  ^ com isso o cliente toma kick com \"Missing Mod(s)\" nas versoes velhas."
    echo "  corrija com: bash tools/deploy-server.sh --apply --sync-whitelist"
  fi
fi
