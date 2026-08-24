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

# O que ja esta la, para nao reenviar byte igual.
#
# Sem isto o deploy manda os 376 arquivos toda vez, um curl (uma conexao de FTP inteira)
# por arquivo. O peso esta nos 363 yaml minusculos de quests/templates, que praticamente
# nunca mudam: entre dois releases o que muda e a DLL. E como o find desce nas subpastas
# primeiro, a DLL -- o unico arquivo que importa para o restart -- era a ultima a subir,
# depois de ~9 minutos de reescrita de arquivos identicos.
#
# A listagem inteira sai numa unica conexao: um curl por pasta ja custaria mais do que o
# envio. O "#FIM <url>" que o -w escreve apos cada listagem diz de qual pasta era o bloco
# anterior.
declare -A REMOTE_SIZES
skipped=0

load_remote_sizes() {
  local urls=() rel
  while IFS= read -r rel; do
    if [ "$rel" = "." ]; then urls+=("$REMOTE/"); else urls+=("$REMOTE/$rel/"); fi
  done < <(cd "$PAYLOAD" && find . -type d | sed 's|^\./||')
  [ "${#urls[@]}" -gt 0 ] || return 0

  # Pasta que ainda nao existe no servidor faz o curl sair diferente de zero; aqui isso
  # so quer dizer "nada para comparar", entao a falha e tolerada de proposito.
  local listing
  listing="$(ftp -w '#FIM %{url_effective}
' "${urls[@]}" 2>/dev/null || true)"

  # O awk ja junta pasta+arquivo num campo so. Emitir a pasta separada deixava a linha
  # dos arquivos da raiz comecando por TAB, e o `read` com IFS=TAB come o campo vazio da
  # frente -- o tamanho ia parar no nome e nenhum arquivo da raiz (as duas DLLs, as unicas
  # que mudam de verdade) casava nunca.
  local path size
  while IFS=$'\t' read -r path size; do
    [ -n "$path" ] && [ -n "$size" ] || continue
    REMOTE_SIZES["$path"]="$size"
  done < <(printf '%s' "$listing" | tr -d '\r' | awk -v base="$REMOTE/" '
      $1 == "#FIM" {
        dir = $2
        sub(base, "", dir)
        sub("/$", "", dir)
        for (i = 1; i <= n; i++) print (dir == "" ? name[i] : dir "/" name[i]) "\t" size[i]
        n = 0
        next
      }
      NF >= 9 && $1 !~ /^d/ { n++; name[n] = $NF; size[n] = $5 }
    ')
}

echo "conferindo o que ja esta no servidor..."
load_remote_sizes
echo "  ${#REMOTE_SIZES[@]} arquivo(s) ja no destino"

# Decide antes de escrever, para a simulacao poder mostrar exatamente a mesma lista que
# o --apply vai enviar. Um dry run que nao diz o que mudaria nao serve para autorizar.
PENDING=()
while IFS= read -r file; do
  rel="${file#$PAYLOAD/}"
  if [ "${REMOTE_SIZES[$rel]:-}" = "$(stat -c %s "$file")" ]; then
    skipped=$((skipped + 1))
    continue
  fi
  PENDING+=("$file")
done < <(find "$PAYLOAD" -type f)

echo "  a enviar: ${#PENDING[@]}   iguais: $skipped"
for file in "${PENDING[@]:0:12}"; do echo "    ${file#$PAYLOAD/}"; done
[ "${#PENDING[@]}" -gt 12 ] && echo "    ... e mais $(( ${#PENDING[@]} - 12 ))"

if [ "$APPLY" != 1 ]; then
  echo
  echo "(simulacao -- nada foi escrito. repita com --apply)"
  exit 0
fi

sent=0
for file in "${PENDING[@]}"; do
  rel="${file#$PAYLOAD/}"
  ftp --ftp-create-dirs -T "$file" "$REMOTE/$rel"
  sent=$((sent + 1))
  [ $((sent % 25)) -eq 0 ] && echo "  ... $sent/${#PENDING[@]}"
done
echo "  enviados $sent, iguais $skipped (de $count)"

# Releases antigos criavam uma segunda assembly. O mod agora é deliberadamente idêntico
# nos dois lados; apagar o resíduo evita o BepInEx carregar duas versões do mesmo código.
ftp -Q "-DELE /BepInEx/plugins/NpcValheim/NpcValheim.Server.dll" "$REMOTE/" || true
echo "  removida a DLL legada NpcValheim.Server.dll (se existia)"

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
