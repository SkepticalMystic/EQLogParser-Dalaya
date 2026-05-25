#!/usr/bin/env python3
"""Convert Dalaya's spells_us.txt (game format) to EQLogParser's spells.txt (parser format).

Run after each Dalaya patch that updates spells_us.txt.

Usage:
    python convert_spells.py [SOURCE] [DEST]

Defaults:
    SOURCE = F:/Dalaya/spells_us.txt
    DEST   = ../../EQLogParser/data/spells.txt  (relative to this script)

Mapping (see CLAUDE.md and EQDataStore.ParseCustomSpellData):
    parser col  source col   meaning
    0           0            spell id
    1           1            spell name
    2           104..119     level    = min(non-255) across the 16 class-level cols
    3           17           duration (6-second ticks; EQDataStore multiplies by 6 for seconds)
    4           83           beneficial flag (0=detrimental, 1=beneficial, 2/3=beneficial-variants)
    6           98           target type     (Pet=14 enables auto pet detection)
    7           104..119     class mask = bitmask, bit(classid-1) set when col(103+classid) != 255
    8           -            damaging flag   (1 if any of src[6..8] non-empty)
    10          85           resist type (0=unr 1=mag 2=fire 3=cold 4=poison 5=disease 6=chrom 7=prism)
    15, 16      -            ambiguity flags (hard-coded "1", "1")
    17          6            lands-on-you message
    18          7            lands-on-other message
    19          8            wear-off message
    all others  -            hard-coded "0"

Source cols 104..119 hold per-class level requirements in EQ classid order:
  104 War, 105 Clr, 106 Pal, 107 Rng, 108 Shd, 109 Dru, 110 Mnk, 111 Brd,
  112 Rog, 113 Shm, 114 Nec, 115 Wiz, 116 Mag, 117 Enc, 118 Bst, 119 Ber.
A value of 255 means the class cannot learn the spell. Verified against
Minor/Light/Greater Healing (Cleric+Pal+Ranger+Druid+Shaman+Beastlord rows)
plus class-unique spells (Wolf Form, Camouflage, Frost Bolt, Cardiac Arrest,
Disintegrate, Chorus of Althuna, Inner Fire). Wiki cross-reference:
https://wiki.shardsofdalaya.com/wiki/<Class>_spells.

Still hard-coded "0" (no clean source mapping or Dalaya-specific concept):
  MaxHits (col 5), CombatSkill (9), SongWindow (11), Adps (12), Mgb (13), Rank (14).
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

DEFAULT_SOURCE = Path(r"F:/Dalaya/spells_us.txt")
DEFAULT_DEST = Path(__file__).resolve().parent.parent.parent / "EQLogParser" / "data" / "spells.txt"


CLASS_LEVEL_FIRST_COL = 104  # source col for classid=1 (Warrior); +15 reaches Berserker.


def _level_and_class_mask(src_fields: list[str]) -> tuple[str, str]:
    """Return (level, class_mask) derived from the 16 class-level columns.

    Level = lowest non-255 class level (or "255" if no class can cast).
    ClassMask = bitmask where bit(classid-1) is set when col(103+classid) != 255.
    """
    min_level = 256
    mask = 0
    for classid in range(1, 17):
        col = CLASS_LEVEL_FIRST_COL + (classid - 1)
        if col >= len(src_fields):
            continue
        raw = src_fields[col].strip()
        if not raw:
            continue
        try:
            lvl = int(raw)
        except ValueError:
            continue
        if lvl == 255:
            continue
        mask |= 1 << (classid - 1)
        if lvl < min_level:
            min_level = lvl
    if min_level == 256:
        return "255", "0"
    # Parser parses Level as byte (0-255). Source values up to 254 are valid;
    # 255 already excluded above.
    return str(min(min_level, 255)), str(mask)


def convert_line(src_fields: list[str]) -> str:
    spell_id = src_fields[0]
    name = src_fields[1]
    lands_on_you = src_fields[6]
    lands_on_other = src_fields[7]
    wear_off = src_fields[8]
    duration = src_fields[17] if len(src_fields) > 17 else "0"
    beneficial = src_fields[83] if len(src_fields) > 83 else "0"
    resist = src_fields[85] if len(src_fields) > 85 else "0"
    target = src_fields[98] if len(src_fields) > 98 else "5"
    level, class_mask = _level_and_class_mask(src_fields)
    damaging = "1" if (lands_on_you or lands_on_other or wear_off) else "0"

    return "^".join([
        spell_id,         # 0  Id
        name,             # 1  Name
        level,            # 2  Level (min class level; 255 = no class can cast)
        duration,         # 3  Duration (ticks)
        beneficial,       # 4  Beneficial (0=det, non-zero=beneficial)
        "0",              # 5  MaxHits
        target,           # 6  Target
        class_mask,       # 7  ClassMask (bitmask, classid-1 per bit)
        damaging,         # 8  Damaging
        "0",              # 9  CombatSkill
        resist,           # 10 Resist (0=unresistable, 1=magic, 2=fire, 3=cold, ...)
        "0",              # 11 SongWindow
        "0",              # 12 Adps
        "0",              # 13 Mgb
        "0",              # 14 Rank
        "1",              # 15 HasAmbiguity (a)
        "1",              # 16 HasAmbiguity (b)
        lands_on_you,     # 17 LandsOnYou
        lands_on_other,   # 18 LandsOnOther
        wear_off,         # 19 WearOff
    ])


def convert(source: Path, dest: Path) -> tuple[int, int]:
    written = 0
    skipped = 0
    out_lines: list[str] = []
    with source.open("r", encoding="utf-8", newline="") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            if not line:
                skipped += 1
                continue
            fields = line.split("^")
            if len(fields) < 9:
                skipped += 1
                continue
            out_lines.append(convert_line(fields))
            written += 1

    dest.parent.mkdir(parents=True, exist_ok=True)
    # Match the existing file's encoding/line endings: ASCII, LF, trailing newline.
    with dest.open("w", encoding="utf-8", newline="\n") as f:
        for line in out_lines:
            f.write(line + "\n")

    return written, skipped


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", nargs="?", default=str(DEFAULT_SOURCE), help=f"path to spells_us.txt (default: {DEFAULT_SOURCE})")
    parser.add_argument("dest", nargs="?", default=str(DEFAULT_DEST), help=f"path to spells.txt output (default: {DEFAULT_DEST})")
    args = parser.parse_args()

    source = Path(args.source)
    dest = Path(args.dest)

    if not source.is_file():
        print(f"error: source not found: {source}", file=sys.stderr)
        return 1

    written, skipped = convert(source, dest)
    print(f"wrote {written} spells to {dest}")
    if skipped:
        print(f"skipped {skipped} blank/short lines")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
