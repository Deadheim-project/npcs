#!/usr/bin/env python3
"""
Converts KG Marketplace's .cfg content into NpcValheim yaml.

KG's configs are the accumulated content of a live server -- sixty stocked merchants, two
hundred and forty-eight quests, a travel network -- and none of it is in a format this mod
reads. This turns that body of work into our own files so it survives the move, instead of
being retyped by hand.

Usage:
    python import_kg.py <kg-configs-dir> <output-dir>

where <kg-configs-dir> is a copy of BepInEx/config/Marketplace/Configs from the server, and
<output-dir> gets `quests/` and `templates/` written into it.

Nothing here talks to the game: it reads text and writes text, so the conversion can be
re-run and diffed whenever the source content changes.
"""

import io
import math
import os
import re
import sys
from collections import OrderedDict

# ---------------------------------------------------------------------------
# KG's quest cooldown is a bare number with no unit in the file, and the mod's own source
# is no longer public, so it has to be read from the content. The daily packs use 20 and 30,
# and the one-shot story quests use 999999999; 20 and 30 *hours* are ordinary soft-daily
# timers, while 20 and 30 seconds or minutes would make a quest called "Diaria" meaningless.
# Hours it is. Every generated file records the raw number, so flipping this reading is a
# search-and-replace rather than a re-derivation.
COOLDOWN_UNIT_HOURS = 1.0

# KG type -> ours. Harvest is KG's "pick it up yourself", which is exactly our Gather.
# Craft has no equivalent here; the two quests using it become Collect, which asks the player
# to hand the crafted item over -- close enough to be playable, and flagged in the report.
OBJECTIVE_KINDS = {
    "kill": "Kill",
    "collect": "Collect",
    "harvest": "Gather",
    "talk": "Talk",
    "craft": "Collect",
    "explore": "Explore",
    "move": "Explore",
}

COINS = "Coins"


def slug(raw):
    """The same shape QuestStore.Slug produces, so ids generated here resolve in game."""
    out = []
    for ch in (raw or "").strip().lower():
        if ch.isascii() and (ch.isalpha() or ch.isdigit()):
            out.append(ch)
        elif ch in " -_" and out and out[-1] != "-":
            out.append("-")
    return "".join(out).strip("-")


