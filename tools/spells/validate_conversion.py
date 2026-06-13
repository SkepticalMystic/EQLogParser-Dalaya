"""Cross-check the shipped spells.txt against an independent reading of spells_us.txt.

This is a defense-in-depth check that reads Dalaya's game-format `spells_us.txt`
a *second*, independent way and compares the result against the parser-format
`spells.txt` that `convert_spells.py` produces. It catches two failure modes:

  1. Conversion bugs  — `convert_spells.py` reads the wrong source column, or a
     transform regresses. The column-meaning table below is maintained
     independently of the converter (sourced from ngdeao/SoD-winspellparser's
     `SpellParser.cs` LoadSpell, not imported from convert_spells.py), so a
     mapping mistake in one shows up as a disagreement against the other.
  2. Source-format drift — a Dalaya patch shifts/adds source columns. The
     per-field domain checks (Phase A) flag values that no longer look like the
     field they're supposed to be (e.g. a message string where Beneficial should
     be an int), and the field comparison (Phase B) flags shipped rows that no
     longer match a fresh reading of the patched source.

Usage:
    python validate_conversion.py [SOURCE] [SHIPPED]

Defaults:
    SOURCE  = F:/Dalaya/spells_us.txt
    SHIPPED = ../../EQLogParser/data/spells.txt  (relative to this script)

Exit code 0 when source sanity passes and every comparable field agrees;
non-zero otherwise — so it can gate a rebuild the same way analyze_diff.py does.

DELIBERATE DUPLICATION: the derivations below intentionally re-implement the
converter's field logic rather than importing convert_spells.py. Two
independently-maintained readings is the whole point — if they drift apart, that
is the signal. Keep this file's column constants in sync with the SoD source,
not with the converter.
"""

from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

DEFAULT_SOURCE = Path(r"F:/Dalaya/spells_us.txt")
DEFAULT_SHIPPED = Path(__file__).resolve().parent.parent.parent / "EQLogParser" / "data" / "spells.txt"

# Modal source-column width. spells_us.txt is the live-EQ-style 239-column
# format (confirmed against SpellParser.cs). Rows deviating from this — or a
# wholesale change to the modal width — signal a source-format change.
EXPECTED_SOURCE_COLS = 239

# A real column shift trips a domain check on essentially every row; a handful
# of pre-existing source-data quirks (e.g. a typo'd class level) trips only a
# few. Below this many violations we report the field as an informational
# WARNING; at or above it we treat it as drift and fail.
DOMAIN_DRIFT_THRESHOLD = 60

# --- Independent SoD column table (authority: ngdeao/SoD-winspellparser
# SpellParser.cs LoadSpell). Maintained separately from convert_spells.py. ---
SRC_ID = 0
SRC_NAME = 1
SRC_LANDS_ON_YOU = 6
SRC_LANDS_ON_OTHER = 7
SRC_WEAR_OFF = 8
SRC_CAST_TIME_MS = 13       # SpellParser: CastingTime = fields[13] / 1000 (s)
SRC_RECAST_TIME_MS = 15
SRC_DURATION_BASE = 17
SRC_BENEFICIAL = 83
SRC_RESIST_TYPE = 85
SRC_TARGET_TYPE = 98
SRC_SHORT_DURATION = 154    # parser SongWindow
SRC_CLASS_LEVEL_FIRST = 104  # classid=1 (War); +15 reaches Berserker (Ber)

# Parser-format column names (matches analyze_diff.py / convert_spells.py).
COL_NAMES = {
    0: "Id", 1: "Name", 2: "Level", 3: "Duration", 4: "Beneficial",
    5: "MaxHits", 6: "Target", 7: "ClassMask", 8: "Damaging", 9: "CombatSkill",
    10: "Resist", 11: "SongWindow", 12: "Adps", 13: "Mgb", 14: "Rank",
    15: "HasAmbiguityA", 16: "HasAmbiguityB", 17: "LandsOnYou",
    18: "LandsOnOther", 19: "WearOff", 20: "CastingTimeMs", 21: "RecastTimeMs",
    22: "Category",
}

