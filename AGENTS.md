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
- milestones and acceptance criteria: [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md);
- complete project configuration reference and safe examples:
  [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md).

Do not read all documents in full for every task. Read only the relevant
sections when changing process boundaries, public contracts, configuration,
XAE operation semantics, or milestone scope.

When creating or reviewing `twincat-gateway.json`, use the configuration
reference rather than inferring defaults from examples. Do not enable
activation, change the expected AMS NetId, broaden ADS access, or enable report
deletion without an explicit user decision.

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

Project versions are generated automatically by Nerdbank.GitVersioning from
`version.json` and Git history. Do not manually bump versions in project files
or generated artifacts for normal commits; change the base version in
`version.json` only when explicitly requested.

The repository CLI is an unverified development client and is outside MVP
acceptance. Do not fix it or run CLI-specific checks during MVP work; defer
those checks until post-MVP unless the user explicitly requests them.

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

The project MCP configuration requires Sherlock 2.12.0 and Roslyn CodeLens 2.14.0. On a new machine, install them once:

```powershell
dotnet tool install --global Sherlock.MCP.Server --version 2.12.0
dotnet tool install --global RoslynCodeLens.Mcp --version 2.14.0
```

If either server is unavailable, report the missing prerequisite instead of guessing.

- Use Roslyn CodeLens for solution symbols, relationships, source, diagnostics, and change impact. Start with a symbol or file overview, then drill into focused queries.
- The config launches Roslyn from the current checkout with a relative solution path. Never reuse an absolute path from another checkout. When uncertain, use `list_solutions` and require this checkout's path with no skipped projects. Manual launch from the repository root: `roslyn-codelens-mcp .\TwinCatGateway.sln`.
- Use Sherlock for external assemblies, packages, types, and members. Locate DLLs with `find_assembly_by_*` or `get_project_output_paths`; do not hardcode build output paths. Start with lean search/type tools and request `projection='full'` only when supported and necessary.
- Before inspecting a NuGet assembly, get its referenced version and target framework with Roslyn `get_nuget_dependencies`, pass both to Sherlock `find_assembly_by_nuget_package`, and confirm the result with `get_assembly_info`. For file references such as `Interop.TCatSysManagerLib`, use the resolved project `HintPath`.
- Use Sherlock for closed-source API metadata/XML documentation and Roslyn for solution usages. Query both for the same fact only when the first result is incomplete or a version mismatch is suspected. Use IL analysis only when metadata, documentation, and source usages are insufficient.

### Text search and output limits

- Use `rg` for raw text, configuration, documentation, logs, scripts, generated text, error codes/messages, and focused fallback when semantic tools are insufficient. Limit searches by path, glob, or specific pattern; inspect candidate files and narrow ranges. Exclude build output, caches, large fixtures, and binary-like data unless targeted.
- Keep normal model-visible command output within 200 lines or 8 KiB. Save larger output locally and inspect only its summary, counts, exit status, matches, and narrow relevant ranges. Do not load an entire large file, report, build log, or repository diff unless required. Keep full gateway output in local logs and return compact summaries/references.

## Architecture constraints

- Only the desktop gateway process may own or call DTE, `ITcSysManager`, or other TwinCAT COM objects. A focused XAE library may contain the wrappers, but it must execute only inside that process.
- Run all XAE COM calls sequentially on one STA thread with a message pump and OLE `IMessageFilter`.
- Do not pass COM objects across threads or processes.
- CLI and MCP are thin IPC clients; domain logic belongs in the gateway/core.
- Do not close an XAE instance opened by the user unless explicitly requested.
- ADS is allowed in the MVP only through narrow read-only adapters. Runtime
  status may call `ReadState` on the fixed System Service port 10000 and PLC
  runtime ports discovered from the exact selected `.tsproj`; TcUnit may read
  the configured completion and suite-count symbols. Both use the target
  selected and verified through XAE/profile. Do not add general symbol
  browsing, caller-selected ADS ports or NetIds, ADS writes, RPC, runtime state
  control, PLC debugging, or Automation Interface code editing.