def read_blocks(path):
    """KG's format: a [Name] header followed by that entry's lines until the next header.

    Comment lines (# ...) are dropped, which is what the server does with them -- several
    files use them to switch content off, and importing a disabled trader line would quietly
    put an item back on sale.
    """
    blocks = OrderedDict()
    current = None
    with io.open(path, encoding="utf-8-sig", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            header = re.match(r"^\[(.+?)\]$", line)
            if header:
                # KG hangs a modifier off the header with "=": "[Quest01=autocomplete]" for
                # quests, "[Dialogue=https://...png]" for portraits. The id is the left half,
                # and everything that references a quest writes only that half -- reading the
                # whole string as the id is what left the VIP quest boards pointing at nothing.
                current = header.group(1).split("=", 1)[0].strip()
                blocks[current] = []
                continue
            if line.startswith("#"):
                continue
            if current is not None:
                blocks[current].append(line)
    return blocks


def yaml_str(value):
    """Quotes anything that YAML would otherwise reinterpret. Quest text is player-written
    Portuguese full of colons, quotes and exclamation marks."""
    text = (value or "").replace("\\", "\\\\").replace('"', '\\"')
    return '"%s"' % text


# ---------------------------------------------------------------------------
# Quests


def parse_targets(line, kind):
    """"Boar, 10 | Neck, 5" -> [(Boar, 10), (Neck, 5)].

    A Talk objective is written as a bare NPC name with no count, so a chunk without a comma
    means "one of these".
    """
    targets = []
    for chunk in line.split("|"):
        chunk = chunk.strip()
        if not chunk:
            continue
        parts = [p.strip() for p in chunk.split(",")]
        if len(parts) >= 2 and parts[-1].isdigit():
            targets.append((",".join(parts[:-1]), int(parts[-1])))
        else:
            targets.append((chunk, 1))
    return targets


def parse_rewards(line, report):
    """"EpicMMO_Exp: 250 | Item: Coins, 25 | Item: Bow, 1, 3" -> coins/xp/items."""
    coins, experience, items = 0, 0, []
    for chunk in line.split("|"):
        chunk = chunk.strip()
        if not chunk:
            continue
        key, _, value = chunk.partition(":")
        key, value = key.strip().lower(), value.strip()

        if key in ("epicmmo_exp", "epicmmo_experience"):
            experience = int(re.sub(r"\D", "", value) or 0)
        elif key == "item":
            parts = [p.strip() for p in value.split(",")]
            if len(parts) < 2 or not parts[1].isdigit():
                report.append("reward not understood: %r" % chunk)
                continue
            name, amount = parts[0], int(parts[1])
            quality = int(parts[2]) if len(parts) > 2 and parts[2].isdigit() else 1
            if name.lower() == COINS.lower():
                coins += amount
            else:
                items.append((name, amount, quality))
        elif key == "skill":
            # We have no skill-experience reward. Dropped rather than faked.
            report.append("skill reward dropped: %r" % chunk)
        else:
            report.append("reward not understood: %r" % chunk)
    return coins, experience, items


def parse_requirements(line, report):
    """"QuestFinished: X | EpicMMO_Level: 15" -> (required level, [quest ids])."""
    level, quests = 0, []
    for chunk in line.split("|"):
        chunk = chunk.strip()
        if not chunk or chunk.lower() == "none":
            continue
        key, _, value = chunk.partition(":")
        key, value = key.strip().lower(), value.strip()

        if key == "epicmmo_level":
            level = int(re.sub(r"\D", "", value) or 0)
        elif key == "questfinished":
            quests.extend(slug(q) for q in value.split(",") if slug(q))
        else:
            report.append("requirement not understood: %r" % chunk)
    return level, quests


def convert_quests(configs_dir, out_dir, report):
    quests_dir = os.path.join(configs_dir, "Quests")
    written = {}
    if not os.path.isdir(quests_dir):
        return written

    target_dir = os.path.join(out_dir, "quests")
    os.makedirs(target_dir, exist_ok=True)

    for filename in sorted(os.listdir(quests_dir)):
        if not filename.lower().endswith(".cfg"):
            continue
        source = os.path.join(quests_dir, filename)

        for kg_id, lines in read_blocks(source).items():
            if len(lines) < 7:
                report.append("%s [%s]: only %d lines, skipped" % (filename, kg_id, len(lines)))
                continue

            kind_word = lines[0].strip().lower()
            kind = OBJECTIVE_KINDS.get(kind_word)
            if kind is None:
                report.append("%s [%s]: unknown quest type %r, skipped" % (filename, kg_id, lines[0]))
                continue
            if kind_word == "craft":
                report.append("%s [%s]: Craft has no equivalent, imported as Collect" % (filename, kg_id))

            quest_id = slug(kg_id)
            if quest_id in written:
                report.append("%s [%s]: duplicate id %r, kept the first" % (filename, kg_id, quest_id))
                continue

            name, description = lines[1].strip(), lines[2].strip()
            targets = parse_targets(lines[3], kind)
            if not targets:
                report.append("%s [%s]: no objective, skipped" % (filename, kg_id))
                continue

            coins, experience, items = parse_rewards(lines[4], report)
            raw_cooldown = int(re.sub(r"\D", "", lines[5]) or 0)
            level, prerequisites = parse_requirements(lines[6], report)

            # KG's "never again" sentinel is a number so large it stops meaning a duration.
            reset_hours = 0 if raw_cooldown >= 100000 else int(round(raw_cooldown * COOLDOWN_UNIT_HOURS))

            out = []
            out.append("# Importado do KG Marketplace: %s [%s]" % (filename, kg_id))
            out.append("# Cooldown original: %s (lido como horas)" % raw_cooldown)
            out.append("id: %s" % quest_id)
            out.append("name: %s" % yaml_str(name))
            out.append("description: %s" % yaml_str(description))
            out.append("objectives:")
            for target, amount in targets:
                out.append("  - kind: %s" % kind)
                out.append("    target: %s" % yaml_str(target))
                out.append("    amount: %d" % amount)
            out.append("requiredLevel: %d" % level)
            out.append("repeatable: false")
            out.append("resetHours: %d" % reset_hours)
            if prerequisites:
                out.append("requiresQuests:")
                for prerequisite in prerequisites:
                    out.append("  - %s" % prerequisite)
            out.append("rewards:")
            out.append("  coins: %d" % coins)
            out.append("  experience: %d" % experience)
            if items:
                out.append("  items:")
                for item_name, amount, quality in items:
                    out.append("    - itemName: %s" % yaml_str(item_name))
                    out.append("      amount: %d" % amount)
                    out.append("      quality: %d" % quality)
            out.append("")

            path = os.path.join(target_dir, quest_id + ".yaml")
            io.open(path, "w", encoding="utf-8").write("\n".join(out))
            written[quest_id] = name

    return written


# ---------------------------------------------------------------------------
# Traders


def convert_traders(configs_dir, out_dir, report):
    traders_dir = os.path.join(configs_dir, "Traders")
    count = 0
    if not os.path.isdir(traders_dir):
        return count

    target_dir = os.path.join(out_dir, "templates")
    os.makedirs(target_dir, exist_ok=True)

    for filename in sorted(os.listdir(traders_dir)):
        if not filename.lower().endswith(".cfg"):
            continue

        for kg_id, lines in read_blocks(os.path.join(traders_dir, filename)).items():
            sells, buys = [], []
            for line in lines:
                parts = [p.strip() for p in line.split(",")]
                if len(parts) != 4 or not parts[1].isdigit() or not parts[3].isdigit():
                    report.append("%s [%s]: trade not understood: %r" % (filename, kg_id, line))
                    continue

                pay_item, pay_amount = parts[0], int(parts[1])
                get_item, get_amount = parts[2], int(parts[3])
                if pay_amount <= 0 or get_amount <= 0:
                    continue

                if pay_item.lower() == COINS.lower() and get_item.lower() != COINS.lower():
                    # Player pays coins: the merchant sells this. Our shop prices per unit,
                    # KG priced per bundle, so a bundle divides -- rounding up, because a
                    # price of zero would be an item given away.
                    unit = max(1, math.ceil(pay_amount / get_amount))
                    if pay_amount % get_amount:
                        report.append(
                            "%s [%s]: %d coins for %dx %s is %.3f/un, sold at %d/un"
                            % (filename, kg_id, pay_amount, get_amount, get_item,
                               pay_amount / get_amount, unit))
                    sells.append((get_item, unit))
                elif get_item.lower() == COINS.lower() and pay_item.lower() != COINS.lower():
                    # Player hands the item over: the merchant buys it. Rounded down, so the
                    # conversion never invents money the original did not pay -- but never to
                    # zero, because a price of zero is not a cheaper trade, it is no trade at
                    # all. The two directions used to disagree about this: a sell under 1/un
                    # became 1 and survived, a buy under 1/un became 0 and was discarded, so
                    # bulk buy offers vanished while their mirror image did not. KG only had
                    # one such line (50x BoneFragments for 30 coins) out of 20 buys, so the
                    # loss was small -- but it was silent past the report, and the asymmetry
                    # would eat more on any config that priced buying in bulk.
                    unit = max(1, get_amount // pay_amount)
                    if get_amount % pay_amount:
                        report.append(
                            "%s [%s]: %dx %s for %d coins is %.3f/un, bought at %d/un"
                            % (filename, kg_id, pay_amount, pay_item, get_amount,
                               get_amount / pay_amount, unit))
                    buys.append((pay_item, unit))
                else:
                    report.append("%s [%s]: barter without coins is not supported: %r"
                                  % (filename, kg_id, line))

            if not sells and not buys:
                continue

            out = []
            out.append("# Importado do KG Marketplace: Traders/%s [%s]" % (filename, kg_id))
            out.append("# Sem 'name': aplicar este modelo troca a lista de precos e deixa o")
            out.append("# nome do NPC como esta.")
            out.append('name: ""')
            out.append("forType: Marketplace")
            out.append("marketplace:")
            out.append("  taxPercent: 0")
            # An empty list is written by leaving the key out entirely. A bare "buys:" with
            # nothing under it deserialises to null, not to an empty list, and null is what
            # the profile loader would then try to iterate.
            for key, entries in (("sells", sells), ("buys", buys)):
                if not entries:
                    continue
                out.append("  %s:" % key)
                for item_name, price in entries:
                    out.append("    - itemName: %s" % yaml_str(item_name))
                    out.append("      price: %d" % price)
            out.append("")

            path = os.path.join(target_dir, "kg-%s.yaml" % slug(kg_id))
            io.open(path, "w", encoding="utf-8").write("\n".join(out))
            count += 1

    return count


# ---------------------------------------------------------------------------
# Teleporters


def convert_teleporters(configs_dir, out_dir, report):
    hubs_dir = os.path.join(configs_dir, "Teleporters")
    count = 0
    if not os.path.isdir(hubs_dir):
        return count

    target_dir = os.path.join(out_dir, "templates")
    os.makedirs(target_dir, exist_ok=True)

    for filename in sorted(os.listdir(hubs_dir)):
        if not filename.lower().endswith(".cfg"):
            continue

        for kg_id, lines in read_blocks(os.path.join(hubs_dir, filename)).items():
            destinations = []
            for line in lines:
                parts = [p.strip() for p in line.split(",")]
                if len(parts) != 4:
                    report.append("%s [%s]: destination not understood: %r" % (filename, kg_id, line))
                    continue
                try:
                    x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                except ValueError:
                    report.append("%s [%s]: destination not understood: %r" % (filename, kg_id, line))
                    continue
                destinations.append((parts[0], x, y, z))

            if not destinations:
                continue

            out = []
            out.append("# Importado do KG Marketplace: Teleporters/%s [%s]" % (filename, kg_id))
            out.append("# KG cobrava por NPC, nao por destino. Cada rota fica em 0 -- ajuste")
            out.append("# 'cost' por destino no painel de admin.")
            out.append('name: ""')
            out.append("forType: Teleporter")
            out.append("teleporter:")
            out.append("  costItem: Coins")
            out.append("  costAmount: 0")
            out.append("  cooldownSeconds: 0")
            out.append("  destinations:")
            for label, x, y, z in destinations:
                out.append("    - id: %s" % slug(label))
                out.append("      name: %s" % yaml_str(label))
                out.append("      x: %g" % x)
                out.append("      y: %g" % y)
                out.append("      z: %g" % z)
                out.append("      yaw: 0")
                out.append("      cost: 0")
            out.append("")

            path = os.path.join(target_dir, "kg-tp-%s.yaml" % slug(kg_id))
            io.open(path, "w", encoding="utf-8").write("\n".join(out))
            count += 1

    return count


# ---------------------------------------------------------------------------
# Quest boards


def convert_quest_profiles(configs_dir, out_dir, known_quests, report):
    profiles_dir = os.path.join(configs_dir, "QuestProfiles")
    count = 0
    if not os.path.isdir(profiles_dir):
        return count

    target_dir = os.path.join(out_dir, "templates")
    os.makedirs(target_dir, exist_ok=True)

    for filename in sorted(os.listdir(profiles_dir)):
        if not filename.lower().endswith(".cfg"):
            continue

        for kg_id, lines in read_blocks(os.path.join(profiles_dir, filename)).items():
            ids = []
            for line in lines:
                for raw in line.split(","):
                    quest_id = slug(raw)
                    if not quest_id:
                        continue
                    if quest_id not in known_quests:
                        report.append("%s [%s]: offers unknown quest %r" % (filename, kg_id, raw.strip()))
                        continue
                    if quest_id not in ids:
                        ids.append(quest_id)

            if not ids:
                report.append("%s [%s]: no known quests, skipped" % (filename, kg_id))
                continue

            out = []
            out.append("# Importado do KG Marketplace: QuestProfiles/%s [%s]" % (filename, kg_id))
            out.append('name: ""')
            out.append("forType: QuestGiver")
            out.append("questGiver:")
            out.append("  quests:")
            for quest_id in ids:
                out.append("    - %s   # %s" % (quest_id, known_quests[quest_id]))
            out.append("")

            path = os.path.join(target_dir, "kg-quests-%s.yaml" % slug(kg_id))
            io.open(path, "w", encoding="utf-8").write("\n".join(out))
            count += 1

    return count


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    configs_dir, out_dir = sys.argv[1], sys.argv[2]
    report = []

    quests = convert_quests(configs_dir, out_dir, report)
    traders = convert_traders(configs_dir, out_dir, report)
    hubs = convert_teleporters(configs_dir, out_dir, report)
    boards = convert_quest_profiles(configs_dir, out_dir, quests, report)

    print("quests      %d" % len(quests))
    print("traders     %d" % traders)
    print("teleporters %d" % hubs)
    print("quest board %d" % boards)

    report_path = os.path.join(out_dir, "import-report.txt")
    io.open(report_path, "w", encoding="utf-8").write(
        "\n".join(["Anotacoes da importacao do KG Marketplace.", ""] + report) + "\n")
    print("\n%d note(s) -> %s" % (len(report), report_path))
    return 0


if __name__ == "__main__":
    sys.exit(main())