# Parser columns deliberately NOT comparable here: derived from data this script
# has no independent authority for, or hard-coded by the converter.
#   5  MaxHits      — hard-coded "0" (Dalaya doesn't populate it)
#   9  CombatSkill  — hard-coded "0"
#   12 Adps         — categories + upstream overlay (validated by validate_adps.py)
#   13 Mgb          — hard-coded "0"
#   14 Rank         — hard-coded "0"
#   15 HasAmbiguityA, 16 HasAmbiguityB — hard-coded "1"
#   22 Category     — semicolon-joined label lookup (validated by validate_adps.py)
SKIP_COLS = frozenset({5, 9, 12, 13, 14, 15, 16, 22})


def _get(fields: list[str], col: int, default: str = "") -> str:
    return fields[col] if col < len(fields) else default


def _safe_int_str(fields: list[str], col: int) -> str:
    """Float-tolerant non-negative int string, mirroring the converter's ms handling."""
    raw = _get(fields, col).strip()
    if not raw:
        return "0"
    try:
        return str(max(0, int(float(raw))))
    except ValueError:
        return "0"


def _level_and_class_mask(fields: list[str]) -> tuple[str, str]:
    """(level, class_mask) from the 16 class-level cols. Independent re-derivation.

    Level = lowest non-255 class level (or "255"); ClassMask = bit(classid-1)
    set when that class's level column is not 255.
    """
    min_level = 256
    mask = 0
    for classid in range(1, 17):
        raw = _get(fields, SRC_CLASS_LEVEL_FIRST + (classid - 1)).strip()
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
    return str(min(min_level, 255)), str(mask)


# (parser_col, derive_fn) — independent expected value for each comparable field.
FIELD_DERIVATIONS = [
    (0, lambda f: _get(f, SRC_ID)),
    (1, lambda f: _get(f, SRC_NAME)),
    (2, lambda f: _level_and_class_mask(f)[0]),
    (3, lambda f: _get(f, SRC_DURATION_BASE, "0")),
    (4, lambda f: _get(f, SRC_BENEFICIAL, "0")),
    (6, lambda f: _get(f, SRC_TARGET_TYPE, "5")),
    (7, lambda f: _level_and_class_mask(f)[1]),
    (8, lambda f: "1" if (_get(f, SRC_LANDS_ON_YOU) or _get(f, SRC_LANDS_ON_OTHER) or _get(f, SRC_WEAR_OFF)) else "0"),
    (10, lambda f: _get(f, SRC_RESIST_TYPE, "0")),
    (11, lambda f: _get(f, SRC_SHORT_DURATION, "0")),
    (17, lambda f: _get(f, SRC_LANDS_ON_YOU)),
    (18, lambda f: _get(f, SRC_LANDS_ON_OTHER)),
    (19, lambda f: _get(f, SRC_WEAR_OFF)),
    (20, lambda f: _safe_int_str(f, SRC_CAST_TIME_MS)),
    (21, lambda f: _safe_int_str(f, SRC_RECAST_TIME_MS)),
]


def load_source(path: Path) -> tuple[dict[str, list[str]], list[str], Counter[int]]:
    """Return (id -> fields, duplicate-id list, column-width histogram)."""
    by_id: dict[str, list[str]] = {}
    dup_ids: list[str] = []
    width_hist: Counter[int] = Counter()
    with path.open("r", encoding="utf-8", newline="") as fh:
        for raw in fh:
            line = raw.rstrip("\r\n")
            if not line:
                continue
            fields = line.split("^")
            if len(fields) < 9:
                continue
            width_hist[len(fields)] += 1
            sid = fields[SRC_ID]
            if sid in by_id:
                dup_ids.append(sid)
            by_id[sid] = fields
    return by_id, dup_ids, width_hist


def load_shipped(path: Path) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        if not raw:
            continue
        fields = raw.split("^")
        out[fields[0]] = fields
    return out


def _is_int_in(raw: str, lo: int, hi: int) -> bool:
    raw = raw.strip()
    if raw == "":
        return True  # blank is an acceptable "unset" for class-level cols
    try:
        return lo <= int(raw) <= hi
    except ValueError:
        return False


