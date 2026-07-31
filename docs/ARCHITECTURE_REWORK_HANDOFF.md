# Architecture v2 rework handoff

This tracked document is the durable, append-only handoff for the architecture
v2 migration. Bulky logs may remain under `.session/`, but every acceptance
claim, expected failure, unrun check, and next scope is recorded here.

An implementation-complete session is not accepted until its tracked local and
required real-XAE gates pass. Entries are appended after final checks; prior
entries are not rewritten to make later results appear contemporaneous.

## 2026-07-31 — S6 corrective validation gate

### Scope

Review and correct the S0-S6 execution process without starting S7 or changing
any remote TwinCAT state.

### Corrected

- Corrected the `GatewayApplicationService` capability evaluator field
  reference at all three call sites.
- Made nullable PLC-project selection explicit in `XaeBuildTargetResolver`.
- Preserved analyzer-clean production code by using static profile helpers and
  read-only/array concrete result types where required.
- Added tracked migration checkpoints for the completed Core, observation, and
  XAE build-event slices. No removed v1 contract was reintroduced.
- Split progress into implementation, tracked local validation, real-XAE, and
  acceptance states.

### Checks at this gate

- Contract tests: 23/23 on net8.0 and 23/23 on net48.
- Core migration production target (`netstandard2.0`): build passed with zero
  warnings and zero errors.
- Core migration test target (`net8.0`): 82/82 passed.
- Observation migration tests (`net48`, x86): 59 passed; the one real-XAE test
  was skipped because no fixture was available.
- XAE build-event migration tests (`net48`, x86): 7/7 passed.
- The full solution remains intentionally red while v1 consumers are migrated;
  the exact current failure inventory is recorded below after the final build.

### Open acceptance gates

- S4 read-only observation against the configured TwinCAT 3.1.4024.17 bench.
- All S6 required real-XAE build cases listed in the plan.
- S5 cancellation coverage is only prior excluded local evidence. It must move
  onto the tracked operation-journal surface during S9; recreating deleted v1
  journal DTOs for a synthetic checkpoint is prohibited.
- Canonical Configuration, Unit, and Integration test projects remain blocked
  by their intentional v1 consumer cascade.

### Environment and next scope

The read-only session probe observed no Gateway process, no ROT XAE session,
and no loaded solution. No activation, restart, TcUnit run, XAE launch, or
fixture mutation was performed. After this gate, the next implementation scope
is S7 only; its completion marker must follow its final checks.

### Expected broken state

`dotnet build TwinCatGateway.sln --no-restore --configuration Debug` remains
expected-red with 0 warnings and 55 `CS0246` errors. The compiler stops in ten
first-order consumers:

- Core: `GatewayApplicationService`, `GatewayStatusSnapshotStore`,
  `LocalLogStore`, `OperationQueue`, `OperationStore`,
  `RuntimeOperationPolicy`, and `StoredOperation`;
- IPC: `GatewayDispatchResult`, `GatewayProtocolHandler`, and
  `NamedPipeGatewayClient`.

The missing v1 symbols are `HealthResult`, `GatewayStatusResult`,
`GatewayDiagnosticsResult`, `OperationAccepted`, `OperationDetails<T>`,
`OperationSummary`, `CancelOperationResult`, `ResourceKind`, `RuntimeAlert`,
`RuntimeMode`, `GatewayResponse<T>`, and `BuildSummary`. A focused source search
finds 23 direct symbol consumers across Core, IPC, Client, CLI, Desktop, and MCP;
`GatewayEventJournal` is an additional aggregate-status consumer without its
own top-level missing-symbol error. These consumers are assigned to S7-S10 and
were not repaired by restoring v1 contracts.

## 2026-07-31 — S7 Target Config and start/restart

### Scope and tested code

S7 was implemented and accepted on production code commit `8dee04e`. Its three
code commits are:

- `6e6ef9c` — standard Target Config transition;
- `715022b` — explicit Target start/restart semantics;
- `8dee04e` — removal of the recovery-specific contract and service.

The implementation adds transport-independent Target operations with typed
before/after direct observations. Config is a fresh-Config no-op or one guarded
Config command with best-effort pre-command ADS/XAE evidence. Start/restart
requires a fresh direct observation, selects Start from Config/Stop and Restart
from Run, and rejects unreadable, Exception, Transitioning, or Unknown states
before a side effect. Both commands revalidate capability, operator lock,
solution, and AMS identity and require a fresh direct postcondition. Timeout and
cancellation evidence records whether the command had started.

