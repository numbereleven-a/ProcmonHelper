# Changelog

## 1.3

- Added capture without launching a target application and a PMC file picker.
- Added PML saving to local folders and UNC network shares.
- Improved capture lifecycle, elevated-worker IPC, shutdown, and target-exit detection.
- Fixed output file naming, profile validation, numeric limits, and localization.

## 1.2

- Added a reset button that restores default settings without deleting saved profiles.
- Made the Profiles tab more compact.
- Added a button to open the output folder.

## 1.1

- Support UAC elevation with a separate administrator account while keeping the UI and target application under the initiating user.
- Grant the elevated capture worker access to the per-session IPC channel and backing PML directory.
- Optionally load the last used profile at startup; enabled by default.
- Optionally omit events from processes whose names begin with `Procmon` from the PML; enabled by default.

## 1.0

- Initial public release.
- Capture starts immediately before the selected application is launched.
- Automatic and manual stop conditions prevent unnecessary events from filling the PML.
- Local PML preservation, optional CSV/XML export, profiles, and additional destination copying.
- Portable self-contained Windows x64 executable.
