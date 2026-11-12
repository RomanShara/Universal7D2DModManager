#!/usr/bin/env python3
"""Applies every mod's progression.xml patch to the vanilla file in load order and
reports anchors that no longer resolve (the game logs those as
'XML patch ... did not apply')."""
import copy
import re
import xml.etree.ElementTree as ET
from pathlib import Path

MODS = Path(r"E:\7 Days To Die\Mods")
VANILLA = Path(r"E:\7 Days To Die\Data\Config\progression.xml")

PROG = ET.parse(VANILLA).getroot()

NAMED = re.compile(r"^/progression/(\w+)/(\w+)\[@name='([^']+)'\]$")
PLAIN = re.compile(r"^/progression/(\w+)$")
ATTR = re.compile(r"^/progression/(\w+)/@(\w+)$")


def resolve(xpath):
    """-> ('nodes', parent, [nodes]) | ('attr', element, name) | (None, None, None)"""
    m = NAMED.match(xpath)
    if m:
        parent = PROG.find(m.group(1))
        if parent is None:
            return ("nodes", None, [])
        nodes = [el for el in parent.findall(m.group(2)) if el.get("name") == m.group(3)]
        return ("nodes", parent, nodes)

    m = PLAIN.match(xpath)
    if m:
        el = PROG.find(m.group(1))
        return ("nodes", PROG, [el] if el is not None else [])

    m = ATTR.match(xpath)
    if m:
        return ("attr", PROG.find(m.group(1)), m.group(2))

    return (None, None, None)


def apply_mod(path, stats):
    try:
        patch = ET.parse(path).getroot()
    except ET.ParseError as exc:
        print(f"    PARSE ERROR: {exc}")
        stats["failed"] += 1
        return

    for op in list(patch):
        if not isinstance(op.tag, str):
            continue
        xpath = op.get("xpath", "")
        kind, parent, nodes = resolve(xpath)
        if kind is None:
            stats["unknown"] += 1
            continue

        if kind == "attr":
            el, attr_name = parent, nodes
            if el is not None and op.tag in ("set", "remove"):
                if op.tag == "set":
                    el.set(attr_name, op.get("value", ""))
                else:
                    el.attrib.pop(attr_name, None)
                stats["ok"] += 1
            else:
                stats["failed"] += 1
                stats["failures"].append((path.parent.parent.name, xpath))
            continue

        if not nodes:
            stats["failed"] += 1
            stats["failures"].append((path.parent.parent.name, xpath))
            continue

        if op.tag == "insertAfter":
            for node in nodes:
                idx = list(parent).index(node)
                for child in reversed(list(op)):
                    parent.insert(idx + 1, copy.deepcopy(child))
        elif op.tag == "insertBefore":
            for node in nodes:
                idx = list(parent).index(node)
                for child in reversed(list(op)):
                    parent.insert(idx, copy.deepcopy(child))
        elif op.tag == "append":
            for node in nodes:
                for child in op:
                    node.append(copy.deepcopy(child))
        elif op.tag == "remove":
            for node in nodes:
                parent.remove(node)
        elif op.tag == "set":
            for node in nodes:
                for k, v in op.attrib.items():
                    if k != "xpath":
                        node.set(k, v)
        else:
            stats["unknown"] += 1
            continue
        stats["ok"] += 1


def main():
    mods = sorted([p for p in MODS.iterdir() if p.is_dir()], key=lambda p: p.name.lower())
    total = {"ok": 0, "failed": 0, "unknown": 0, "failures": []}
    print(f"{'mod':40s} applied  failed  unhandled")
    for mod in mods:
        patch = mod / "Config" / "progression.xml"
        if not patch.exists():
            continue
        stats = {"ok": 0, "failed": 0, "unknown": 0, "failures": []}
        apply_mod(patch, stats)
        total["ok"] += stats["ok"]
        total["failed"] += stats["failed"]
        total["unknown"] += stats["unknown"]
        total["failures"] += stats["failures"]
        if stats["failed"] or stats["unknown"]:
            print(f"{mod.name[:40]:40s} {stats['ok']:7d}  {stats['failed']:6d}  {stats['unknown']:9d}")

    print(f"\nTOTAL applied={total['ok']} failed={total['failed']} unhandled={total['unknown']}")
    for mod_name, xp in total["failures"][:15]:
        print(f"  FAILED {mod_name}: {xp}")

    skills = {s.get("name"): s.get("parent") for s in PROG.find("skills")}
    attrs = {a.get("name") for a in PROG.find("attributes")}
    bad = [(p.get("name"), p.get("parent")) for p in PROG.find("perks")
           if p.get("parent") and p.get("parent") not in skills]
    print("perks with unknown parent:", bad[:8] or "none")
    print("skills with unknown attribute:", [n for n, p in skills.items() if p not in attrs][:8] or "none")
    print(f"perks={len(PROG.find('perks'))} skills={len(skills)} attributes={len(attrs)}")


if __name__ == "__main__":
    main()
