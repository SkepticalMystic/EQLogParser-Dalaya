#!/usr/bin/env python3
"""Convert Dalaya's spells_us.txt (game format) to EQLogParser's spells.txt (parser format).

Run after each Dalaya patch that updates spells_us.txt.

Usage:
    python convert_spells.py [SOURCE] [DEST]

Defaults:
    SOURCE = F:/Dalaya/spells_us.txt
    DEST   = ../../EQLogParser/data/spells.txt  (relative to this script)

Mapping (see CLAUDE.md and DataManager.ParseCustomSpellData):
    parser col  source col   meaning
    0           0            spell id
    1           1            spell name
    6           -            target type     (hard-coded "5")
    8           -            damaging flag   (1 if any of src[6..8] non-empty)
    15, 16      -            ambiguity flags (hard-coded "1", "1")
    17          6            lands-on-you message
    18          7            lands-on-other message
    19          8            wear-off message
    all others  -            hard-coded "0"

The other parser columns (level/duration/beneficial/maxhits/classmask/etc.) are
intentionally zeroed in the Dalaya build — Dalaya only uses spells.txt to match
cast/lands-on/wear-off text from log lines, not for class/level metadata.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

DEFAULT_SOURCE = Path(r"F:/Dalaya/spells_us.txt")
DEFAULT_DEST = Path(__file__).resolve().parent.parent.parent / "EQLogParser" / "data" / "spells.txt"


def convert_line(src_fields: list[str]) -> str:
    spell_id = src_fields[0]
    name = src_fields[1]
    lands_on_you = src_fields[6]
    lands_on_other = src_fields[7]
    wear_off = src_fields[8]
    damaging = "1" if (lands_on_you or lands_on_other or wear_off) else "0"

    return "^".join([
        spell_id,         # 0  Id
        name,             # 1  Name
        "0",              # 2  Level
        "0",              # 3  Duration
        "0",              # 4  Beneficial
        "0",              # 5  MaxHits
        "5",              # 6  Target
        "0",              # 7  ClassMask
        damaging,         # 8  Damaging
        "0",              # 9  CombatSkill
        "0",              # 10 Resist
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
