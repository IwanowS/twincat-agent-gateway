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