def _is_nonneg_int(raw: str) -> bool:
    raw = raw.strip()
    if raw == "":
        return True
    try:
        return int(raw) >= 0
    except ValueError:
        return False


def _is_nonneg_float(raw: str) -> bool:
    """Cast/recast times are float ms in the source; blank or >= 0 is acceptable."""
    raw = raw.strip()
    if raw == "":
        return True
    try:
        return float(raw) >= 0
    except ValueError:
        return False


def check_source_sanity(by_id: dict[str, list[str]], width_hist: Counter[int]) -> tuple[list[str], list[tuple[str, int, int, list[str]]]]:
    """Phase A -- detect source-format drift.

    Returns (width_problems, domain_findings). width_problems are always hard
    failures. Each domain finding is (label, source_col, count, sample_ids); the
    caller escalates to failure only when count >= DOMAIN_DRIFT_THRESHOLD.
    """
    width_problems: list[str] = []

    modal_width, modal_count = width_hist.most_common(1)[0]
    if modal_width != EXPECTED_SOURCE_COLS:
        width_problems.append(
            f"source modal column width is {modal_width} (expected {EXPECTED_SOURCE_COLS}) "
            f"across {modal_count} rows -- the source format likely changed; revisit the column table"
        )
    off_width = sum(n for w, n in width_hist.items() if w != modal_width)
    if off_width:
        odd = sorted(w for w in width_hist if w != modal_width)
        width_problems.append(f"{off_width} source row(s) deviate from modal width {modal_width}: widths {odd}")

    # Per-field domain checks. Bounds are generous -- they exist to catch a
    # column SHIFT (which lands a string or wildly out-of-range value in the
    # slot), not to validate exact enum membership. Each check returns a
    # predicate; we tally violations and sample ids per field.
    checks = [
        ("Beneficial", SRC_BENEFICIAL, lambda f: _is_int_in(_get(f, SRC_BENEFICIAL, "0"), 0, 15)),
        ("ResistType", SRC_RESIST_TYPE, lambda f: _is_int_in(_get(f, SRC_RESIST_TYPE, "0"), 0, 15)),
        ("TargetType", SRC_TARGET_TYPE, lambda f: _is_int_in(_get(f, SRC_TARGET_TYPE, "0"), 0, 255)),
        # DurationBase: no upper cap -- permanent buffs use large sentinels
        # (e.g. 9999999, 500000 ticks). Only a non-integer signals a shift.
        ("DurationBase", SRC_DURATION_BASE, lambda f: _is_nonneg_int(_get(f, SRC_DURATION_BASE, "0"))),
        ("class-level cols", SRC_CLASS_LEVEL_FIRST,
         lambda f: all(_is_int_in(_get(f, SRC_CLASS_LEVEL_FIRST + i, ""), 0, 255) for i in range(16))),
        ("CastingTime", SRC_CAST_TIME_MS, lambda f: _is_nonneg_float(_get(f, SRC_CAST_TIME_MS, "0"))),
        ("RecastTime", SRC_RECAST_TIME_MS, lambda f: _is_nonneg_float(_get(f, SRC_RECAST_TIME_MS, "0"))),
    ]

    findings: list[tuple[str, int, int, list[str]]] = []
    for label, col, ok in checks:
        count = 0
        samples: list[str] = []
        for sid, fields in by_id.items():
            if not ok(fields):
                count += 1
                if len(samples) < 5:
                    samples.append(sid)
        if count:
            findings.append((label, col, count, samples))

    return width_problems, findings


