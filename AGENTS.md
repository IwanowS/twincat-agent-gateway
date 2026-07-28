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

Fast Contracts/Core development requires the .NET 8 SDK. Building the full desktop solution requires Windows and .NET Framework 4.8 reference assemblies. Visual Studio 2019/XAE Shell and TwinCAT 3.1.4024.17 are required only for real-XAE development and integration checks.

Keep these commands working and update this section if paths change:

```powershell
# Repository-wide restore and build
dotnet restore TwinCatGateway.sln
dotnet build TwinCatGateway.sln --no-restore --configuration Debug

# Fast tests
dotnet test tests/TwinCatGateway.UnitTests/TwinCatGateway.UnitTests.csproj --no-build --configuration Debug
dotnet test tests/TwinCatGateway.ContractTests/TwinCatGateway.ContractTests.csproj --no-build --configuration Debug
```

The solution mixes .NET Framework 4.8 and .NET 8 projects, so Visual Studio 2019 `msbuild` is not a supported repository build driver. Use the pinned .NET SDK even for focused desktop/XAE project builds. Visual Studio 2019/XAE Shell is the automation target, not the build orchestrator for this repository.

Real-XAE integration tests require the explicitly configured remote TwinCAT 3.1.4024.17 test bench described in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md). Do not report them as passed when that environment was unavailable. Report them as not run and state why.

Real-XAE DTE/ROT checks must execute under the same interactive Windows account, session, and integrity level as XAE. A sandbox account may see the XAE PID while seeing an empty ROT and no main-window handle. Treat that combination as an execution-context mismatch; rerun the read-only discovery outside the agent sandbox before asking the user to reopen or terminate XAE. Use the x86 VSTest command documented in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

Before changing the TwinCAT fixture or starting/finishing a real-XAE test,
run `scripts\Get-XaeSessions.ps1 -AsJson` outside the agent sandbox. The
development-only probe relaunches itself as x86 Windows PowerShell and reports
the process count, HWND/title, ROT visibility, and exact `Solution.FullName`
for every XAE/Visual Studio DTE session. Never infer that XAE has no visible
window or loaded solution from sandbox `Get-Process` output alone. Re-run the
probe after a gateway-owned test to verify that its XAE process actually
exited before the next external edit.

### .NET Code and Assembly Analysis

The project MCP configuration requires these global .NET tools at the validated versions. On a new machine, install them once:

```powershell
dotnet tool install --global Sherlock.MCP.Server --version 2.12.0
dotnet tool install --global RoslynCodeLens.Mcp --version 2.14.0
```

Do not silently fall back to guessing when either required MCP server is unavailable. Report the missing prerequisite.

Use Roslyn CodeLens MCP for questions about the loaded solution: symbols, definitions, references, implementations, callers, type hierarchies, source code, diagnostics, and change impact. Start with search_symbols, get_symbol_context, get_type_overview, or get_file_overview, then drill into focused queries. Use list_solutions when the active solution is uncertain.

The project config starts Roslyn CodeLens from the current repository root with the explicit relative solution path. For a manual launch, run this from the root of the current checkout or worktree:

```powershell
roslyn-codelens-mcp .\TwinCatGateway.sln
```

Never reuse an absolute solution path from another checkout. Confirm with list_solutions that the active normalized path belongs to the current checkout and that no projects were skipped.

Use Sherlock MCP for external .NET assemblies, NuGet packages, types, and members instead of guessing. Locate DLLs with find_assembly_by_* or get_project_output_paths; do not hardcode build output paths. Start lean with search_members or get_types_from_assembly, then use get_type_info and filtered member tools. Use get_type_fields and get_type_events when inspecting COM enum constants, fields, and event interfaces. Request projection='full' only on tools that support it and only when exact parameters, attributes, or modifiers are needed.

Before inspecting a NuGet assembly with Sherlock, obtain the version actually referenced by the project with Roslyn CodeLens get_nuget_dependencies, then pass the exact version and target framework to find_assembly_by_nuget_package. For file references such as `Interop.TCatSysManagerLib`, use the resolved project HintPath. Confirm the selected file's identity and version with get_assembly_info before relying on its API metadata.

For referenced closed-source libraries, use Sherlock to inspect API metadata and XML documentation, and Roslyn CodeLens to find usages in the current solution. Do not call both servers for the same fact unless the first result is incomplete or a version mismatch is suspected.

Use IL analysis (get_method_calls or peek_il) only when public metadata, XML documentation, and source usages are insufficient.

After code changes, run the real project build and relevant tests. Treat compiler/build diagnostics and the exact referenced assembly version as authoritative.

## Architecture constraints

- Only the desktop gateway process may own or call DTE, `ITcSysManager`, or other TwinCAT COM objects. A focused XAE library may contain the wrappers, but it must execute only inside that process.
- Run all XAE COM calls sequentially on one STA thread with a message pump and OLE `IMessageFilter`.
- Do not pass COM objects across threads or processes.
- CLI and MCP are thin IPC clients; domain logic belongs in the gateway/core.
- Activation is always an explicit operation and must never follow a build implicitly.
- Do not close an XAE instance opened by the user unless explicitly requested.
- ADS is allowed in the MVP only through narrow read-only adapters. Runtime status may call `ReadState` on the fixed System Service port 10000, and TcUnit may read the configured completion and suite-count symbols. Both use the target selected and verified through XAE/profile. Do not add general symbol browsing, caller-selected ADS ports or NetIds, ADS writes, RPC, runtime state control, PLC debugging, or Automation Interface code editing.
- Do not add PowerShell scripts or modules as a product implementation layer. Development/bootstrap scripts are allowed only when they do not duplicate gateway domain behavior.
- Do not rewrite or revert reorder-only `.tsproj` changes. Detect and mark them as expected generated noise.

## XAE operation rules

- Select an XAE instance by normalized absolute `Solution.FullName`, not by “first instance found”.
- Support multiple running XAE instances.
- Determine completion from XAE events and verifiable postconditions, not fixed sleeps.
- A timeout is only an upper bound; it is not evidence of success or failure by itself.
- Build success requires a completed operation, zero compile errors, and no infrastructure failure.
- Store full raw output locally; return compact summaries and references by default.
- Treat unknown runtime state as `unknown`; never infer a trustworthy state from incomplete evidence.
- TcUnit completion requires the configured ADS completion symbol to report finished after the linked activation. Pass/fail comes from a fresh, valid xUnit report associated with that run. A missing symbol, timeout, missing report, or stale report is an error.

## File editing

- PLC code is edited in `.TcPOU`, `.TcGVL`, `.TcDUT`, and related project files.
- Never let an old unsaved XAE document overwrite an external file change.
- Surface unsaved-document or reload conflicts explicitly instead of silently retrying.
- Do not read or include an entire `.tsproj` in agent context merely to inspect reorder noise; use the classifier result and focused diffs.
- Until the classifier exists, report `.tsproj` changes as `unknown`, inspect only focused metadata/diffs, and never rewrite or revert the file merely to remove suspected reorder noise.

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

State-changing TwinCAT tests must be clearly marked and must fail closed unless an allow-listed remote test profile is supplied. Before finishing, build the affected projects and state exactly which checks were run and which were not.

## Safety and logging

- Activate only a configured and explicitly allowed target profile.
- Local TwinCAT activation, restart, runtime-mode changes, PLC login, and ADS writes are prohibited for this repository. State-changing tests may target only the explicitly allow-listed remote test bench.
- Read-only ADS adapters must use the target selected and verified through XAE/profile. MCP/CLI callers cannot supply an unrelated NetId, arbitrary port, or arbitrary symbol path.
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