The legacy recovery contracts, operation kind, policy service, IPC method, CLI
command, MCP tool, events, errors, and UI naming were removed without aliases.
The public Target MCP surface remains assigned to S9; activation and TcUnit
verification remain assigned to S8.

### Local acceptance on `8dee04e`

- Contract tests: 26/26 on net8.0 and 26/26 on net48.
- Target migration suite: 23/23 on net8.0; its production slice also built for
  netstandard2.0 with zero warnings and zero errors.
- Core migration production target: netstandard2.0 build passed with zero
  warnings and zero errors; Core migration tests passed 82/82 on net8.0.
- Observation migration tests (`net48`, x86): 59 passed and one opt-in real-XAE
  test skipped.
- XAE build-boundary regression (`net48`, x86): 7/7 passed.

The full solution remains expected-red with 0 warnings and 55 `CS0246` errors.
The missing-symbol groups are `RuntimeAlert` (12), `GatewayStatusResult` (10),
`OperationAccepted` (8), `ResourceKind` (8), `OperationSummary` (6),
`GatewayDiagnosticsResult` (3), `BuildSummary` (2), `GatewayResponse<T>` (2),
`OperationDetails<T>` (2), `CancelOperationResult` (1), and `HealthResult` (1).
They occur in `TwinCatGateway.Core` (43) and `TwinCatGateway.Ipc` (12). No
recovery-specific missing symbol remains; this is the deliberate S8-S10 v1
consumer cascade rather than restored deprecated behavior.

### Exact-fixture acceptance on `8dee04e`

The fixture identity was verified before the state-changing cycle:

- solution: `tests/fixtures/TC3_SimpleProject/TC3_SimpleProject.sln`;
- Target AMS NetId: `192.168.3.31.1.1`;
- attached XAE PID: `14480`, exact solution and target, SysManager available,
  synchronization Confirmed, and zero dirty documents;
- direct System Service observation at port 10000: Run, raw ADS state 5,
  raw device state 1;
- direct PLC observation at port 851: Run, raw ADS state 5, raw device state 0.

That first read-only observation, captured at `2026-07-31T14:41:41Z`, closes
the S4 real-XAE gate because XAE, direct System Service, and PLC provenance and
raw evidence were all preserved.

Exactly one authorized cycle ran from `2026-07-31T14:42:32Z` through
`2026-07-31T14:42:43Z`, without retries:

- Config: Run raw 5/device 1 -> Config raw 15/device 1, 5479 ms; the bounded
  fault snapshot reported zero XAE errors and warnings;
- Start: Config raw 15/device 1 -> Run raw 5/device 1, 3932 ms;
- Restart: Run raw 5/device 1 -> fresh Run raw 5/device 1, 2052 ms.

The final XAE observation retained the same PID, solution, AMS identity,
Confirmed synchronization, and zero dirty documents. The Target finished in
fresh direct Run. The XAE session was intentionally left open and attached.
No activation, standalone build, TcUnit, or fault injection was performed on
the bench. The S6 real-XAE matrix therefore remains pending.

The excluded local evidence is `.session/S7_PRE_OBSERVE.json`,
`.session/S7_TARGET_CYCLE.json`, and `.session/S7_FULL_BUILD.log`. The excluded
real harness used an in-memory exact fixture profile with only Config and
start/restart enabled. The tracked `twincat-gateway.json` contains unrelated
user changes, was not used for this cycle, and remains unstaged.

### Final documentation boundary and next scope

All code, local, and real-XAE claims above are tied to `8dee04e`. The final HEAD
is intentionally one later documentation-only commit containing this handoff;
it changes no tested production code. Local checks are repeated after that
documentation commit. The next implementation scope is S8 only; S9 and S10
remain deferred.

## 2026-07-31 — S8 activation and TcUnit verification cutover

### Scope and production commits

S8 production implementation is complete through `3404004`:

- `c687c95` — removed the standalone recent-build/direct-runtime activation
  precondition without restoring build-as-deploy behavior;
- `a7bb7b9` — replaced activation booleans and aggregate completion with
  `finalTargetMode`, `verification`, and five typed stage outcomes;
