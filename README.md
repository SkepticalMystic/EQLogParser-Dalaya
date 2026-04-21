# EQLogParser-Dalaya
EverQuest combat log parser for the [Dalaya](https://www.dalaya.com) private server (formerly known as Shards of Dalaya).

This is a fork of [kauffman12's EQLogParser](https://github.com/kauffman12/EQLogParser), adapted to handle Dalaya's distinct combat log format. It provides damage meters, spell tracking, pet assignment, overlays, triggers, audio alerts, and detailed combat analytics through a rich WPF interface.

## What's Different from the Original

Dalaya's log format differs from live/TLP EverQuest in a few key ways that require special handling:

- **Spell damage attribution** — Dalaya logs reverse the attacker/spell order and use `your` instead of the caster's name in certain lines.
- **Named pet tracking** — Dalaya logs pet names with a trailing space, which is accounted for in pet assignment logic.
- **Spell database** — Ships with a Dalaya-specific `spells.txt` rather than the live-server spell list.

Everything else — the UI, overlays, triggers, and analytics — works the same as the upstream project.

## Download

Link to DOWNLOAD the latest Installer:</br>
https://github.com/SkepticalMystic/EQLogParser-Dalaya/releases/latest

## Minimum Requirements

1. Windows 10 x64
2. .NET 8.0.11 Desktop Runtime for x64 (or any newer 8.0.x version)

## Building from Source

1. Clone this repository.
2. Obtain a free [Syncfusion community license key](https://www.syncfusion.com/products/communitylicense) and add it to `EQLogParser/src/App.xaml.cs`.
3. Open `EQLogParser.sln` in Visual Studio 2022 and build in Release/x64.
4. To produce an installer, compile `EQLogParserInstall/EQLogParserInstall.iss` with [Inno Setup 6](https://jrsoftware.org/isinfo.php).

## Issues & Feedback

Please open an issue at https://github.com/SkepticalMystic/EQLogParser-Dalaya/issues
