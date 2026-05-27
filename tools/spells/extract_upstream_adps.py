#!/usr/bin/env python3
"""Extract Adps classifications from upstream EQLogParser's spells.txt.

Produces tools/spells/upstream-adps.json: a {spell_name: adps_bitmask} map used
by convert_spells.py to stamp parser col 12 (Adps) onto Dalaya spells whose
names match a classified upstream entry. See README.md for the full pipeline.

Adps is a bitmask consumed by the Timeline UI:
    1 = CasterAdps   2 = MeleeAdps   4 = TankAdps   8 = HealingAdps

When the same name appears in multiple upstream entries (rank duplicates) with
different Adps values, we union the bitmasks — if upstream classifies it as
Caster in one row and Melee in another, surface it on both timelines rather
than picking one and hiding the other.

By default, output is filtered to only names that appear in Dalaya's current
spells.txt — the rest is noise from this repo's perspective. Pass --no-filter
to emit the full upstream map (~14K entries, ~425KB) for debugging.

Usage:
    python extract_upstream_adps.py [UPSTREAM_SPELLS_TXT] [OUTPUT_JSON]

Defaults:
    UPSTREAM_SPELLS_TXT = downloaded fresh from kauffman12/EQLogParser master
    OUTPUT_JSON         = tools/spells/upstream-adps.json (relative to script)

The downloaded upstream file is cached at %TEMP%/upstream_spells.txt; re-running
without arguments re-downloads. Pass a local path to skip the download.
"""

from __future__ import annotations

import argparse
import json
import sys
import tempfile
import urllib.request
from pathlib import Path

UPSTREAM_URL = "https://raw.githubusercontent.com/kauffman12/EQLogParser/master/EQLogParser/data/spells.txt"
DEFAULT_OUTPUT = Path(__file__).resolve().parent / "upstream-adps.json"
DEFAULT_DALAYA_SPELLS = Path(__file__).resolve().parent.parent.parent / "EQLogParser" / "data" / "spells.txt"
CACHE_PATH = Path(tempfile.gettempdir()) / "upstream_spells.txt"


def download_upstream(dest: Path) -> None:
    print(f"downloading {UPSTREAM_URL} -> {dest}")
    with urllib.request.urlopen(UPSTREAM_URL) as resp:
        dest.write_bytes(resp.read())


def extract(source: Path) -> dict[str, int]:
    """Return {name: adps_bitmask} for upstream entries with Adps != 0."""
    by_name: dict[str, int] = {}
    with source.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line:
                continue
            fields = line.split("^")
            if len(fields) < 20:
                continue
            name = fields[1]
            try:
                adps = int(fields[12])
            except ValueError:
                continue
            if adps == 0:
                continue
            by_name[name] = by_name.get(name, 0) | adps
    return by_name


def load_dalaya_names(path: Path) -> set[str]:
    names: set[str] = set()
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line:
                continue
            fields = line.split("^")
            if len(fields) >= 2:
                names.add(fields[1])
    return names


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "source",
        nargs="?",
        default=None,
        help=f"path to upstream spells.txt (default: download from {UPSTREAM_URL})",
    )
    parser.add_argument(
        "output",
        nargs="?",
        default=str(DEFAULT_OUTPUT),
        help=f"path to output JSON (default: {DEFAULT_OUTPUT})",
    )
    parser.add_argument(
        "--no-filter",
        action="store_true",
        help="emit the full upstream map without filtering against Dalaya's spell names",
    )
    parser.add_argument(
        "--dalaya-spells",
        default=str(DEFAULT_DALAYA_SPELLS),
        help=f"path to Dalaya spells.txt used for filtering (default: {DEFAULT_DALAYA_SPELLS})",
    )
    args = parser.parse_args()

    if args.source is None:
        download_upstream(CACHE_PATH)
        source = CACHE_PATH
    else:
        source = Path(args.source)

    if not source.is_file():
        print(f"error: source not found: {source}", file=sys.stderr)
        return 1

    classifications = extract(source)
    raw_count = len(classifications)
    if not args.no_filter:
        dalaya_path = Path(args.dalaya_spells)
        if not dalaya_path.is_file():
            print(f"error: dalaya spells.txt not found: {dalaya_path}", file=sys.stderr)
            return 1
        dalaya_names = load_dalaya_names(dalaya_path)
        classifications = {n: a for n, a in classifications.items() if n in dalaya_names}
        print(f"filtered {raw_count} upstream entries -> {len(classifications)} that match Dalaya names")
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(classifications, f, indent=2, sort_keys=True, ensure_ascii=False)
        f.write("\n")
    print(f"wrote {len(classifications)} classified names to {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
