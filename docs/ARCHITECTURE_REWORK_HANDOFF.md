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
