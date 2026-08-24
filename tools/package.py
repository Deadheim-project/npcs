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
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "NpcValheim")

# Assemblies that belong to us. YamlDotNet is deliberately absent -- the modpack ships it,
# and a second copy makes BepInEx pick one and break whoever wanted the other.
ASSEMBLIES = ["NpcValheim.dll", "LiteDB.dll"]


def build_distribution():
    """Compiles the release into an output folder of its own."""
    out_dir = os.path.join(PROJECT, "bin", "Dist")
    command = [
        "dotnet", "build", os.path.join(PROJECT, "NpcValheim.csproj"),
        "-c", "Release", "-p:OutputPath=" + out_dir + os.sep,
        "-v", "q", "--nologo",
    ]
    if subprocess.call(command) != 0:
        return None
    return out_dir


def main():
    if len(sys.argv) > 1:
        bin_dir = sys.argv[1]
    else:
        bin_dir = build_distribution()
        if bin_dir is None:
            print("build failed")
            return 1

    dist_dir = os.path.join(ROOT, "dist")
    out_path = os.path.join(dist_dir, "Npcs.zip")
    server_path = os.path.join(dist_dir, "server-upload", "NpcValheim")
    os.makedirs(dist_dir, exist_ok=True)

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

    for asset_folder in ("Mailbox", "Icons"):
        assets = os.path.join(PROJECT, "Assets", asset_folder)
        if os.path.isdir(assets):
            for name in sorted(os.listdir(assets)):
                entries.append((os.path.join(assets, name), "Assets/%s/%s" % (asset_folder, name)))

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path, arcname in entries:
            archive.write(path, arcname)

    # The dedicated server receives exactly the same tree as a player. Rebuilding this
    # staging folder from the same entry list prevents a stale or server-only DLL from
    # drifting away from the launcher package.
    if os.path.isdir(server_path):
        shutil.rmtree(server_path)
    for path, arcname in entries:
        destination = os.path.join(server_path, *arcname.split("/"))
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        shutil.copy2(path, destination)

    print("%s  (%d files, %.1f KB)" % (out_path, len(entries), os.path.getsize(out_path) / 1024.0))
    print("%s  (same %d files for the dedicated server)" % (server_path, len(entries)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
