#!/usr/bin/env python3
"""Removes crafting-speed / loot-speed / lockpicking bonuses from 7DTD perk mods.

Modes:
  --dry   only report what would change
  (default) write changes, after backing up originals

Rules:
  * a perk whose only effects are the targeted ones is deleted entirely
  * a perk that keeps other effects loses just the targeted passive_effect lines
"""
import re
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

MODS = Path(r"E:\7 Days To Die\Mods")
BACKUP = Path(r"D:\AI_Work\UNIVERSAL 7 DAYS TO DIE MOD MANAGER\mods-backup\2026-11-10_perks")
TARGETS = ("CraftingTime", "ScavengingTime", "LockPickTime", "LockPickBreakChance")

# mod -> (perks to delete entirely, perks to strip targeted effects from)
PLAN = {
    "AwesomePerks": (["AP_Lockpick"], []),
    "CPPS-V3": (
        ["perkCraftSpeed"],
        ["perkCoSt", "perkLooter", "perkStealth"],
    ),
    "Dewta-V3-Perks Plus Serverside Only": (
        ["KnowledgeablePP", "LockpickingLawyerPP", "SteadyHandsPP"],
        ["FastLooterPP"],
    ),
    "LegendPerks": ([], ["CraftingLP", "LockPickLP"]),
    "SingularityPerk": ([], ["Ucorp", "Fcorp"]),
    "Ultimate_End_Game_Perk_V3": (
        ["perkUEGPLockPickSpecialist"],
        ["perkUEGPCraftingProwess", "perkUEGPLooting"],
    ),
    "UltimateEndGamePerk": (
        ["LockPickSpecialist"],
        ["CraftingProwess", "Looting"],
    ),
    "zzPerkMastery": ([], ["perkCriminal", "perkCrafting"]),
}


def find_perk_span(text: str, name: str):
    """Returns (start, end) of the whole <perk name="...">...</perk> block."""
    m = re.search(r'<perk\s+name="%s"' % re.escape(name), text)
    if not m:
        return None
    close = text.find("</perk>", m.start())
    if close < 0:
        return None
    end = close + len("</perk>")
    # swallow the rest of the line and one blank line so we do not leave gaps
    while end < len(text) and text[end] in " \t":
        end += 1
    if end < len(text) and text[end] == "\r":
        end += 1
    if end < len(text) and text[end] == "\n":
        end += 1
    if end < len(text) and text[end] == "\n":
        end += 1
    return m.start(), end


def strip_effect(text: str, start: int, end: int, effect: str):
    """Removes every <passive_effect name="effect" .../> inside [start, end)."""
    removed = 0
    while True:
        m = re.compile(r'<passive_effect\s+name="%s"' % re.escape(effect)).search(text, start, end)
        if not m:
            return text, removed

        # walk to the end of this element (self-closing or with children)
        i = m.start()
        depth = 0
        stop = None
        while i < end:
            if text.startswith("/>", i):
                stop = i + 2
                break
            if text[i] == ">":
                if depth == 0 and text.startswith("</passive_effect>", i + 1):
                    stop = i + 1 + len("</passive_effect>")
                    break
                if depth == 0:
                    # opening tag: look for a closing tag later
                    close = text.find("</passive_effect>", i, end)
                    stop = (close + len("</passive_effect>")) if close >= 0 else (i + 1)
                    break
            i += 1
        if stop is None:
            return text, removed

        # remove trailing spaces + newline belonging to the line
        line_start = text.rfind("\n", 0, m.start()) + 1
        line_end = text.find("\n", stop)
        line_end = len(text) if line_end < 0 else line_end + 1

        prefix = text[line_start:m.start()]
        suffix = text[stop:line_end].strip()
        if prefix.strip() == "" and suffix == "":
            text = text[:line_start] + text[line_end:]
        else:
            text = text[:m.start()] + text[stop:]

        end -= (line_end - line_start) if (prefix.strip() == "" and suffix == "") else (stop - m.start())
        removed += 1


def process(mod: str, deletes, strips, dry: bool):
    path = MODS / mod / "Config" / "progression.xml"
    if not path.exists():
        print(f"  !! missing {path}")
        return

    original = path.read_text(encoding="utf-8")
    text = original
    report = []

    for name in deletes:
        span = find_perk_span(text, name)
        if not span:
            report.append(f"    - {name}: NOT FOUND")
            continue
        block = text[span[0]:span[1]]
        text = text[:span[0]] + text[span[1]:]
        report.append(f"    DELETE perk {name} ({len(block)} bytes)")

    for name in strips:
        span = find_perk_span(text, name)
        if not span:
            report.append(f"    ~ {name}: NOT FOUND")
            continue
        before = text[span[0]:span[1]]
        for effect in TARGETS:
            text, count = strip_effect(text, span[0], span[1], effect)
            if count:
                report.append(f"    STRIP {name}: {effect} x{count}")
        # recompute span after edits for the "became empty" check
        span2 = find_perk_span(text, name)
        if span2:
            after = text[span2[0]:span2[1]]
            if "passive_effect" not in after and "triggered_effect" not in after:
                text = text[:span2[0]] + text[span2[1]:]
                report.append(f"    DELETE perk {name} (empty after strip)")

    if text == original:
        print(f"  {mod}: no changes")
        return

    try:
        ET.fromstring(text)
    except ET.ParseError as exc:
        print(f"  {mod}: XML BROKEN -> {exc}")
        return

    print(f"  {mod}:")
    for line in report:
        print(line)

    if not dry:
        dest = BACKUP / mod / "Config"
        dest.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, dest / "progression.xml")
        path.write_text(text, encoding="utf-8")


def main():
    dry = "--dry" in sys.argv
    print("DRY RUN" if dry else "APPLYING")
    for mod, (deletes, strips) in PLAN.items():
        process(mod, deletes, strips, dry)


if __name__ == "__main__":
    main()
