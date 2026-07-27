# AGENTS.md

## Project overview

TwinCAT Agent Gateway is a local Windows application that gives coding agents a compact, reliable interface to TwinCAT 3 XAE.

Target environment for the MVP:

- TwinCAT 3.1.4024.17;
- Visual Studio 2019 or a compatible XAE Shell;
- Windows 10/11;
- .NET Framework 4.8 for the x86 desktop/COM host;
- .NET 8 for CLI and MCP adapters.

The MVP covers XAE connection/status, `Build`, `Rebuild`, `Clean`, explicit configuration activation, compact diagnostics, local full logs, and TcUnit result collection. PLC source code is edited directly in project files.

Authoritative design documents:

- architecture and operation semantics: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md);
- milestones and acceptance criteria: [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md).

Do not read both documents in full for every task. Read only the relevant sections when changing process boundaries, public contracts, XAE operation semantics, or milestone scope.

## Repository layout

```text
src/       production projects
tests/     unit, contract, and TwinCAT integration tests
skills/    agent-facing workflows built on the gateway API
docs/      architecture, decisions, and implementation plan
```

When a component develops substantially different commands or conventions, add a nested `AGENTS.md` in that component instead of expanding this root file.

## Setup and commands

Development requires a Windows machine with Visual Studio 2019 build tools, TwinCAT XAE 3.1.4024.17, .NET Framework 4.8 targeting pack, and .NET 8 SDK.

After the solution skeleton is created, keep these commands working and update this section if paths change:

```powershell
# Restore and build from a Visual Studio Developer PowerShell
msbuild TwinCatGateway.sln /restore /m /p:Configuration=Debug

# Fast tests
dotnet test tests/TwinCatGateway.UnitTests/TwinCatGateway.UnitTests.csproj
dotnet test tests/TwinCatGateway.ContractTests/TwinCatGateway.ContractTests.csproj

# Requires a configured TwinCAT 3.1.4024.17 machine
dotnet test tests/TwinCatGateway.IntegrationTests/TwinCatGateway.IntegrationTests.csproj
```

Do not report TwinCAT integration tests as passed when the required XAE environment was unavailable. Report them as not run and state why.

## Architecture constraints

- Only the desktop gateway may own or call DTE, `ITcSysManager`, or other TwinCAT COM objects.
- Run all XAE COM calls sequentially on one STA thread with a message pump and OLE `IMessageFilter`.
- Do not pass COM objects across threads or processes.
- CLI and MCP are thin IPC clients; domain logic belongs in the gateway/core.
- Activation is always an explicit operation and must never follow a build implicitly.
- Do not close an XAE instance opened by the user unless explicitly requested.
- Do not add an ADS client, online-variable access, PLC debugging, or Automation Interface code editing to the MVP.
- Do not add PowerShell scripts or modules as an implementation layer.
- Do not rewrite or revert reorder-only `.tsproj` changes. Detect and mark them as expected generated noise.

## XAE operation rules

- Select an XAE instance by normalized absolute `Solution.FullName`, not by “first instance found”.
- Support multiple running XAE instances.
- Determine completion from XAE events and verifiable postconditions, not fixed sleeps.
- A timeout is only an upper bound; it is not evidence of success or failure by itself.
- Build success requires a completed operation, zero compile errors, and no infrastructure failure.
- Store full raw output locally; return compact summaries and references by default.
- Treat unknown runtime state as `unknown`; never infer a trustworthy state from incomplete evidence.
- TcUnit pass/fail comes from a fresh, completed report associated with the current run. A missing or stale report is an error.

## File editing

- PLC code is edited in `.TcPOU`, `.TcGVL`, `.TcDUT`, and related project files.
- Never let an old unsaved XAE document overwrite an external file change.
- Surface unsaved-document or reload conflicts explicitly instead of silently retrying.
- Do not read or include an entire `.tsproj` in agent context merely to inspect reorder noise; use the classifier result and focused diffs.

## Code style

- Use C# with nullable reference types enabled where supported.
- Use English identifiers, public API names, error codes, and log property names.
- Keep external DTOs versioned, serializable, and independent of COM/WPF/MCP types.
- Use stable machine-readable error codes plus short human-readable messages.
- Preserve original HRESULT and detailed exception context in local logs.
- Never use empty `catch` blocks or suppress an exception without recording the reason.
- Pass cancellation and deadlines through long-running operations.
- Keep UI presentation separate from domain and operation state.
- Prefer small focused types and one execution path per domain operation.

## Testing instructions

For every behavioral change:

- add or update tests for success and relevant failure paths;
- cover timeout and cancellation when the operation can wait;
- run the nearest unit tests first;
- run contract tests after changing DTOs, IPC, CLI, or MCP;
- run real TwinCAT integration tests after changing DTE/COM/XAE behavior;
- do not replace required real-XAE integration coverage with mocks.

Before finishing, build the affected projects and state exactly which checks were run and which were not.

## Safety and logging

- Activate only a configured and explicitly allowed target profile.
- Never substitute another target, solution, or AMS identity automatically.
- Do not put secrets, credentials, or unnecessary PLC source content in logs.
- Do not return stack traces, full Build Output, full Error List, large XML, or large diffs by default.
- Keep destructive or machine-state-changing actions separate from read-only status and build operations.

## Documentation and scope

Update `docs/ARCHITECTURE.md` only when architecture, public contracts, operation semantics, or safety boundaries change.

Update `docs/IMPLEMENTATION_PLAN.md` only when milestone scope, ordering, or acceptance criteria change.

Do not expand the MVP without an explicit user decision. If code and documentation disagree, identify the conflict and make the selected behavior explicit rather than guessing.

## Completion checklist

Before completing a task:

- verify that the change stays within the requested scope;
- build and test the affected area;
- keep CLI/MCP behavior consistent with the gateway contract;
- keep default output compact;
- note any behavior not verified on TwinCAT 3.1.4024.17;
- update only the documentation made inaccurate by the change.