- `ee948d5` — attached TcUnit to activation and Target start/restart in the
  same root operation, with completion and report baselines captured before
  the root side effect;
- `3404004` — removed the normal `getTestResults` IPC/client/CLI/MCP route,
  separate test operation kind and lifecycle events, and retained xUnit as an
  immutable root-operation resource.

Native activation is issued once and observes its own compilation. A failed
verification no longer erases a successful deploy stage. Restart-only
verification preserves S7 `Start` versus `Restart` selection and its fresh
direct Run postcondition. When the completion flag is already true at baseline,
verification requires a reset-to-false followed by a new true edge; it then
requires a fresh stable XML report and applies the configured zero-test policy.

### Local validation on `3404004`

- Contract serialization: 28/28 on net8.0 and 28/28 on net48.
- Core S2-S5 migration suite: 82/82 on net8.0.
- Observation migration suite (`net48`, x86): 59 passed, one opt-in real test
  skipped.
- S6 XAE build-event migration suite (`net48`, x86): 7/7.
- S7 Target operations migration suite: 23/23 on net8.0.
- S8 activation-verification migration suite (`net48`, x86): 12/12. It covers
  successful and failed reports, stale report rejection, an already-true
  completion baseline requiring a new edge, unreadable baseline failure before
  the root side effect, ADS/symbol errors, timeout, cancellation, invalid XML,
  and fail/warn/allow zero-test policies.

The full solution remains expected-red with zero warnings and 54 `CS0246`
errors: `RuntimeAlert` (12), `GatewayStatusResult` (10), `OperationAccepted`
(8), `ResourceKind` (8), `OperationSummary` (6),
`GatewayDiagnosticsResult` (3), `BuildSummary` (2), `GatewayResponse<T>` (2),
`CancelOperationResult` (1), `HealthResult` (1), and `OperationDetails<T>` (1).
They occur in `TwinCatGateway.Core` (42) and `TwinCatGateway.Ipc` (12). The one
fewer `OperationDetails<T>` error than the S7 baseline is the intentional
removal of the standalone test-result lookup; no deprecated v1 DTO was
reintroduced. The excluded exact log is `.session/S8_FULL_BUILD.log`.

### Real-XAE gate blocked without side effects

The before and after ROT checks both found one attached session: XAE PID
`14480`, `TcXaeShell.DTE.15.0`, with exact solution
`tests/fixtures/TC3_SimpleProject/TC3_SimpleProject.sln`. The tracked but
unstaged user configuration names AMS NetId `192.168.3.31.1.1`, PLC port 851,
and enables TcUnit verification, but those live identities were not accepted
as verified because the Gateway could not load the profile.

The installed MCP/Gateway is a v1 binary and returned
`GATEWAY_NOT_READY` at `gateway.config.validate`: it supports only schema
version 1 and expected the removed profile-level `solution` field. Its public
activation tool also still exposes `runAfterActivation`, `waitForTcUnit`, and a
separate `twincat_get_test_results`. Running that binary would validate the old
workflow, not the production code above. No build, activation, restart, TcUnit,
recovery, XAE close, or remote state change was attempted after this mismatch.

Therefore the inherited S6 matrix and the combined S8
`activation + tcunit -> fresh result -> target restart + tcunit -> second fresh
result` checkpoint remain pending. S8 is implemented and locally green but is
not accepted. The next session must first provide a v2-capable tracked
Gateway/executable path (normally through the remaining S9 consumer cutover),
then run both remote gates against the exact fixture and finish in fresh Target
Run without closing the attached XAE.

The tracked `twincat-gateway.json` user changes and all `.session/` contents
remain excluded from commits.

## 2026-08-01 — S9 MCP, IPC, and operation journal v2

### Scope and commits

S9 is implemented locally through the following thematic commits:

- `e830da8` — immutable v2 operation journal and exact-ID cancellation;
- `c682140` — typed v2 IPC and client calls;
- `6a79758` — object-oriented v2 MCP tools;
- `74eb77f` — canonical state, diagnostic, operation, artifact, doc, and log
  resources;
- `330414b` — v1 MCP removal and metadata-generated reference;
- `13f1018` — checkout Desktop and CLI v2 consumer migration;
- `0129ffc` — tracked S9 MCP and real-XAE acceptance suites;
- `313f04f` — remaining Unit/CLI/client test consumers migrated to v2;
- `9ad3394` — v1 standalone IntegrationTests project retired from the solution,
  with six applicable tests moved into tracked v2 migration suites.

