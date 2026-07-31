# AGENTS.md

## Project overview

TwinCAT Agent Gateway is a local Windows desktop application that exposes
profile-scoped TwinCAT 3 XAE and remote Target operations to coding agents.

Target environment:

- TwinCAT 3.1.4024.17;
- Visual Studio 2019 or compatible XAE Shell;
- Windows 10/11;
- .NET Framework 4.8 x86 desktop/COM host;
- .NET 8 MCP adapter.

The repository is in an intentionally breaking architecture-v2 rework. The
current code may still implement v1 contracts, and intermediate sessions do
not have to keep the complete solution working.

## Authoritative documents

- target object model and operation semantics:
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md);
- accepted and deferred decisions:
  [`docs/ARCHITECTURE_DECISIONS.md`](docs/ARCHITECTURE_DECISIONS.md);
- target configuration schema:
  [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md);
- target MCP tools/resources:
  [`docs/MCP_REFERENCE.md`](docs/MCP_REFERENCE.md);
- agent workflows and deferred debugging scenarios:
  [`docs/WORKFLOWS.md`](docs/WORKFLOWS.md);
- ordered multi-session implementation:
  [`docs/ARCHITECTURE_REWORK_PLAN.md`](docs/ARCHITECTURE_REWORK_PLAN.md);
- durable append-only migration handoff:
  [`docs/ARCHITECTURE_REWORK_HANDOFF.md`](docs/ARCHITECTURE_REWORK_HANDOFF.md);
- implemented v1 details useful during migration:
  [`docs/ARCHITECTURE_V1_BASELINE.md`](docs/ARCHITECTURE_V1_BASELINE.md) and
  [`docs/CONFIGURATION_V1_BASELINE.md`](docs/CONFIGURATION_V1_BASELINE.md);
- historical MVP milestones:
  [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md).

For rework tasks, take one session scope from the rework plan. Do not restore
deprecated v1 behavior merely to make an intermediate build green.

Do not read every document in full for every task. Start with the target
architecture, the relevant decision, and the assigned session section. Read
the v1 baseline only for implementation details being migrated.

## Repository layout

```text
src/       production projects
tests/     unit, contract, and real-XAE integration tests
skills/    task-oriented agent workflows
docs/      target contracts, decisions, plans, and implementation references
scripts/   development and packaging helpers
```

Add a nested `AGENTS.md` only when a component develops materially different
commands or constraints.

## Canonical commands

The .NET 8 SDK is required for normal repository development. The full
desktop solution additionally needs Windows and .NET Framework 4.8 reference
assemblies.

```powershell
dotnet restore TwinCatGateway.sln
dotnet build TwinCatGateway.sln --no-restore --configuration Debug

dotnet test tests/TwinCatGateway.UnitTests/TwinCatGateway.UnitTests.csproj `
  --no-build --configuration Debug
dotnet test tests/TwinCatGateway.ContractTests/TwinCatGateway.ContractTests.csproj `
  --no-build --configuration Debug
```

Use the pinned .NET SDK. Visual Studio 2019/XAE Shell is the automation
target, not the repository build orchestrator.

Project versions come from Nerdbank.GitVersioning and `version.json`. Do not
manually edit generated version properties.

The repository CLI is a development client, not an acceptance surface, unless
the task explicitly targets it.

## Tool selection and output

- Use Roslyn CodeLens for solution symbols, relationships, source diagnostics,
  and change impact.
- Use Sherlock for external assemblies/packages. Resolve package version and
  target framework first; do not hardcode build-output paths.
- Use `rg` for focused text, configuration, documentation, logs, scripts,
  generated text, and fallback searches.
- Keep normal model-visible output below 200 lines or 8 KiB. Save large output
  locally and inspect counts, exit status, and narrow relevant ranges.
- Do not query multiple tools for the same fact unless the first result is
  incomplete or a version mismatch is suspected.

The configured Roslyn server must load this checkout's
`TwinCatGateway.sln` with no skipped projects.

## Target architecture invariants

### Object boundaries

Public contracts distinguish:

- Gateway process;
- XAE session;
- XAE-observed TwinCAT system state;
- direct remote Target System Service state;
- each PLC runtime state;
- operation/artifacts.

Do not recreate an aggregate `runtime mode`. Preserve observation source,
AMS address/port, raw ADS state, raw device state, timestamp, and error.

### Profile authority

Profile identifies solution and optional target and defines maximum
capabilities. Agent calls pass profile; Gateway resolves and revalidates
actual resources immediately before side effects.

Do not add another agent-side confirmation layer for a configured capability.
An explicit user prohibition in the current task still wins. A build-only task
remains compile-only because activation is outside the requested workflow, not
because the profile needs another confirmation.

### Capabilities and operator locks

- static capability `false` is absolute;
- operator session locks can only reduce capabilities;
- read-only state/diagnostics remain available while mutating actions are
  locked;
- XAE close additionally uses exact-PID session consent;
- Gateway-launched XAE defaults close consent on; attached XAE defaults it off;
- no `Process.Kill`.

