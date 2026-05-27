#!/usr/bin/env python3
"""Extract spell categories from Dalaya's dbstr_us.txt.

Produces tools/spells/dalaya-categories.json: a {category_id: label} map for
type=5 entries in dbstr_us.txt. The labels are the same ones the Shards of
Dalaya wiki renders on per-spell pages (e.g. "HP Buffs", "Haste",
"Damage Shield") — they come from the game's own classification.

convert_spells.py reads this JSON alongside source cols 156/157/158
(CategoryDescID[0..2]) on each spell to apply a category-based ADPS bitmask.

dbstr_us.txt format:
    <Major>^<Minor>^<String>
    Major = id within type
    Minor = type (5 = spell categories, 6 = spell descriptions, etc.)

Discovered via review of ngdeao/SoD-winspellparser/SpellParser.cs (a Dalaya-
specific spell parser that exposes the full source-file schema).

Usage:
    python extract_dalaya_categories.py [DBSTR_US_TXT] [OUTPUT_JSON]

Defaults:
    DBSTR_US_TXT = F:/Dalaya/dbstr_us.txt
    OUTPUT_JSON  = tools/spells/dalaya-categories.json (relative to script)
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

DEFAULT_SOURCE = Path(r"F:/Dalaya/dbstr_us.txt")
DEFAULT_OUTPUT = Path(__file__).resolve().parent / "dalaya-categories.json"

CATEGORY_TYPE = "5"


def extract(source: Path) -> dict[str, str]:
    """Return {category_id: label} for type-5 entries (spell categories)."""
    categories: dict[str, str] = {}
    with source.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line:
                continue
            fields = line.split("^")
            if len(fields) < 3:
                continue
            major, minor, label = fields[0], fields[1], fields[2]
            if minor == CATEGORY_TYPE:
                categories[major] = label.strip()
    return categories


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("source", nargs="?", default=str(DEFAULT_SOURCE), help=f"path to dbstr_us.txt (default: {DEFAULT_SOURCE})")
    parser.add_argument("output", nargs="?", default=str(DEFAULT_OUTPUT), help=f"path to output JSON (default: {DEFAULT_OUTPUT})")
    args = parser.parse_args()

    source = Path(args.source)
    output = Path(args.output)

    if not source.is_file():
        print(f"error: source not found: {source}", file=sys.stderr)
        return 1

    categories = extract(source)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(categories, f, indent=2, sort_keys=True, ensure_ascii=False)
        f.write("\n")
    print(f"wrote {len(categories)} category labels to {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