The public MCP adapter now exposes exactly nine tools and 22 resource templates.
All Gateway-owned mutations, including preflight failures, receive an exact
operation ID. `gateway_start` and `gateway_shutdown` deliberately return typed
lifecycle results without an ID because process lifecycle is outside the
Gateway journal. Operation artifacts and events resolve only by exact ID; URI
parsing has no `latest`, `last`, relative, malformed-escape, query, fragment, or
missing-artifact fallback.

Client mutations use receipt-then-poll semantics. Cancellation after a receipt
forwards exactly one bounded cancellation request for that operation ID. Native
MCP structured content, output schemas, and resource-link blocks are used.
The checked-in MCP reference and installed `twincat-doc://mcp` are generated
from the same metadata catalog and pass drift check mode.

### Final local validation

- full `TwinCatGateway.sln` Debug build: zero warnings, zero errors;
- Unit tests: 176/176;
- contract serialization: 28/28 on net8.0 and 28/28 on net48;
- Core S2-S5 migration suite: 82/82;
- operation journal: 3/3;
- observation (`net48`, x86): 59 passed, one opt-in real-XAE test skipped;
- XAE build event suite: 7/7;
- Target operations: 23/23;
- activation/TcUnit verification: 12/12;
- IPC v2: 5/5 on net8.0 and production net48 build passed;
- MCP S9: 20/20, including exact listing, schemas, structured content,
  resource links, compact bounds, URI security, missing artifacts,
  cancellation, and stdio protocol-only stdout;
- S9 real-XAE harness: local default run skipped both opt-in tests; the later
  checkout stand run passed exact-profile PLC Build and stopped the S8 chain at
  TcUnit report baseline access before activation side effects.

The complete solution contains no remaining v1 compile failure. The old
standalone `TwinCatGateway.IntegrationTests` source directory remains tracked
but is no longer a solution project; broad deletion was intentionally avoided
after six active tests were relocated. It is historical source, not acceptance
surface. A source search confirms no public v1 tool/resource name in the MCP
adapter.

### Real-XAE boundary and next scope

The checkout-built Desktop PID `44656` was started with the explicit tracked
configuration and launched exact-solution XAE PID `40348` under
`TcXaeShell.DTE.15.0`. Same-user ROT showed exactly one loaded solution:
`tests/fixtures/TC3_SimpleProject/TC3_SimpleProject.sln`. The S9 harness then
confirmed the exact config/profile/solution, DTE availability, confirmed
synchronization, and zero dirty documents before its first operation.

Standalone PLC Build for logical project `PlcProject2` passed with exact
operation ID `2259978eab20433e89a38024eeeb73b3`, zero compile errors/warnings,
and no accepted project-noise changes. Fresh direct observations before and
after remained Target Run (AMS `192.168.3.31.1.1`, System Service port 10000,
raw ADS 5/device 1), PLC 851 Run, and PLC 852 Run.

The combined S8 chain stopped before activation side effects. Root operation
`b015f8f0e47c42a982e3288381417100` failed during TcUnit baseline preparation
because the Gateway user received `UnauthorizedAccessException` while deleting
`\\WIN-T077ADA\c\TwinCAT\3.1\Boot\tcunit_xunit_testresults.xml` with
`allowDeleteExistingReport=true`. No Target restart followed and no retry was
attempted. Subsequent monitor evidence retained fresh Target and both PLC
runtimes in Run. The post-check ROT retained the same exact XAE PID and
solution; XAE and checkout Gateway were intentionally left open.

The remaining S6 build matrix and combined S8
`activate + tcunit -> target restart + tcunit -> fresh final Run` gate remain
pending. To resume S8, the operator must either grant the interactive Gateway
user delete access to the configured report or explicitly approve changing the
profile to `allowDeleteExistingReport=false` and restarting the checkout
Gateway. The PLC Exception case additionally waits for the guarded, disabled
by default project define workflow and byte-for-byte restoration. XAE must
remain open and attached. S10 remains the next consumer/UI scope; S9 only made
Desktop compile and consume v2 observations and did not implement the planned
UI redesign.

The user-modified tracked `twincat-gateway.json` and all `.session/` contents
remain unstaged and excluded from every S9 commit.

