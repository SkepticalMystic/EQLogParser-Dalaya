# Website guide authoring & screenshots

> **Purpose:** How to write/update a `website/dalaya/guides/*.html` page and capture real
> in-app screenshots for it, using synthetic data so nothing from a real player's log ever
> reaches the public site. Read this before creating a new guide or reworking an existing one.

## Guide file conventions (recap)

The full structure lives in the root `CLAUDE.md` under "GitHub Pages site" — read that first.
Short version: one HTML file per guide under `website/dalaya/guides/`, add an entry to the
`GUIDES` array in `guides/nav.js` to wire up the sidebar/prev-next, call `renderNav("<id>")` at
the bottom of the new file. `guides/index.html` picks the card up automatically.

For screenshots inside a guide, use the `.guide-screenshot` CSS class (added in `css/style.css`
alongside this tooling):

```html
<img class="guide-screenshot" src="images/<guide-id>-<step>.png"
     alt="..." style="max-width: 420px;" />
```

Images live in `website/dalaya/guides/images/`, named `<guide-id>-<short-step-name>.png`. Set
`style="max-width: ..."` per image to roughly its native width so it doesn't stretch — the
class itself only handles border/radius/background.

## Why this needs OS-level automation, not the Browser pane

EQLogParser-Dalaya is a native WPF desktop app, not a web page — the Browser pane's tools
(`computer`, `read_page`, etc.) only drive an actual browser tab. There's no accessibility-tree
equivalent available for the desktop window, so this is coordinate-based automation: screenshot
the window, eyeball pixel coordinates for the next click from that image, click, repeat.
`ui-helpers.ps1` in this folder wraps the Win32 calls needed for that loop.

**Dot-source it at the top of every PowerShell tool call that needs it** — the PowerShell tool
does not persist variables/types between separate tool invocations, only the working directory
does:

```powershell
. "<repo-root>\EQLogParser\tools\guide-screenshots\ui-helpers.ps1"
```

### Function reference

| Function | Use |
|---|---|
| `Start-AppAndWait $exePath` | Launches the app, polls until the main window has a handle/title, returns the process object |
| `Focus-App $procId` | Brings the app window to the foreground before clicking |
| `Get-AppRect $procId` | Returns the app window's screen `RECT` |
| `Click-At $x $y` | Left-click at screen coordinates |
| `CtrlClick-At $x $y` | Ctrl+left-click (multi-select rows) |
| `RightClick-At $x $y` | Right-click (context menus) |
| `Type-AndEnter $text` | Types into whatever control has focus, then presses Enter — for native dialog filename boxes |
| `Screenshot-AppWindow $procId $outPath` | Screenshots **only** the app's window rect |
| `Screenshot-FullDesktop $outPath` | Screenshots the whole screen — only for locating native Open/Save dialogs (see below) |
| `Crop-Image $inPath $outPath $x $y $w $h` | Crops a PNG to a sub-rectangle |

### Finding a floating window's true bounds

Floating child windows (like the Spell Browser) don't necessarily open where you'd guess relative
to the main window content behind them. Don't estimate a crop rectangle from a mental model of the
layout — crop a generous, deliberately-oversized region first, read it back, then narrow in on the
actual edges (title bar, close button, bottom status text) before committing to a final tight crop.
Guessing the rectangle up front reliably produces a crop that's cut off on one side and bleeding
background content on the other.

### Finding click coordinates reliably

The `Read` tool often displays a screenshot scaled down (e.g. "displayed at 2000x1087" for a
2560x1391 original) and reports coordinates in the *displayed* size. If you eyeball a position
from that description, multiply by `original / displayed` before passing it to `Click-At` — using
the displayed number directly is a frequent source of clicks landing tens of pixels off target.
Safer approach: `Crop-Image` a small region (guess-and-check is fine) until the target text sits
clearly inside the crop, then compute the click point from the **crop's offset + local position
within the crop**, not from the original screenshot's displayed rendering.

Some Syncfusion `SfDataGrid` rows (e.g. the "Merged Fights" grid in the Raid Damage view) didn't
visibly respond to `Click-At` after several coordinate-corrected attempts — no row highlight, no
downstream panel update — despite the same click pattern working fine on other grids (Fight List,
DPS Summary). Similarly, right-clicking a row in the Trigger Manager's "Manage Characters" list
opened the *Fight List's* context menu instead of a characters-list one, even though the click
coordinates were well inside the characters panel. Cause unconfirmed in both cases — possibly a
docked-panel z-order/focus quirk specific to certain panel combinations. If a control won't respond
correctly after 2-3 careful attempts, don't keep burning turns on it — fall back to a screenshot of
whatever state you already have (e.g. the loaded-but-unselected list, or an empty property-grid
template showing just field labels) if it still demonstrates the guide's point; it usually does.

