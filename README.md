# ProcmonHelper

English · [Русский](README_RU.md)

![ProcmonHelper main window](docs/images/procmonhelper-main.png)

Process Monitor records activity from the entire system. Starting a capture too early or stopping it too late fills the PML with unrelated events and makes the useful information harder to find.

ProcmonHelper can start capture immediately before launching a selected application, or monitor the system without launching one. It stops collection at the required moment and preserves the resulting PML.

## Why use it

- Capture begins immediately before the target application starts: early startup events are preserved without collecting unrelated activity beforehand.
- The target launch can be disabled when only system monitoring is required.
- Capture stops at the required moment instead of continuing to fill the log with unnecessary events.
- Collection can stop automatically when the target closes, after a time limit, at a PML size limit, or when the free-space reserve is reached.
- Manual stop finalizes and preserves the collected PML.
- The PML is staged locally during capture and can then be saved to either a local folder or a UNC network share.
- Reusable profiles keep launch, capture, stop, and save settings together.
- The status panel shows capture timing, active conditions, filters, and the saved file path.
- No installation is required: the release is a single portable EXE.

## Requirements

- Windows 10 or Windows 11 x64
- `Procmon64.exe` from the official [Microsoft Sysinternals Process Monitor page](https://learn.microsoft.com/sysinternals/downloads/procmon)
- Administrator approval when the capture worker starts

Process Monitor is not included and is never downloaded automatically. If its license dialog has not been accepted yet, start `Procmon64.exe` manually once before using ProcmonHelper.

## Quick start

1. Download and extract Process Monitor.
2. Start `ProcmonHelper.exe` and select `Procmon64.exe`.
3. Select the application whose launch you want to trace, or clear **Launch a target application** for monitoring-only capture.
4. Configure capture mode and stop conditions.
5. Select the local folder or UNC network share where the PML should be saved.
6. Click **Start capture** and approve elevation.
7. Use the launched application or monitored system normally. Stop capture manually when needed, or let a configured condition stop it.

The completed PML path is shown in the status panel. PML files can be opened directly in Process Monitor.

## Capture modes and filters

- **All events** starts Process Monitor without inherited saved filters.
- **Selected processes** stores the process list in the profile and capture summary. It does not physically remove unrelated events from the raw PML.
- **PMC configuration** loads a user-prepared `.PMC` file. Use this mode when the PML itself must be filtered by Process Monitor rules.

The **Do not write events from Procmon*.exe to PML** option is enabled by default in the first two modes and physically drops those events during capture. A user-prepared PMC takes precedence in **PMC configuration** mode, so add the same exclusion to that file if required.

## License

ProcmonHelper is distributed under the [MIT License](LICENSE).

## Download

[![release](https://img.shields.io/github/v/release/numbereleven-a/ProcmonHelper?label=release&style=flat-square)](https://github.com/numbereleven-a/ProcmonHelper/releases/latest)
[![downloads](https://img.shields.io/github/downloads/numbereleven-a/ProcmonHelper/total?label=downloads&style=flat-square&color=yellowgreen)](https://github.com/numbereleven-a/ProcmonHelper/releases)
