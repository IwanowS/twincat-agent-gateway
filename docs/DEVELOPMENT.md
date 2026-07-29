# Local development

## Prerequisites

For Contracts/Core development:

- Git;
- .NET 8 SDK.

For a full desktop build:

- Windows 10 or 11;
- .NET Framework 4.8 reference assemblies.

Only real-XAE integration work additionally requires:

- Visual Studio 2019 or compatible TwinCAT XAE Shell;
- TwinCAT 3.1.4024.17.

The repository pins the .NET 8 SDK family in `global.json`. A newer installed SDK must not silently change the build.

The repository-level `NuGet.Config` clears user-level package sources and restores only from `nuget.org`. Add a new source only when a project dependency actually requires it, and never commit credentials.

## Restore, build, and fast tests

Run from the repository root:

```powershell
dotnet restore TwinCatGateway.sln
dotnet build TwinCatGateway.sln --no-restore --configuration Debug
dotnet test tests/TwinCatGateway.UnitTests/TwinCatGateway.UnitTests.csproj --no-build --configuration Debug
dotnet test tests/TwinCatGateway.ContractTests/TwinCatGateway.ContractTests.csproj --no-build --configuration Debug
```

`dotnet build` is the supported build command because the solution contains both .NET Framework 4.8 and .NET 8 projects. Visual Studio 2019/XAE Shell is the automation target; its older MSBuild cannot orchestrate the pinned .NET 8 solution. Use `dotnet build` for focused desktop/XAE project checks as well.

## Versioning

Nerdbank.GitVersioning derives all assembly, file, informational, and package
versions from the repository-root `version.json`. The `0.1` version line uses
Git height as the patch component, so commits after the version baseline receive
distinct versions after the existing `0.1.0` MVP. A tag matching
`v<major>.<minor>.<patch>` marks a public release; other builds retain commit
identity in their version.

Do not add project-local `<Version>` properties or pass a separate version to
the portable publishing script. Change `version.json` when starting a new
version line, and commit that change before relying on the resulting version.

## Integration-test policy

Local integration tests may inspect files and compile test assemblies, but must not activate a TwinCAT configuration, restart TwinCAT, change runtime mode, log in to a PLC, write ADS values, or select a substitute target.

### User-session ROT boundary

Real-XAE COM checks must run under the same interactive Windows account,
session, and integrity level as the XAE process. The DTE registration lives in
that user's Running Object Table (ROT). A sandboxed agent account can still see
the XAE PID in the process list while receiving no DTE monikers; it can also
observe `MainWindowHandle = 0` even though the XAE window is visible to the
user.

Treat these combined symptoms as an execution-context mismatch first, not as
evidence that XAE or the solution is closed:

- the expected `TcXaeShell` or `devenv` PID is running;
- ROT discovery returns no XAE candidates;
- a gateway-launched process exits or times out waiting for ROT registration.

For Codex-hosted checks, run the real-XAE test command outside the agent
sandbox so it inherits the interactive user's COM context. Do not ask the user
to close, reopen, or terminate XAE until a same-user ROT discovery has also
failed.

The same interactive-session boundary applies to the repository-owned
WinForms dialogs used by `XaeDialogSupervisorTests`. If a sandbox run detects
the expected dialog but records `actionRequested=true` and
`actionCompleted=false`, rerun that focused test outside the sandbox in the
same interactive user session before diagnosing a gateway regression. The
focused rerun may interact only with dialogs created by the test process; it
must not close or manipulate user-owned XAE windows.

Build with the pinned SDK as usual, then run the already-built .NET Framework
integration assembly through VSTest's x86 platform:

Before fixture edits and before/after real-XAE tests, inspect the interactive
x86 ROT rather than relying on sandbox process metadata:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Get-XaeSessions.ps1 -AsJson
```

The output includes the number of XAE/Visual Studio processes, ROT sessions,
loaded solutions, PID, HWND/title, start time, ProgID, and exact
`Solution.FullName`. A process visible only outside ROT is reported explicitly.

```powershell
$env:TWINCAT_GATEWAY_XAE_SOLUTION = 'C:\absolute\path\to\project.sln'
dotnet vstest `
  'tests\TwinCatGateway.IntegrationTests\bin\Debug\net48\TwinCatGateway.IntegrationTests.dll' `
  '/Platform:x86' `
  '/TestCaseFilter:FullyQualifiedName~XaeEnvironmentTests'
```

Set `TWINCAT_GATEWAY_ALLOW_XAE_LAUNCH=1` only for the separately authorized
test that launches and closes its own XAE instance. It must never close a
user-owned instance.

The state-changing activation acceptance test is separately opt-in and requires
the exact allow-listed remote AMS NetId:

```powershell
$env:TWINCAT_GATEWAY_XAE_SOLUTION = 'C:\absolute\path\to\project.sln'
$env:TWINCAT_GATEWAY_ALLOW_REMOTE_ACTIVATION = '1'
$env:TWINCAT_GATEWAY_REMOTE_AMS_NET_ID = '192.168.3.31.1.1'
dotnet vstest `
  'tests\TwinCatGateway.IntegrationTests\bin\Debug\net48\TwinCatGateway.IntegrationTests.dll' `
  '/Platform:x86' `
  '/TestCaseFilter:FullyQualifiedName~DesktopGatewayActivationTests.ActivationBuildsAndRunsRemoteTargetThroughIpc'
