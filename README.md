# TwinCAT Agent Gateway

TwinCAT Agent Gateway is a local Windows desktop application that gives coding
agents a compact, typed interface to TwinCAT 3 XAE. The MVP supports XAE
discovery/launch, Build/Rebuild/Clean, explicit remote activation with an
optional Run confirmation,
cursor-based diagnostics, bounded XAE Error List reads, local raw logs, and
linked TcUnit result collection.

> **Architecture rework:** the repository is moving through an intentionally
> incompatible v2 redesign. The approved target contract is documented in
> [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); the implemented v1 baseline
> is preserved separately in
> [`docs/ARCHITECTURE_V1_BASELINE.md`](docs/ARCHITECTURE_V1_BASELINE.md).
> During the staged cutover, the checked-out applications may implement only
> part of the target contract.

## Requirements

- Windows 10/11;
- TwinCAT 3.1.4024.17;
- Visual Studio 2019 or compatible 32-bit XAE Shell;
- .NET Framework 4.8;
- .NET 8 Desktop Runtime x64.

The desktop gateway and XAE must run as the same interactive Windows user,
session, and integrity level.

## Per-user installation

From the repository:

```powershell
.\scripts\Install-Gateway.ps1
.\scripts\Install-CodexMcp.ps1
```

The first script builds with `dotnet build`, installs the WPF x86 gateway and
the .NET 8 MCP adapter under `%LOCALAPPDATA%\TwinCatAgentGateway`, and offers
to add its stable `bin` directory to the user PATH. The second script
registers `twincat-gateway-mcp` globally through the supported Codex CLI.
Skills remain a separate choice:

```powershell
.\scripts\Install-Skills.ps1 -Scope User
.\scripts\Install-Skills.ps1 -Scope Project -ProjectPath C:\repos\Machine
```

This repository includes a root `twincat-gateway.json` for MCP debugging with
`tests/fixtures/TC3_SimpleProject/TC3_SimpleProject.sln` on the allow-listed
remote TwinCAT target. Other projects should put their own
`twincat-gateway.json` in the project root. Relative paths are resolved from
that file, and both commands search upward without crossing a Git root. See the
[canonical setup instructions](setup/SETUP_INSTRUCTIONS.txt) and the
[installation guide](docs/INSTALLATION.md).

`twincat-gateway` is the WPF application. Agents use
`twincat-gateway-mcp`; normal MCP operations never start the WPF process.
Only the explicit `gateway_start` tool may do so after checking project policy.
The explicit `gateway_shutdown` tool closes it only when
`agentProcessControl.allowShutdown` is `true`.

## Portable package

Build the framework-dependent portable ZIP:

```powershell
dotnet restore TwinCatGateway.sln
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Publish-Portable.ps1
```

The archive name contains the Git-derived package version, for example
`artifacts\TwinCatAgentGateway-0.1.1-g1a2b3c4d5-windows.zip`. Extract it, copy
`twincat-gateway.example.json` to the project as `twincat-gateway.json`, and
set the solution path. The shipped example is build-only: activation is
disabled.

Start the desktop host and MCP adapter:

```powershell
desktop\twincat-gateway.exe --config C:\project\twincat-gateway.json
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
- An attached user-owned XAE session starts in `syncRequired`; the current disk
  state is not assumed to match its in-memory project model.
- Unsaved XAE documents are never saved or discarded automatically. They fail
  build/sync with `DIRTY_XAE_DOCUMENT`; discard requires both an explicit
  request and profile permission.
- A solution may reference its selected TwinCAT `.tsproj` outside the solution
  directory; that project's source tree remains inside the verified workspace.
- Schema-valid reorder-only `.tsproj` changes are accepted only when observed
  inside a tracked, successfully completed XAE operation. They are retained,
  not rewritten.
- PLC `.tmc` files that belong to the selected project graph are generated
  build artifacts. Their changes are always accepted and retained; the gateway
  never asks an agent to merge or inspect the full file.
- Activation uses the XAE `TwinCAT.ActivateConfiguration` command once. It
  preserves the user-configured tri-state Auto Boot selection and answers the
  final Run prompt through `runAfterActivation`; no second restart command is
  issued.

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

- [target architecture and operation semantics](docs/ARCHITECTURE.md);
- [accepted and deferred architecture decisions](docs/ARCHITECTURE_DECISIONS.md);
- [target configuration schema](docs/CONFIGURATION.md);
- [target MCP tools and resources](docs/MCP_REFERENCE.md);
- [agent workflows and deferred debugging scenarios](docs/WORKFLOWS.md);
- [multi-session architecture rework plan](docs/ARCHITECTURE_REWORK_PLAN.md);
- [implemented v1 architecture baseline](docs/ARCHITECTURE_V1_BASELINE.md);
- [historical MVP implementation plan](docs/IMPLEMENTATION_PLAN.md);
- [development and real-XAE checks](docs/DEVELOPMENT.md);
- [installation and agent setup](docs/INSTALLATION.md);
- [troubleshooting](docs/TROUBLESHOOTING.md);
- [release notes and verified matrix](docs/RELEASE_NOTES.md).
