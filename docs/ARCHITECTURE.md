# Architecture

- `ProcmonHelper.Contracts`: immutable profiles, session records, worker DTOs, and service interfaces.
- `ProcmonHelper.Core`: state machine, validation, filename expansion, and stop-condition evaluation.
- `ProcmonHelper.Infrastructure`: Process Monitor commands, process launch, current-user named-pipe worker, durable replace-based JSON storage, profile repository, disk checks, file transfer, and session orchestration.
- `ProcmonHelper.App`: non-elevated WPF MVVM UI, runtime RU/EN resources, tray integration, and the `--elevated-worker` entry mode.

The UI creates a random pipe name containing the session GUID and 96 random bits. The server has an explicit protected ACL for the initiating Windows SID and the built-in Administrators group, allowing UAC elevation with a separate administrator account. Session directories grant both identities access so the elevated worker can write the backing PML while post-processing remains in the initiating user's process. The UI launches the same executable using `runas`. DTO polymorphism limits IPC to start, target PID, heartbeat, and stop; there is no arbitrary command endpoint. The worker owns Procmon, monitors its lifetime and independently enforces duration, aggregate PML size, free-space reserve, target-exit and client-heartbeat conditions.

ProcmonHelper does not verify Authenticode signatures or publishers. The selected executable must exist, be named `Procmon64.exe`, and expose a readable Process Monitor version. This is an operational compatibility check, not a security or provenance decision.

Session metadata is flushed to disk through `session.json.tmp` and then replaced. Destination transfers use a `.partial` file and rename only after length verification.
