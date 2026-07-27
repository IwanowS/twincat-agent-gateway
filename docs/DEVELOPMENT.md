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

Build with the pinned SDK as usual, then run the already-built .NET Framework
integration assembly through VSTest's x86 platform:

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
  '/TestCaseFilter:FullyQualifiedName~DesktopGatewayActivationTests.ActivationBuildsAndRestartsRemoteTargetThroughIpc'
```

This test builds the selected solution, activates its configuration, restarts
TwinCAT on the remote target, waits for read-only ADS state `Run`, and checks
that XAE has no modal dialogs. Never set the opt-in variable for a local target
or an unapproved bench.

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

Machine-specific profiles belong in ignored files such as `profiles.local.yml` or `appsettings.Local.json`. Commit only safe examples without credentials, host-specific paths, or AMS identities.

## Formatting and validation

```powershell
dotnet format TwinCatGateway.sln --verify-no-changes --no-restore
git diff --check
```

TwinCAT project and PLC source files use CRLF because XAE may rewrite them. Other text files use LF.