def cross_check(by_id: dict[str, list[str]], shipped: dict[str, list[str]]) -> tuple[Counter[int], dict[int, list[tuple[str, str, str]]]]:
    """Phase B — per-field disagreement counts + sample mismatches by parser col."""
    mismatches: Counter[int] = Counter()
    samples: dict[int, list[tuple[str, str, str]]] = {}
    for sid in by_id.keys() & shipped.keys():
        src_fields = by_id[sid]
        ship = shipped[sid]
        for parser_col, derive in FIELD_DERIVATIONS:
            if parser_col in SKIP_COLS:
                continue
            expected = derive(src_fields)
            actual = ship[parser_col] if parser_col < len(ship) else ""
            if expected != actual:
                mismatches[parser_col] += 1
                bucket = samples.setdefault(parser_col, [])
                if len(bucket) < 5:
                    bucket.append((sid, expected, actual))
    return mismatches, samples


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", nargs="?", default=str(DEFAULT_SOURCE), help=f"path to spells_us.txt (default: {DEFAULT_SOURCE})")
    parser.add_argument("shipped", nargs="?", default=str(DEFAULT_SHIPPED), help=f"path to shipped spells.txt (default: {DEFAULT_SHIPPED})")
    args = parser.parse_args()

    source = Path(args.source)
    shipped_path = Path(args.shipped)

    if not source.is_file():
        print(f"error: source not found: {source}", file=sys.stderr)
        return 2
    if not shipped_path.is_file():
        print(f"error: shipped spells.txt not found: {shipped_path}", file=sys.stderr)
        return 2

    by_id, dup_ids, width_hist = load_source(source)
    shipped = load_shipped(shipped_path)

    print(f"source rows:  {len(by_id)}  ({source})")
    print(f"shipped rows: {len(shipped)}  ({shipped_path})")

    failed = False

    # Roster: shipped should be a faithful 1:1 of the source IDs.
    only_source = sorted(by_id.keys() - shipped.keys(), key=lambda s: int(s) if s.lstrip("-").isdigit() else 0)
    only_shipped = sorted(shipped.keys() - by_id.keys(), key=lambda s: int(s) if s.lstrip("-").isdigit() else 0)
    if dup_ids:
        print(f"\n!! {len(dup_ids)} duplicate id(s) in source (last wins): {dup_ids[:10]}")
        failed = True
    if only_source:
        print(f"\n!! {len(only_source)} source id(s) missing from shipped (stale spells.txt?): {only_source[:20]}")
        failed = True
    if only_shipped:
        print(f"\n!! {len(only_shipped)} shipped id(s) not in source (removed upstream?): {only_shipped[:20]}")
        failed = True

    print("\n--- Phase A: source-format sanity ---")
    width_problems, domain_findings = check_source_sanity(by_id, width_hist)
    for p in width_problems:
        print(f"  !! {p}")
        failed = True
    for label, col, count, samples in domain_findings:
        drift = count >= DOMAIN_DRIFT_THRESHOLD
        tag = "!!" if drift else "warn:"
        verdict = "likely COLUMN SHIFT" if drift else f"below drift threshold ({DOMAIN_DRIFT_THRESHOLD}); likely source-data quirk"
        print(f"  {tag} {count} row(s) out-of-domain {label} at source col {col} -- {verdict}; e.g. ids {samples}")
        if drift:
            failed = True
    if not width_problems and not domain_findings:
        print(f"  OK: modal width {EXPECTED_SOURCE_COLS}, all sampled fields in domain.")
    elif not width_problems and not any(c >= DOMAIN_DRIFT_THRESHOLD for _, _, c, _ in domain_findings):
        print(f"  OK: modal width {EXPECTED_SOURCE_COLS}; warnings above are pre-existing data quirks, not drift.")

    print("\n--- Phase B: field-by-field cross-check vs shipped ---")
    mismatches, samples = cross_check(by_id, shipped)
    if not mismatches:
        compared = sorted(c for c, _ in FIELD_DERIVATIONS if c not in SKIP_COLS)
        print(f"  OK: all comparable fields agree (cols {compared}).")
    else:
        for col in sorted(mismatches):
            print(f"  col {col:2d} ({COL_NAMES.get(col, '?'):14s}): {mismatches[col]} disagreement(s)")
            for sid, expected, actual in samples.get(col, []):
                print(f"        id {sid}: source-reading {expected!r} != shipped {actual!r}")
        failed = True

    if failed:
        print("\nFAILED: see flagged items above.")
        return 1
    print("\nOK: shipped spells.txt is consistent with an independent reading of the source.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
