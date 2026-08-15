#!/usr/bin/env python3
"""
Gera o manifest.json do launcher a partir do modpack Deadheim publicado no
Thunderstore.

Por que gerar em vez de manter na mão: o pack tem ~40 mods com versão fixada.
Manter isso manualmente sincronizado com o servidor é erro na certa — e mod em
versão diferente da do servidor é causa clássica de desync e de crash ao
entrar. Aqui a fonte da verdade é o próprio pack: publicou versão nova do
Deadheim, roda isso de novo e commita o resultado.

Uso:
    python installer/generate-manifest.py
    python installer/generate-manifest.py --out DeadheimLauncher/manifest.sample.json
"""

import argparse
import json
import sys
import urllib.request

PACK_NAMESPACE = "Deadheimmods"
PACK_NAME = "Deadheim"
API = "https://thunderstore.io/api/experimental/package/{ns}/{name}/"
UA = {"User-Agent": "DeadheimLauncher-manifest-generator"}

# O pacote do BepInEx não é um plugin: ele é o carregador, e vai na raiz da
# pasta do Valheim (winhttp.dll ao lado do valheim.exe).
GAME_ROOT_PACKAGES = {"BepInExPack_Valheim"}

# Mods de conveniência/admin que rodam só no cliente: podem ficar de fora sem
# quebrar a entrada no servidor, então entram como opcionais.
OPTIONAL_PACKAGES = {
    "Server_devcommands",
    "DevToggle",
    "Azus_UnOfficial_ConfigManager",
}

# Mods de autoria própria, que não vêm do Thunderstore e sim de GitHub Releases.
#
# assetPattern ".zip" casa com qualquer zip do release: são repositórios de um
# mod só, e assim o nome exato do arquivo não precisa ser combinado de antemão.
# Se um release passar a ter vários zips, troque pelo nome exato.
OWN_MODS_OWNER = "Deadheim-project"
OWN_MODS = [
    ("npcs", "NPCs", "Mercador, Teleportador, Correio e Missões."),
    ("Deadheim", "Deadheim", "Mod base do servidor."),
    ("RaidSystem", "Raid System", "Sistema de raides."),
    ("Hearthstone", "Hearthstone", "Pedra de retorno."),
    ("donationshop", "Donation Shop", "Loja de doações."),
]

# Mods opcionais que não fazem parte do pack, mas que o servidor oferece.
EXTRA_OPTIONAL = [
    ("JereKuusela", "Server_devcommands", "Comandos de admin/debug no servidor."),
    ("YouDied", "DevToggle", "Atalho para alternar o modo dev."),
    ("Azumatt", "Azus_UnOfficial_ConfigManager", "Menu in-game para editar as configs dos mods."),
]


def fetch(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def parse_dependency(dep):
    """'Azumatt-AzuClock-1.0.5' -> ('Azumatt', 'AzuClock', '1.0.5')"""
    parts = dep.rsplit("-", 2)
    if len(parts) != 3:
        raise ValueError(f"dependência em formato inesperado: {dep}")
    return parts[0], parts[1], parts[2]


def slugify(name):
    return name.lower().replace("_", "-").replace(" ", "-")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="DeadheimLauncher/manifest.sample.json")
    args = ap.parse_args()

    pack = fetch(API.format(ns=PACK_NAMESPACE, name=PACK_NAME))
    latest = pack["latest"]
    pack_version = latest["version_number"]
    deps = latest["dependencies"]

    print(f"Pack {PACK_NAMESPACE}/{PACK_NAME} v{pack_version} -> {len(deps)} dependências")

    thunderstore_mods = []
    for dep in deps:
        ns, name, version = parse_dependency(dep)
        entry = {
            "id": slugify(name),
            "name": name.replace("_", " "),
            "description": f"Do modpack Deadheim {pack_version}.",
            "required": name not in OPTIONAL_PACKAGES,
            "source": "Thunderstore",
            "thunderstoreNamespace": ns,
            "thunderstoreName": name,
            "version": version,
        }
        if name in GAME_ROOT_PACKAGES:
            entry["target"] = "GameRoot"
            entry["description"] = "Carregador de mods do Valheim. Instalado na raiz do jogo."
        thunderstore_mods.append(entry)

    for ns, name, desc in EXTRA_OPTIONAL:
        if any(m["thunderstoreName"] == name for m in thunderstore_mods):
            continue
        thunderstore_mods.append({
            "id": slugify(name),
            "name": name.replace("_", " "),
            "description": desc + " Opcional.",
            "required": False,
            "source": "Thunderstore",
            "thunderstoreNamespace": ns,
            "thunderstoreName": name,
        })

    own_mods = [
        {
            "id": slugify(repo),
            "name": nome,
            "description": desc,
            "required": True,
            "source": "GitHub",
            "gitHubOwner": OWN_MODS_OWNER,
            "gitHubRepo": repo,
            "assetPattern": ".zip",
        }
        for repo, nome, desc in OWN_MODS
    ]

    manifest = {
        "_comment": (
            f"Gerado por installer/generate-manifest.py a partir de "
            f"{PACK_NAMESPACE}/{PACK_NAME} v{pack_version}. Não edite à mão: "
            f"publique a nova versão do pack e rode o script de novo."
        ),
        "packVersion": pack_version,
        "ownMods": own_mods,
        "thunderstoreMods": thunderstore_mods,
    }

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    required = sum(1 for m in thunderstore_mods if m["required"])
    optional = len(thunderstore_mods) - required
    print(f"Escrito {args.out}")
    print(f"  {len(own_mods)} mod(s) próprio(s)")
    print(f"  {required} obrigatórios, {optional} opcionais")
    return 0


if __name__ == "__main__":
    sys.exit(main())
