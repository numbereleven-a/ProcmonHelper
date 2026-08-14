# Session lifecycle

The normal target-launch path is `Idle → Validating → Preparing → WaitingForElevation → StartingProcmon → WaitingForProcmon → LaunchingTarget → Capturing → StopRequested → StoppingProcmon → Finalizing`. Monitoring-only capture skips `LaunchingTarget` and transitions directly from `WaitingForProcmon` to `Capturing`. Optional `Exporting` and `Copying` states follow finalization before a terminal state.

Invalid transitions throw immediately. A session directory is created before elevation and includes `session.json`, `procmon/`, `export/`, and `logs/`. The worker stops Procmon in a bounded cleanup block on normal stop, IPC loss, or cancellation. A failed target launch therefore cannot intentionally leave capture running.

The repository can identify session JSON records whose state is not `Completed`, `CompletedWithWarnings`, or `Failed`. The current UI does not automatically recover them. Existing PML is preserved for manual inspection; automatic export/copy retry is planned but not implemented.