## 2026-08-01 — S9 corrective stand continuation

The operator granted delete access and changed the TcUnit report share from
the administrative `\\WIN-T077ADA\c\...` path to
`\\WIN-T077ADA\TwinCAT\3.1\Boot\tcunit_xunit_testresults.xml`. The checkout
Gateway was restarted with the explicit user configuration. Same-user ROT and
v2 resources confirmed exactly one `TcXaeShell.DTE.15.0` session for the
fixture solution, AMS `192.168.3.31.1.1`, confirmed synchronization, zero
dirty documents, and fresh Target/PLC Run before the next operation.

The combined S8 harness reached the native **Activate Configuration** dialog,
but its dialog supervisor timed out with "did not present the expected
activation confirmation dialog" while the dialog was visibly present. The
operator cancelled it, restarted XAE after a separate project-load issue, and
explicitly deferred investigation. No Target restart or accepted two-report
TcUnit chain followed, so S8 remains pending.

The first S6 PLC Rebuild attempt then crashed checkout Gateway PID `35412`.
Windows Application evidence recorded `0xc0000005` in `ntdll.dll`, with the
managed stack entering
`IVsSolutionBuildManager2.StartUpdateSpecificProjectConfigurations`. XAE PID
`13488` remained alive and exact-solution visible in ROT. The corrective commits
are:

- `ba91b6d` — replace the unsafe mixed hierarchy/configuration array call with
  a single-project VSSDK request and add explicit Clean/Rebuild flag tests;
- `1dc7e43` — align the public `detail=full` value with the generated MCP
  schema and CLI help;
- `e08244e` — remove nullable COM arguments after the first replacement failed
  closed with `0x800706F4` and exact operation ID
  `500150efd9e6436c8b6c4aad140212d0` before side effects.

After `e08244e`, logical-project Rebuild passed with operation
`c4d027e4a5f24fc78db1cdf48eea4f31`, zero errors/warnings, and no expected
project noise. Solution-scope Build passed with operation
`d169aa159499482bb726f52df0dcde98`, also with zero errors/warnings and no
noise.

For the compile-error/external-reload gate, the exact original bytes of
`PlcProject2/POUs/MAIN.TcPOU` were recorded as SHA-256
`CDD0C403E864CA670B29073999E6D5A59354A14209DD92A622CB2EDE053222B5`.
A temporary `f1 := ;` edit produced expected `BUILD_FAILED` operation
`0c952f7cb46e474b9f669b48567a8b0b` with the exact file, line 12, and
`Expression expected instead of ';'`. The file was restored from current HEAD
after line-ending normalization was detected, the exact original hash and
clean Git status were re-confirmed, and clean reload/build operation
`c0d5a67aca4e44a08b17a407bf408e2f` passed with zero errors/warnings.

Final read-only evidence retained Gateway PID `49612`, attached XAE PID
`13488`, the exact fixture solution, confirmed synchronization, zero dirty
documents/dialogs, and fresh Run for Target System Service plus PLC ports 851
and 852. The full solution build remained zero warnings/errors; the XAE build
event suite is 9/9 and contract serialization is 29/29 on both net8.0 and
net48.

S6 remains pending only for an operator-created dirty XAE document, a real
`.tsproj` noise occurrence, and the guarded PLC Exception workflow. S8 remains
pending by operator direction. The user-owned `twincat-gateway.json` and
`.session/` remain unstaged. XAE and checkout Gateway are intentionally left
open and attached.

## 2026-08-01 — S6 dirty-document gate through v2 state

The operator created an unsaved edit in the exact fixture
`PlcProject2/POUs/MAIN.TcPOU`; XAE visibly marked the editor `MAIN*`. Initial
checkout Gateway observations incorrectly returned an empty `dirtyDocuments`
array. Read-only same-user ROT/DTE evidence showed the exact path with
`Document.Saved=false`, while the Target System Service and PLC ports 851/852
remained fresh Run and no dialog was present.

The corrective commits are:

- `693b460` and `61f3dc8` — enumerate RDT document data and resolve TwinCAT
  hierarchy monikers to exact project paths;
- `dff31c3` — refresh and query the Visual Studio RDT v3 dirty-state service;
- `be6db5a` — query the TwinCAT hierarchy item dirty flag and preserve exact
  dirty paths through XAE snapshots, clones, and the v2 contract state.

