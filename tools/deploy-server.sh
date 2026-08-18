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
#
set -euo pipefail

HOST="loboda.dathost.net"
NETRC="${DEADHEIM_NETRC:-$HOME/.deadheim-netrc}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PAYLOAD="$ROOT/dist/server-upload/NpcValheim"
REMOTE="ftp://$HOST/BepInEx/plugins/NpcValheim"
KG_PLUGIN="ftp://$HOST/BepInEx/plugins/KGvalheim-Marketplace_And_Server_NPCs_Revamped"

APPLY=0; REMOVE_KG=0
for arg in "$@"; do
  case "$arg" in
    --apply)     APPLY=1 ;;
    --remove-kg) REMOVE_KG=1 ;;
    *) echo "argumento desconhecido: $arg" >&2; exit 2 ;;
  esac
done

[ -f "$NETRC" ]     || { echo "faltando $NETRC (machine $HOST login <user> password <pass>)" >&2; exit 1; }
[ -d "$PAYLOAD" ]   || { echo "faltando $PAYLOAD -- rode antes: python tools/package.py" >&2; exit 1; }

ftp() { curl --netrc-file "$NETRC" -sS --max-time 600 "$@"; }

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