## End-to-end recipe

1. **Build the app** if `EQLogParser/EQLogParser/bin/x64/Debug/net8.0-windows10.0.17763.0/EQLogParser-Dalaya.exe` doesn't exist yet: `dotnet build -p:Platform=x64` (see root `CLAUDE.md`).
2. **Create a dummy log** — copy `sample-dummy-log.txt` (or write a new one following its format) to a scratch path named `eqlog_<SomeName>_dalaya.txt`. See "Dummy data rules" below — this is the step that must not be skipped.
3. **Launch**: `Start-AppAndWait $exePath` → note the returned process's `Id`.
4. **Open the dummy log**: `Focus-App`, `Click-At` the sidebar "Open Log" button (or the File menu's "Open and Monitor Log File"), `Click-At` "Everything" in its flyout (guarantees every line loads regardless of the dummy timestamps vs. real wall-clock time) — this opens a native `CommonOpenFileDialog`. Call `Type-AndEnter` with the full dummy-log path **immediately, with no `Click-At` in between** — the filename box already has keyboard focus when the dialog opens, and clicking anywhere in the dialog (even at what looks like the filename box) hands focus to the file list instead, silently swallowing everything you type afterward. If you do need to click inside a native dialog for some other reason, get its coordinates from `Screenshot-FullDesktop` (see next step), never from `Screenshot-AppWindow` — see the pitfall below.
5. **Drive the UI** for whatever the guide needs — `Click-At` / `CtrlClick-At` / `RightClick-At` fight rows, menu items, etc. Screenshot after each step with `Screenshot-AppWindow`, `Read` the PNG to see the result and find the next click's coordinates.
6. **Native Open/Save dialogs** are separate top-level windows outside the app's rect. To interact with one: `Screenshot-FullDesktop`, `Read` it to locate the dialog, then `Crop-Image` immediately to just the dialog region and discard the full-desktop capture (see privacy note below) before doing anything else with it. **Never call `Screenshot-AppWindow` while a native dialog is open** — once the dialog has focus, `Get-AppRect` (which reads the process's `MainWindowHandle`) starts returning the *dialog's* rect instead of the main window's, at a different apparent scale than the real screen (a coordinate pulled from that image and fed back into `Click-At` lands nowhere near the intended control). Stick to `Screenshot-FullDesktop` for any coordinate-finding for as long as a dialog is on screen.
7. **Crop each final screenshot** with `Crop-Image` down to just the relevant panel/menu/dialog — tight crops keep guide images small and avoid dragging in unrelated UI chrome.
8. **Copy finals** into `website/dalaya/guides/images/` and reference them from the guide HTML.
9. **Verify** the guide renders (see "Local verification" below).
10. **Close the app** when done: `Stop-Process -Id $procId -Force`.

## Dummy data rules — read before capturing anything

**Never open a real player's log file for a guide screenshot, and never let a screenshot show
real recent-files or real folder names.** This site is public. Two concrete traps found the
first time this was done (2026-07-13), both from a session that had a real log open before the
dummy log existed:

1. **The File menu's recent-files list** (`recent1File`…`recent6File` in `MainWindow.xaml`) is a
   sibling of "Open and Monitor Log File" in the same dropdown — opening the File menu at all
   shows it, with real file paths and real character names. Crop screenshots of the File menu
   down to just the top row (menu bar + the "Open and Monitor Log File" item itself), never the
   full dropdown, unless the recent-files entries are already all dummy/scratch paths.
2. **Native Open/Save dialogs default to the user's last-used real folder** (Documents, a
   OneDrive personal folder, etc.), and the folder browser pane shows real folder names (game
   library folders, personal account name in the sidebar breadcrumb). Either redirect the dialog
   to a scratch path with `Type-AndEnter` before screenshotting anything, or crop the screenshot
   down to just the filename field / Save-as-type row / buttons — never the folder browser,
   breadcrumb, or sidebar.