### COM/XAE

- Only the desktop process may own/call DTE, `ITcSysManager`, or TwinCAT COM.
- Run all COM calls sequentially on one STA thread with message pump and OLE
  `IMessageFilter`.
- Do not pass COM objects across threads/processes.
- Select XAE by normalized absolute `Solution.FullName`.
- Support multiple running XAE instances without choosing the first one.
- Determine completion from events and postconditions, not fixed sleeps or
  return-from-command alone.

### Source files and workspace

- Agent edits `.TcPOU`, `.TcGVL`, `.TcDUT`, and related project files on disk.
- Gateway source discovery and synchronization must use one exact project
  graph resolver.
- Expose profile source roots/files without scanning unrelated neighboring
  directories.
- Never let a stale/dirty XAE buffer overwrite an external edit.
- Surface dirty/reload conflicts; never silently save/discard.
- Preserve the attach-scoped file-change guard and typed VSSDK reload.
- Do not rewrite/revert `.tsproj` or `.tmc` generated noise.

### Build, activation, and tests

- Default standalone build scope is the PLC project and is primarily a compile
  check.
- Do not block PLC compilation because Target/PLC state is Exception.
- Build never performs Config, activation, or restart.
- Activation observes its own native XAE compilation; it does not require a
  standalone/recent build.
- Config is a normal Target operation from any state, not recovery.
- Target start/restart means Config/Stopped -> start and Run -> restart.
- TcUnit is a verification stage of activation or target restart.
- A failed verification does not erase a successful deploy stage.
- Missing/stale completion/report remains failure evidence.

### Deferred scope

Do not implement these without a new approved plan item:

- programmatic project variant selection;
- XAE online debugger;
- arbitrary ADS symbol read/write/watch;
- force/release force;
- PLC application Run/Stop/Reset;
- online change/download/login/logout;
- breakpoints/stepping/call stack;
- multi-PLC TcUnit aggregation.

The operator selects the project variant manually in XAE for phase 1.

## Real-XAE development

Real-XAE checks require the explicitly configured remote TwinCAT 3.1.4024.17
test bench described in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

A profile capability is standing authorization for its exact configured test
bench when the assigned workflow requires the corresponding operation. Do not
substitute another target or perform an operation the user explicitly
prohibited.

Run `scripts\Get-XaeSessions.ps1 -AsJson` outside the agent sandbox before and
after fixture-changing real-XAE work. DTE/ROT checks must execute under the
same interactive Windows account, session, and integrity level as XAE.

Report real-XAE tests as not run when the environment was unavailable. Do not
replace required real-XAE evidence with mocks.

## Testing policy

- Add/update tests for every behavioral change.
- Include success and relevant failure paths.
- Cover timeout/cancellation for waiting operations.
- Add capability-disabled, operator-locked, and identity-mismatch cases.
- Run the nearest unit tests at coherent checkpoints.
- Run contract tests after DTO/IPC/MCP changes.
- Batch related changes before scarce remote activation/TcUnit checkpoints.
- During the breaking rework, an expected red build is permitted only when the
  session handoff lists exact failures and remaining consumers.

## Code and error style

- Use C# with nullable reference types where supported.
- Use English identifiers, public APIs, error codes, and log properties.
- Keep DTOs versioned, serializable, and independent of COM/WPF/MCP types.
- Every operation error includes stable code, component, stage, operation ID,
  retryability, and side-effect evidence.
- Preserve HRESULT/exception context in local logs.
- Never use empty catches or suppress an exception without recording why.
- Pass cancellation and deadlines through long-running operations.
- Keep UI presentation separate from application/domain state.

## Documentation policy

Update:

- `ARCHITECTURE.md` for object boundaries, operation semantics, or invariants;
- `ARCHITECTURE_DECISIONS.md` for accepted/deferred architecture choices;
- `CONFIGURATION.md` for schema/capability changes;
- `MCP_REFERENCE.md` for public tool/resource changes;
- `WORKFLOWS.md` for task sequences and deferred debugging use cases;
- `ARCHITECTURE_REWORK_PLAN.md` for session progress, ordering, and acceptance.

After typed MCP schemas exist, generate the installed MCP/configuration
reference from the same metadata instead of maintaining duplicate contracts.

Keep v1 baseline files historical. Do not edit them to describe v2 behavior.

## Git and completion

- Inspect `git status --short` before editing and before completion.
- Preserve unrelated user changes.
- Do not use destructive reset/clean/revert.
- Review focused diffs.
- Keep commits thematic and independently understandable.
- Commit only when requested or when the assigned project workflow explicitly
  delegates commit authority.

Before completing a task:

- verify scope against the assigned rework session;
- update plan progress/handoff when implementation work was done;
- run and report exact applicable checks;
- state expected broken state and unrun real-XAE checks;
- confirm no deprecated contract was unintentionally reintroduced;
- update only documentation made inaccurate by the change.
