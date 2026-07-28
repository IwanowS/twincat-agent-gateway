# Troubleshooting

Start with compact `status`, then read the cursor-based event page. Fetch one
raw resource only when those results are insufficient.

```powershell
twincat-gateway status
twincat-gateway diagnostics --after-cursor 0 --max-events 100
twincat-gateway diagnostics --after-cursor 0 --minimum-severity error
```

## Gateway does not start

- `No configuration file was found`: copy `appsettings.example.json` to
  `appsettings.Local.json`, or pass `--config <absolute-path>`.
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

Gateway-owned edits are synchronized before build by fingerprinting project
sources, discarding supported dirty XAE documents, reloading changed documents,
and temporarily suppressing `.tsproj` file-change notifications during the
operation.

If a dialog remains:

1. do not click Save; it can overwrite the agent's external edit;
2. record the dialog and current operation stage;
3. close the dialog with Reload/Reload All only when the disk version is the
   intended source of truth;
4. request reconnect and retry once;
5. inspect diagnostics for `EXTERNAL_EDIT_CONFLICT` or
   `XAE_WORKSPACE_OWNERSHIP_FAILED`.

Added and removed PLC source files are not structurally synchronized in the
MVP and fail explicitly.

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

Exit the MCP adapter and desktop gateway, then remove the extracted portable
directory. This does not remove TwinCAT projects, XAE state,
`appsettings.Local.json` stored elsewhere, or logs under LocalAppData. Delete
configuration and logs separately only after confirmation.