The checkout-built v2 state then exposed the exact
`PlcProject2/POUs/MAIN.TcPOU` path. One compile-only PLC Build was issued and
failed as required with exact operation ID
`d969e92c019547978d8538df4a2c5e1b`, code `DIRTY_XAE_DOCUMENT`, component
`xae`, stage `xae.workspace.dirty`, and `sideEffectsStarted=false`. Its exact
operation event stream contains only queued, started, and failed events; no
compile/deploy stage ran. A postcondition read confirmed the editor remained
dirty and Target remained fresh Run.

Validation after the correction: full solution build zero warnings/errors;
Unit 176/176; XAE build migration 10/10; MCP v2 20/20; contract serialization
29/29 on both net8.0 and net48. S6 now remains pending only for a real
`.tsproj` noise occurrence and the guarded PLC Exception workflow. S8 remains
deferred by operator direction. The operator must still undo/discard the
deliberate unsaved `MAIN*` edit before further clean/synchronization gates.
The user-owned `twincat-gateway.json` and `.session/` remain unstaged. XAE and
checkout Gateway are intentionally left open and attached.

The operator then undid the deliberate edit and closed only the `MAIN` editor
without saving when XAE retained its dirty marker. The disk file remained Git
clean with the original SHA-256 above. Exact synchronization operation
`1825f5da6c2d46c8aef06b2b595a5d72` succeeded with no discarded documents;
v2 XAE state returned `synchronizationState=confirmed`, an empty
`dirtyDocuments` array, and a confirmed ten-file source graph.

A final compile-only `PlcProject2` Build succeeded as operation
`7a7a392aa93543fd832ebf3a3fadf5c3` with zero errors/warnings. Postconditions
retained the exact attached XAE identity, no dialogs or dirty documents, and a
fresh direct Target Run. The fixture tree stayed Git clean and this attempt
did not produce a real `.tsproj` noise occurrence, so that observational gate
remains pending rather than being replaced with synthetic evidence.

## 2026-08-01 — Pre-S10 package and architecture gate

The prerequisite tooling gate was completed without beginning S10. Commit
`e85be8d` centralizes all direct NuGet versions in
`Directory.Packages.props`; common package references remain in the existing
`Directory.Build.props`, no `Directory.Build.targets` was added, and transitive
pinning remains disabled. The activation-verification net48 harness retains its
pre-gate `Microsoft.Extensions.Logging.Abstractions` 8.0.2 resolution through a
local `VersionOverride`; the central default is 10.0.7.

The new multi-target `TwinCatGateway.ArchitectureTests` project uses
`NetArchTest.eNhancedEdition` 1.4.5. It enforces the documented assembly
boundaries and prevents direct EnvDTE/Visual Studio interop use outside Xae,
plus direct COM/ADS use from Desktop `*ViewModel` and `*Row` types. Failure
output includes each failing type and `IType.Explanation`. CLI and Desktop are
analyzed from their solution-built artifacts because they share the
`twincat-gateway` assembly/package identity. Acceptance therefore builds the
complete solution first, and a missing executable artifact fails explicitly.

Package-assets comparison covered every pre-existing project and target
framework, including the historical IntegrationTests project outside the
solution: 625 canonical project/TFM/package-version entries before and 625
after, with zero differences. The architecture project adds exactly
`NetArchTest.eNhancedEdition` 1.4.5 and its `Mono.Cecil` 0.11.6 dependency.
No architecture-rule violation was found.

Local acceptance evidence:

- solution restore and Debug build: zero warnings/errors;
- architecture: net8.0 7/7; net48/x86 7/7;
- Unit 176/176; Configuration 81/81; Migration 82/82;
- Contract net8.0 29/29 and net48/x86 29/29;
- OperationJournal 3/3; TargetOperations 23/23; IPC v2 net8.0 5/5;
- MCP v2 20/20; XAE build 10/10; ActivationVerification 12/12;
- Observation 59 passed and one remote ADS opt-in test skipped.

The S9 real-XAE project was not run. No XAE, Config, activation, restart,
TcUnit, or remote Target operation was issued. Public DTO/IPC/MCP/configuration
contracts and runtime behavior are unchanged, and S10 remains unstarted. The
user-owned `twincat-gateway.json` and `.session/` remain unstaged.