```

This test builds the selected solution, activates its configuration, restarts
TwinCAT on the remote target, waits for read-only ADS state `Run`, and checks
that XAE has no modal dialogs. Never set the opt-in variable for a local target
or an unapproved bench.

The linked TcUnit acceptance additionally launches its own XAE instance,
waits for the single PLC configured by the profile, reads a fresh report
through the operator-provided read-only path, queries `getTestResults`, and
verifies that a subsequent Build ignores pre-existing TcUnit runtime entries
in XAE Error List:

```powershell
$env:TWINCAT_GATEWAY_XAE_SOLUTION = 'C:\absolute\path\to\project.sln'
$env:TWINCAT_GATEWAY_ALLOW_XAE_LAUNCH = '1'
$env:TWINCAT_GATEWAY_ALLOW_REMOTE_ACTIVATION = '1'
$env:TWINCAT_GATEWAY_REMOTE_AMS_NET_ID = '192.168.3.31.1.1'
$env:TWINCAT_GATEWAY_TCUNIT_REPORT_PATH = '\\runtime-host\share\tcunit_xunit_testresults.xml'
dotnet vstest `
  'tests\TwinCatGateway.IntegrationTests\bin\Debug\net48\TwinCatGateway.IntegrationTests.dll' `
  '/Platform:x86' `
  '/TestCaseFilter:FullyQualifiedName~DesktopGatewayActivationTests.ActivationRunsLinkedTcUnitThroughIpc'
```

The MVP profile designates exactly one TcUnit PLC through `tcUnit.adsPort`.
Other PLCs may exist in the solution, but must not publish to the configured
report file. Multi-PLC aggregation is tracked as post-MVP scope in
[`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md).

State-changing scenarios run only on a dedicated remote test bench with:

- an explicitly allow-listed solution and target;
- disposable or recoverable runtime state;
- a known TwinCAT/XAE version;
- a test-specific activation profile;
- an ADS route from the gateway host to the selected target;
- a test PLC task that cyclically calls `TcUnit.RUN()` or `TcUnit.RUN_IN_SEQUENCE()` and exposes the configured completion symbols;
- TcUnit xUnit publishing enabled to an accessible report path;
- for a remote runtime, a dedicated read-only SMB share (or equivalent
  filesystem access) for that report path; administrative shares are not a
  supported assumption;
- retained gateway, XAE, build, activation, and TcUnit logs.

If the bench is unavailable, report real-XAE tests as not run. Do not replace them with mocked acceptance.

## Optional TwinCAT Project Compare component

Some TwinCAT installations include Beckhoff's Project Compare component under
`C:\TwinCAT\3.1\Components\TcProjectCompare`. It is available for future
experiments with semantic comparison of TwinCAT project files.

The public COM interface `ITcHeadlessCompare` is declared by
`TcPrjCmpPkgInterface.dll` and its accompanying type library. It provides:

- headless comparison of two files or an argument array;
- overall difference detection;
- separate counts for formal and logical changes;
- addition and deletion counts;
- indexed error, warning, and informational messages.

The bundled supported-file catalogue includes `.tsproj`, `.xti`, `.tmc`,
`.TcPOU`, `.TcGVL`, `.TcDUT`, and other TwinCAT formats. This makes the
component potentially useful as an independent semantic signal when
investigating generated `.tsproj` noise or PLC source changes. Its formal and
logical counters do not identify the exact changed XML blocks, so they are not
by themselves proof that a change is reorder-only.

Do not use Project Compare for PLC `.tmc` gateway policy. Beckhoff documents
PLC `.tmc` as automatically regenerated after compilation and not mergeable.
The gateway therefore treats only `.tmc` files explicitly referenced by the
selected PLC project graph as always-allowed generated artifacts.

`TcProjectCompareCore.dll` exposes a richer `HeadlessDiff` API, including
accept and save operations, but depends on internal TwinCAT PLC merge, WPF,
TFS, type-system, and storage assemblies. Prefer the small public COM
interface for any exploratory integration; do not invoke file-mutating
operations during classification.

Project Compare compares files on disk. It does not make an open XAE instance
reload externally changed files and does not suppress XAE file-change dialogs.
The inspected extension also referenced TwinCAT Storage `3.1.4025.1`, while
the gateway MVP targets TwinCAT `3.1.4024.17`; compatibility must therefore be
verified in a real target XAE before relying on it.

## Local configuration

The production discovery name is `twincat-gateway.json`. This repository
intentionally commits a root debug configuration for
`tests/fixtures/TC3_SimpleProject/TC3_SimpleProject.sln`. It enables MCP
process control and activation only for the allow-listed remote AMS NetId
`192.168.3.31.1.1`, requires a recent successful build, and automatically
waits for the single TcUnit PLC on ADS port 851. The report is read through
`\\WIN-T077ADA\c\TwinCAT\3.1\Boot\tcunit_xunit_testresults.xml`.

The committed fixture intentionally contains one passing test and one failing
test named `GatewayReportsFailedTcUnit`. Its expected linked result is
`TEST_FAILED` with the runtime remaining in `Run`; this verifies that a TcUnit
failure is not misclassified as a PLC runtime exception.

For the lightweight exception fixture, use an activation request timeout of
30 seconds. The timeout is an upper bound, not success evidence; increase it
explicitly when diagnosing a slower project. The configuration schema does
not define a profile-level default activation timeout.

The fixture-local
`tests/fixtures/TC3_SimpleProject/twincat-gateway.json` remains
activation-disabled for safe test discovery. Real state-changing integration
tests still require the allow-listed environment opt-in described above.

Put other machine-specific profiles in their local project and ignore them
when they contain host paths or AMS identities. Relative paths resolve from
the configuration directory. `appsettings.Local.json` is never discovered
automatically, but an existing file can still be selected explicitly with
`--config`. Commit only safe examples without credentials unless the
repository deliberately owns a specific allow-listed test-bench profile, as
this checkout now does.

## Formatting and validation

```powershell
dotnet format TwinCatGateway.sln --verify-no-changes --no-restore
git diff --check
```

TwinCAT project and PLC source files use CRLF because XAE may rewrite them. Other text files use LF.