- Do not add PowerShell scripts or modules as a product implementation layer. Development/bootstrap scripts are allowed only when they do not duplicate gateway domain behavior.

## XAE operation rules

- Select an XAE instance by normalized absolute `Solution.FullName`, not by “first instance found”.
- Support multiple running XAE instances.
- Determine completion from XAE events and verifiable postconditions, not fixed sleeps.
- A timeout is only an upper bound; it is not evidence of success or failure by itself.
- Build success requires a completed operation, zero compile errors, and no infrastructure failure.
- Treat unknown runtime state as `unknown`; never infer a trustworthy state from incomplete evidence.
- TcUnit completion requires the configured ADS completion symbol to report finished after the linked activation. Pass/fail comes from a fresh, valid xUnit report associated with that run. A missing symbol, timeout, missing report, or stale report is an error.

## File editing

- PLC code is edited in `.TcPOU`, `.TcGVL`, `.TcDUT`, and related project files.
- Never let an old unsaved XAE document overwrite an external file change.
- Surface unsaved-document or reload conflicts explicitly instead of silently retrying.
- Use the `.tsproj` classifier result and focused diffs; do not read or include an entire `.tsproj` merely to inspect generated noise.
- For `WhitespaceOnly` or `ExpectedReorderOnly`, report expected generated noise and do not rewrite or revert the file.
- For `Unknown` or `ContentChanged`, inspect only the compact classifier artifact and focused metadata/diffs. Never rewrite or revert the file merely to remove suspected generated noise.

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

Add or update coverage for every behavioral change, but schedule execution at
coherent validation checkpoints instead of after every edit:

- add or update tests for success and relevant failure paths;
- cover timeout and cancellation when the operation can wait;
- batch related source and test changes, using focused builds or static checks
  during the inner loop only when they provide new evidence;
- run the nearest unit tests first at a coherent local checkpoint;
- run contract tests after changing DTOs, IPC, CLI, or MCP;
- run real TwinCAT integration tests after the related DTE/COM/XAE changes
  have stabilized, preferably once near the end of the task;
- do not replace required real-XAE integration coverage with mocks.

Remote activation and state-changing TwinCAT tests are scarce validation
checkpoints, not an inner-loop tool. Accumulate compatible changes before one
activation/test run. If that run exposes several failures, collect the complete
bounded failure set, fix the batch, perform the necessary local build once, and
then reactivate/retest once. Re-run earlier only when runtime evidence is
essential to choose the implementation; never defer required final evidence
beyond task completion.

State-changing TwinCAT tests must be clearly marked and must fail closed unless
an allow-listed remote test profile is supplied.

## Safety and logging

- Remote activation, restart, or state-changing TwinCAT integration tests require an explicit user request and a configured, explicitly allowed remote target profile. A `Build`, `Rebuild`, or `Clean` request does not authorize activation or restart.
- Local TwinCAT activation, restart, runtime-mode changes, PLC login, and ADS writes are prohibited for this repository. State-changing tests may target only the explicitly allow-listed remote test bench.
- Read-only ADS adapters must use the target selected and verified through XAE/profile. MCP/CLI callers cannot supply an unrelated NetId, arbitrary port, or arbitrary symbol path.
- Never substitute another target, solution, or AMS identity automatically.
- Do not put secrets, credentials, or unnecessary PLC source content in logs.
- Keep destructive or machine-state-changing actions separate from read-only status and build operations.

## Documentation and scope

Update `docs/ARCHITECTURE.md` only when architecture, public contracts, operation semantics, or safety boundaries change.

Update `docs/IMPLEMENTATION_PLAN.md` only when milestone scope, ordering, or acceptance criteria change.

Do not expand the MVP without an explicit user decision. If code and documentation disagree, identify the conflict and make the selected behavior explicit rather than guessing.

## Completion checklist

Before completing a task:

- verify that the change stays within the requested scope;
- run the applicable affected build/tests and state exactly which checks ran and which did not;
- keep MCP behavior consistent with the gateway contract;
- note any behavior not verified on TwinCAT 3.1.4024.17;
- update only the documentation made inaccurate by the change.
