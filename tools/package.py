#!/usr/bin/env python3
"""
Builds dist/Npcs.zip -- what the Deadheim Launcher downloads and unpacks into
BepInEx/plugins/NpcValheim/.

The zip is committed rather than built in CI because the GitHub runner has no Valheim
install to compile against. This script exists so the contents are derived from the tree
instead of remembered: forgetting to add a file here is how a release ships a mod whose
quests folder is empty.

Usage:
    python tools/package.py            # uses NpcValheim/bin/Release
    python tools/package.py <bin-dir>
"""

import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "NpcValheim")

# Assemblies that belong to us. YamlDotNet is deliberately absent -- the modpack ships it,
# and a second copy makes BepInEx pick one and break whoever wanted the other.
ASSEMBLIES = ["NpcValheim.dll", "LiteDB.dll"]


def main():
    bin_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(PROJECT, "bin", "Release")
    out_path = os.path.join(ROOT, "dist", "Npcs.zip")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    entries = []
    for name in ASSEMBLIES:
        path = os.path.join(bin_dir, name)
        if not os.path.exists(path):
            print("missing: %s" % path)
            return 1
        entries.append((path, name))

    for folder in ("quests", "templates"):
        source = os.path.join(PROJECT, "Content", folder)
        if not os.path.isdir(source):
            continue
        for name in sorted(os.listdir(source)):
            if name.endswith(".yaml"):
                entries.append((os.path.join(source, name), "Content/%s/%s" % (folder, name)))

    assets = os.path.join(PROJECT, "Assets", "Mailbox")
    if os.path.isdir(assets):
        for name in sorted(os.listdir(assets)):
            entries.append((os.path.join(assets, name), "Assets/Mailbox/%s" % name))

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path, arcname in entries:
            archive.write(path, arcname)

    print("%s  (%d files, %.1f KB)" % (out_path, len(entries), os.path.getsize(out_path) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
