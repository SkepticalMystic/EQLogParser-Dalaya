"""Classify changes between two parser-format spells.txt files.

Useful for verifying a rebuild didn't introduce structural drift. Run it after
convert_spells.py against the previous version of spells.txt (or a backup).

Usage:
    python analyze_diff.py OLD NEW

Output: counts of added/removed/changed IDs, plus a per-column breakdown of
where the changes landed. Changes outside the EXPECTED_CHANGE_COLS set are
flagged as suspicious — the converter only writes those columns from source
data, so drift elsewhere indicates a mapping bug or a hand-edited file.

Note: the first rebuild after a new source column is mapped will legitimately
show a one-time burst of changes for that column. Add the column here when that
happens. Expected steady-state diff between rebuilds is small.
"""

from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

EXPECTED_CHANGE_COLS = {
    1,   # Name (occasional patch renames)
    2,   # Level (derived from class-level cols 104..119)
    3,   # Duration (source col 17)
    4,   # Beneficial (source col 83)
    6,   # Target (source col 98)
    7,   # ClassMask (derived from class-level cols 104..119)
    8,   # Damaging flag (derived from message presence)
    10,  # Resist (source col 85)
    11,  # SongWindow (source col 154)
    12,  # Adps (categories + upstream overlay)
    17,  # LandsOnYou
    18,  # LandsOnOther
    19,  # WearOff
    20,  # CastingTimeMs (source col 13)
    21,  # RecastTimeMs (source col 15)
    22,  # Category (semicolon-joined labels from cols 156/157/158)
    23,  # Skill (source col 100)
    24,  # RecourseID (source col 150)
    25,  # TimerID (source col 167)
}

COL_NAMES = {
    0: "Id", 1: "Name", 2: "Level", 3: "Duration", 4: "Beneficial",
    5: "MaxHits", 6: "Target", 7: "ClassMask", 8: "Damaging", 9: "CombatSkill",
    10: "Resist", 11: "SongWindow", 12: "Adps", 13: "Mgb", 14: "Rank",
    15: "HasAmbiguityA", 16: "HasAmbiguityB", 17: "LandsOnYou",
    18: "LandsOnOther", 19: "WearOff", 20: "CastingTimeMs", 21: "RecastTimeMs",
    22: "Category", 23: "Skill", 24: "RecourseID", 25: "TimerID",
}


def load(p: Path) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for raw in p.read_text(encoding="utf-8").splitlines():
        if not raw:
            continue
        fields = raw.split("^")
        out[fields[0]] = fields
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("old", help="path to previous spells.txt")
    parser.add_argument("new", help="path to new spells.txt")
    args = parser.parse_args()

    old = load(Path(args.old))
    new = load(Path(args.new))

    added = sorted(set(new) - set(old), key=int)
    removed = sorted(set(old) - set(new), key=int)
    common = sorted(set(old) & set(new), key=int)

    changed: list[str] = []
    column_changes: Counter[int] = Counter()
    for sid in common:
        if old[sid] != new[sid]:
            changed.append(sid)
            # Compare full length, not zip-truncated. When the format adds new
            # trailing columns, the missing values on the old side count as a
            # change in those columns (vs. the empty string).
            max_len = max(len(old[sid]), len(new[sid]))
            for i in range(max_len):
                a = old[sid][i] if i < len(old[sid]) else ""
                b = new[sid][i] if i < len(new[sid]) else ""
                if a != b:
                    column_changes[i] += 1

    print(f"old rows: {len(old)}")
    print(f"new rows: {len(new)}")
    print(f"added (new IDs):   {len(added)}")
    print(f"removed (gone IDs):{len(removed)}")
    print(f"changed rows:      {len(changed)}")
    print()
    print("changes by column:")
    for col, n in sorted(column_changes.items()):
        flag = "" if col in EXPECTED_CHANGE_COLS else "  <-- UNEXPECTED"
        print(f"  col {col:2d} ({COL_NAMES.get(col, '?'):14s}): {n}{flag}")

    if added:
        print(f"\nfirst 20 added IDs: {added[:20]}")
    if removed:
        print(f"first 20 removed IDs: {removed[:20]}")

    if changed:
        print("\nfirst 10 name renames (col 1 changes):")
        renames = [sid for sid in changed if old[sid][1] != new[sid][1]][:10]
        for sid in renames:
            print(f"  ID {sid}: {old[sid][1]!r} -> {new[sid][1]!r}")

    unexpected = sorted(set(column_changes) - EXPECTED_CHANGE_COLS)
    if unexpected:
        print(f"\n!! drift in unexpected columns: {unexpected}")
        return 1
    print("\nOK: all changes confined to expected columns.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
