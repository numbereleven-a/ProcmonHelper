# Changelog

## 1.1

- Support UAC elevation with a separate administrator account while keeping the UI and target application under the initiating user.
- Grant the elevated capture worker access to the per-session IPC channel and backing PML directory.
- Optionally load the last used profile at startup; enabled by default.

## 1.0

- Initial public release.
- Capture starts immediately before the selected application is launched.
- Automatic and manual stop conditions prevent unnecessary events from filling the PML.
- Local PML preservation, optional CSV/XML export, profiles, and additional destination copying.
- Portable self-contained Windows x64 executable.
