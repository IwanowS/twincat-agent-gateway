# TwinCAT Agent Gateway

TwinCAT Agent Gateway is a local Windows desktop application that gives coding
agents a compact, typed interface to TwinCAT 3 XAE. The MVP supports XAE
discovery/launch, Build/Rebuild/Clean, explicit remote activation and restart,
cursor-based diagnostics, local raw logs, and linked TcUnit result collection.

## Requirements

- Windows 10/11;
- TwinCAT 3.1.4024.17;
- Visual Studio 2019 or compatible 32-bit XAE Shell;
- .NET Framework 4.8;
- .NET 8 Desktop Runtime x64.

The desktop gateway and XAE must run as the same interactive Windows user,
session, and integrity level.

## Portable installation

Build the framework-dependent portable ZIP:

```powershell
dotnet restore TwinCatGateway.sln
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Publish-Portable.ps1
```

Extract `artifacts\TwinCatAgentGateway-0.1.0-windows.zip`, copy
`appsettings.example.json` to `appsettings.Local.json`, and set the absolute
solution path. The shipped example is build-only: activation is disabled.

Start `desktop\TwinCatGateway.Desktop.exe`, then use:

```powershell
cli\twincat-gateway.exe status
cli\twincat-gateway.exe build --profile default --action rebuild
mcp\twincat-gateway-mcp.exe
```

The MCP process is a thin stdio adapter. Restarting it does not close the
desktop gateway or its XAE session.

## Safety model

- Build never activates TwinCAT.
- Activation is an explicit operation limited to the exact allow-listed remote
  AMS NetId in the selected profile.
- General ADS reads, ADS writes, RPC, `WriteControl`, PLC login, and local
  runtime control are outside the MVP.
- While attached, the agent owns project files; stale unsaved XAE document
  changes may be discarded before external edits are synchronized.
- Reorder-only `.tsproj` changes are classified and retained, not rewritten.

## Development

```powershell
dotnet restore TwinCatGateway.sln
dotnet build TwinCatGateway.sln --no-restore --configuration Debug
dotnet test tests\TwinCatGateway.UnitTests\TwinCatGateway.UnitTests.csproj `
  --no-build --configuration Debug
dotnet test tests\TwinCatGateway.ContractTests\TwinCatGateway.ContractTests.csproj `
  --no-build --configuration Debug
```

Further documentation:

- [architecture and operation semantics](docs/ARCHITECTURE.md);
- [implementation milestones and acceptance criteria](docs/IMPLEMENTATION_PLAN.md);
- [development and real-XAE checks](docs/DEVELOPMENT.md);
- [troubleshooting](docs/TROUBLESHOOTING.md);
- [release notes and verified matrix](docs/RELEASE_NOTES.md).