3. **The Trigger Manager's "Manage Characters" list is independent of which log is open** (found
   2026-07-18) — it lists every real character that has triggers configured, and stays populated
   with real names even after switching to a dummy log, because it's driven by trigger config, not
   the current log file. If a guide screenshot's crop region could include the Trigger Manager
   panel, either close/avoid that tab or crop it out entirely — opening a dummy log does **not**
   clean this panel up the way it does the Fight List.

General rule: prefer `Screenshot-AppWindow` (cropped to the app's own rect) over
`Screenshot-FullDesktop` everywhere possible; only use the full-desktop capture to locate a
native dialog, then crop down immediately and don't keep the raw full-desktop image around.

### Dummy log format reference

Confirmed against `EQLogParser.Test/src/parsing/DamageLineParserTest.cs` and
`FileUtil.ParseFileName`'s regex (`^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.(txt|log)(?:\.gz)?$` —
**letters only** in the player/server segments, no digits).

- **Filename**: `eqlog_<PlayerName>_dalaya.txt` — this is how the app infers "You" = PlayerName and the server name, so the parsed data reads naturally in screenshots.
- **Timestamp prefix**: `[Ddd Mmm dd HH:mm:ss yyyy] ` — exactly 27 characters (`AppSettings.ActionIndex`). Use two-digit days to avoid the single-digit space-padding edge case.
- **Self melee**: `You crush <NPC> for <N> points of damage. (Critical)` — trailing `(Critical)` / `(Lucky Critical)` optional.
- **Other player melee**: `<Player> slashes <NPC> for <N> points of damage.`
- **NPC melee on a player**: `<NPC> crushes <Player> for <N> points of damage.`
- **Self spell nuke**: `<NPC> has taken <N> damage from your <SpellName>.`
- **Other player spell/DoT (Dalaya-reversed order)**: `<NPC> has taken <N> damage from <Player> by <SpellName>.`
- **Heal**: `<Healer> has healed <Target> for <N> points of damage.` (also works with `you` as target)
- **NPC death**: `<NPC> has been slain by <Killer>!` — note: `MainActions.ExportFights`'s "without adds" filter currently drops this line even for the selected fight itself (see `BACKLOG.md`), so don't rely on it surviving an export for a screenshot.

`sample-dummy-log.txt` in this folder is a full working example (one boss fight + one trash
fight, 3 characters) already verified to parse correctly and produce a populated Fight List /
DPS Summary.

## Local verification

`.claude/launch.json` (repo root, one level above `EQLogParser/`) has a `dalaya-site` config that
serves the static site:

```powershell
python -m http.server 3131 --directory EQLogParser/website/dalaya
```

Use the Browser pane's `preview_start` with `{name: "dalaya-site"}`, then `navigate` to
`http://localhost:3131/guides/<id>.html`. Check `preview_logs` for `200`s on the HTML and every
image request — that alone confirms the images resolve. If the `computer` screenshot action
times out (an environment hiccup unrelated to the guide content, observed 2026-07-13), fall back
to `get_page_text` for text content and `javascript_tool` for image state, e.g.:

```js
Array.from(document.querySelectorAll('img.guide-screenshot'))
  .map(img => ({src: img.src, complete: img.complete, w: img.naturalWidth, h: img.naturalHeight}))
```

`complete: true` with the expected `naturalWidth`/`naturalHeight` is sufficient evidence the
image loaded correctly even without a visual screenshot.

## Publishing

Deploy is automatic: `.github/workflows/deploy-pages.yml` triggers on push to `master` when
`website/dalaya/**` changes.

If `master` already has unrelated local commits ahead of `origin/master` that aren't ready to
ship, don't just `git push` — that sends everything ahead, not just the guide change. Isolate
it instead:

```powershell
git switch -c tmp-guide-update origin/master   # branch from the remote tip, not local master
git add website/dalaya/...
git commit -m "..."
git push origin tmp-guide-update:master        # fast-forwards origin/master by one commit
git switch master
git fetch origin
git rebase origin/master                       # replays the held-back commit(s) on top
git branch -d tmp-guide-update
```

This works because `git switch -c ... origin/master` carries over any uncommitted working-tree
changes as long as the files involved don't differ between local `master` and `origin/master` —
true whenever the held-back commit doesn't touch the same files as the guide change.

## Files

- `ui-helpers.ps1` — the PowerShell automation functions described above
- `sample-dummy-log.txt` — a verified-working synthetic raid log; copy to `eqlog_<Name>_dalaya.txt` before opening in the app
