# Process Monitor integration

Research performed against Microsoft Sysinternals Process Monitor 4.04 (`Procmon64.exe`, file version 4.04) on 2026-08-04. The official product page identifies Process Monitor 4.04 and Windows 10 or later support: <https://learn.microsoft.com/en-us/sysinternals/downloads/procmon>.

The executable's `/?` usage dialog and readable resource strings confirm these supported switches:

| Operation | Switches used |
|---|---|
| Start capture | `/Quiet /Minimized /BackingFile <PML>` |
| Capture all events without inherited filters | `/NoFilter` |
| Load reviewed configuration | `/LoadConfig <PMC>` |
| Wait for readiness | `/WaitForIdle` |
| Stop all Procmon instances | `/Terminate` |
| Open a PML | `/OpenLog <PML>` |
| Export | `/SaveApplyFilter /SaveAs <CSV-or-XML>` |

Also detected but not used by the normal workflow: `/NoConnect`, `/AcceptEula`, `/Profiling`, `/PagingFile`, `/Run32`, `/SaveAs1`, `/SaveAs2`, `/Runtime`, `/RingBuffer`, `/RingBufferSize`, `/RingBufferLen`, boot-log switches, and driver altitude configuration.

Every argument owned by ProcmonHelper is passed through `ProcessStartInfo.ArgumentList`. No shell is involved. `/AcceptEula` is intentionally never emitted. A non-zero early exit is reported as a startup failure, with guidance that the license may need interactive acceptance.

## Known limitations

- `/Terminate` addresses Process Monitor globally; startup therefore refuses to pretend it owns an unrelated existing capture. The UI should ask the user to close an existing Procmon instance before starting.
- Supported CLI switches do not expose a safe general filter builder. Physical capture filtering is available only through `.PMC` loaded with `/LoadConfig`.
- Procmon may split large backing files into numbered PML segments. Size enforcement and finalization enumerate every PML sharing the session backing-file stem.
- CSV/XML export is an offline Procmon operation. PML remains the authoritative artifact if export fails.
- The binary PMC format is neither parsed nor generated.
