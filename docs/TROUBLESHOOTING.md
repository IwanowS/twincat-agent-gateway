# Troubleshooting

Start with the MCP `twincat_status` tool, then read
`twincat_get_diagnostics` with a cursor. Fetch one raw resource only when those
results are insufficient. The globally installed `twincat-gateway` command is
the WPF host, not the repository development CLI.

## Gateway does not start

- `GATEWAY_CONFIG_NOT_FOUND`: put `twincat-gateway.json` in the project/Git
  root, or pass `--config <path>`.
- `GATEWAY_CONFIG_AMBIGUOUS`: MCP workspace roots resolved to different
  project configurations; narrow the workspace rather than choosing one
  implicitly.
- `GATEWAY_NOT_RUNNING`: start `twincat-gateway` manually or call
  `gateway_start` once.
- `GATEWAY_RUNNING_DIFFERENT_PROJECT`: do not close or switch the existing
  process automatically; ask the user to resolve the project ownership.
- `GATEWAY_START_DISABLED`: project policy has `allowStart: false`; manual
  launch is required.
- `GATEWAY_SHUTDOWN_DISABLED`: project policy has `allowShutdown: false`;
  close the gateway manually or explicitly enable shutdown for this project.
- `GATEWAY_INTERACTIVE_LAUNCH_UNAVAILABLE`: MCP could not hand the configured
  desktop gateway launch to the interactive Windows Explorer session. Start
  `twincat-gateway --config <absolute-path>` manually; the agent does not fall
  back to a child process with the MCP environment.
- `GATEWAY_START_TIMEOUT`: one start attempt was made but IPC did not become
  ready. Inspect the WPF/tray process and instance log; do not loop restarts.
- `Configuration could not be loaded`: fix the reported JSON/property error.
  Only `schemaVersion: 1` is accepted; there is no earlier public schema to
  migrate in version 0.1.0.
- A missing .NET runtime prevents the process from starting. Install .NET
  Framework 4.8 and the .NET 8 Desktop Runtime x64 listed in the README.

Logs default to
`%LOCALAPPDATA%\TwinCatAgentGateway\Logs`. `logRetentionDays` controls
operation-log pruning and must be between 1 and 3650.

## XAE process is visible but gateway reports none

The desktop gateway, XAE, and diagnostic command must run under the same
interactive user, Windows session, and integrity level. A process can be
visible while its ROT object is inaccessible from another context.

For repository diagnostics, inspect both processes and typed ROT sessions:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Get-XaeSessions.ps1 -AsJson
```

`ProcessCount > 0` with `RotSessionCount = 0` is an execution-context mismatch
or an XAE instance that has not registered DTE. Do not terminate or reopen the
user's XAE based only on sandbox process metadata.

## Wrong or multiple XAE solutions

Selection is by normalized absolute `Solution.FullName`. Configure the exact
`.sln` path. The gateway does not attach to the first DTE instance and does not
substitute another solution.

- `XAE_NOT_FOUND`: no exact solution match and launch is disabled or failed.
- `XAE_MULTIPLE_MATCHES`: more than one typed ROT candidate matches. Close the
  duplicate or select a single operator-owned session.
- `SOLUTION_MISMATCH`: XAE changed solution after selection; reconnect.

## Build configuration or platform fails

Explicit request values override the profile. Otherwise the profile values
override the active solution choice.

- `BUILD_CONFIGURATION_NOT_FOUND`: the requested configuration/platform pair
  is absent or did not become active.
- `BUILD_CONFIGURATION_AMBIGUOUS`: multiple configurations match, or active
  project contexts expose more than one platform.
- `BUILD_CONFIGURATION_FAILED`: the typed EnvDTE selection call itself failed;
  inspect the event and XAE log resource.

The gateway does not guess among mixed platforms.

## File Modification Detected dialogs

The gateway fingerprints only the selected TwinCAT project graph. Dirty XAE
documents are reported and block build/sync; they are never saved or discarded
automatically. Modified PLC sources may be reloaded according to
`externalChangePolicy`, while `.tsproj` notifications are temporarily
suppressed during a tracked operation.

If a dialog remains:

1. do not click Save; it can overwrite the agent's external edit;
2. record the dialog and current operation stage;
3. use the UI **Sync disk** action (or permitted MCP `twincat_sync`) only when
   the disk version is the intended source of truth;
4. if XAE has dirty documents, either save/close them manually or explicitly
   request discard when the profile permits it;
5. inspect diagnostics for `EXTERNAL_EDIT_CONFLICT` or
   `XAE_WORKSPACE_OWNERSHIP_FAILED`.

Added and removed PLC sources require `reloadAll` or an explicit force sync.
The candidate project graph is validated before the selected TwinCAT project
is reloaded.

## Activation or runtime verification fails

Activation requires an enabled profile, exact solution, exact remote AMS
NetId, and (by default) a recent successful build.

- `ACTIVATION_NOT_ALLOWED`: profile policy rejected the operation.
- target mismatch: do not change the configured identity to make the call
  pass; verify the selected target in XAE.
- runtime `unknown`: evidence is incomplete; do not report success.
- runtime `exception`: the target answered but is in TwinCAT Exception state.
  Repair/reset the test target through the approved operator workflow, then
  reconnect. Do not treat it as unavailable.

The gateway may attempt the documented recovery-to-Config sequence. A failed
recovery is reported; it is not silently retried through ADS control.

## TcUnit does not complete

The MVP reads only the configured completion and initialized-suite symbols on
one PLC ADS port after the linked activation.

- `TEST_COMPLETION_SYMBOL_UNAVAILABLE`: verify the configured PLC port and
  fixed symbol names.
- `TEST_COMPLETION_TIMEOUT`: the PLC did not publish completion before the
  deadline; fixed sleep is not completion evidence.
- `TEST_REPORT_NOT_PRODUCED`: verify the report share/path and that exactly one
  configured PLC publishes it.
- `TEST_REPORT_INVALID`: retain the report and inspect the focused xUnit
  resource.

A non-zero XAE/VSTest command exit is not by itself TcUnit failure evidence.
The fresh linked xUnit report determines test pass/fail.

## Uninstall

Exit the MCP adapter and desktop gateway. For a per-user install, remove only
`app` and `bin` under `%LOCALAPPDATA%\TwinCatAgentGateway`, then remove the
user PATH entry if needed. This does not remove project-local
`twincat-gateway.json`, TwinCAT projects, XAE state, or logs. Delete
configuration and logs separately only after confirmation.
