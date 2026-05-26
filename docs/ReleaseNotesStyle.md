# Release notes style

> **Purpose:** Convention for the GitHub release page body and the bundled `data/releasenotes.rtf` entries. Settled 2026-05-26 after the 1.0.5 / 1.0.8 / 1.1.0 / 1.1.1 releases each picked a different shape. Read before drafting any release.

## Always

- **Title:** `EQLogParser-Dalaya X.Y.Z` (no body duplication — earlier releases accidentally repeated the title as an H1 inside the body)
- **Inline code** (backticks) for paths, file names, sample log lines, config strings, CLI args
- **Bold** for UI elements, menu items, feature names (e.g. **DPS Summary**, **Help → About**, **Set as Verified Player**)
- **No footer line.** Skip auto-update / installer mentions — users either run the installer or get the in-app prompt; the release page itself doesn't need to say so.

## Body shape — pick one

### Compact format (default)

Use when the release is many small items, each describable in 1–3 sentences. Most releases land here.

```markdown
## Changes
1. **Topic**: description.
2. **Topic**: description.
3. **Topic**: description.
```

Section header is `## Changes` for a mix, `## Fixes` if everything is a bug fix. Use both sections if there's a clean split.

### Detailed format

Use when the release is dominated by 1–3 substantial features that each need explanation (paragraphs, sub-bullets, sample log lines).

```markdown
One-line headline summary if there's a flagship feature.

### Feature name
Paragraph(s) explaining what + why. Bullets for sub-points where helpful.

### Another feature
...
```

No outer section heading — the H3s carry the structure.

### Heuristic for choosing

If any single item needs more than ~3 sentences to describe, use the detailed format. Otherwise the compact format. Splitting hairs: lean compact.

## RTF entries

The bundled `data/releasenotes.rtf` shown in the in-app **Release Notes** window uses the same content but in RTF syntax. Conventions for those:

- Header line: `{\pard \ql \f0 \sa180 \li0 \fi0 \outlinelevel0 \b \fs24 X.Y.Z | MM/DD/YY\par}`
- Each numbered item: `{\pard \ql \f0 \fs20 \sa0 \li720 \fi-360 N.\tx360\tab ... \par}`
- Last item of a release uses `\sa180\par` (extra space-after) instead of `\par` to separate releases
- Use `{\b text}` for bold; same content as the GitHub markdown
- Existing entries in the file are the reference — copy the structure exactly when adding a new release

The compact format maps cleanly to RTF (each numbered item → one paragraph). The detailed format with H3 subsections doesn't map as cleanly; in practice the RTF entry can be a compact summary even when the GitHub release uses the detailed format.
