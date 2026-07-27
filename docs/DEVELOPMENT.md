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

State-changing scenarios run only on a dedicated remote test bench with:

- an explicitly allow-listed solution and target;
- disposable or recoverable runtime state;
- a known TwinCAT/XAE version;
- a test-specific activation profile;
- retained gateway, XAE, build, activation, and TcUnit logs.

If the bench is unavailable, report real-XAE tests as not run. Do not replace them with mocked acceptance.

## Local configuration

Machine-specific profiles belong in ignored files such as `profiles.local.yml` or `appsettings.Local.json`. Commit only safe examples without credentials, host-specific paths, or AMS identities.

## Formatting and validation

```powershell
dotnet format TwinCatGateway.sln --verify-no-changes --no-restore
git diff --check
```

TwinCAT project and PLC source files use CRLF because XAE may rewrite them. Other text files use LF.
