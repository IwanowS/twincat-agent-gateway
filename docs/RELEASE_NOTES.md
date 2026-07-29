# Release notes

## 0.1.22

- Added the read-only `twincat_get_xae_messages` MCP tool for bounded
  error/warning snapshots from the exact attached XAE solution.
- A matching `xae-messages` development CLI command exists, but repository CLI
  behavior and MCP parity are unverified for MVP and deferred until post-MVP.
- Runtime `Exception Code` and `Page Fault` messages from the XAE Error List
  are retained in runtime-alert and operation-error `details`.
- Reused the shared STA Error List reader for build diagnostics and added
  contract, adapter, policy, monitor, and marked real-XAE coverage.

## 0.1.0 MVP

TwinCAT Agent Gateway 0.1.0 provides:

- typed x86 STA automation of TwinCAT XAE without `dynamic`;
- exact solution-path ROT selection and gateway-owned XAE launch;
- Build/Rebuild/Clean with external-edit synchronization and compact
  diagnostics;
- XSD-backed PLC object validation and `.tsproj` reorder classification;
- exact project-graph fingerprints, configurable external-change policies,
  explicit disk synchronization, and fail-closed dirty-document handling;
- explicit allow-listed remote activation/restart with verified postconditions;
- one-PLC linked TcUnit completion and fresh xUnit result collection;
- versioned Named Pipe contracts, .NET 8 development CLI, and stdio MCP
  adapter with policy-checked `gateway_start` mediated by the interactive
  Windows Explorer session;
- project-local `twincat-gateway.json` discovery and manual/agent
  window/tray lifecycle;
- policy-checked `gateway_shutdown` that acknowledges the request before
  closing the desktop gateway;
- minimal WPF operations/status UI, per-user installer, and portable ZIP
  packaging.
- JSON responses, configuration, registry records, and structured NDJSON logs
  keep Unicode text readable as UTF-8 instead of escaping it as `\uXXXX`.

### Verified matrix (2026-07-28)

| Component | Verified |
|---|---|
| Host OS | Windows 10 22H2, build 19045 |
| TwinCAT XAE | 3.1.4024.17 |
| XAE host | 32-bit TcXaeShell 15.0 |
| Desktop target | .NET Framework 4.8 x86 |
| Installed .NET Framework release | 533325 |
| Repository SDK | .NET SDK 8.0.416 with `global.json` roll-forward |
| Remote test runtime | AMS NetId `192.168.3.31.1.1` |
| MCP SDK | stable `ModelContextProtocol` 1.4.1 |

Verified real-XAE scenarios include exact attach/launch, typed
`ITcSysManager`, Silent Mode without modal dialogs, Build/Rebuild/Clean,
configuration/platform switching, explicit remote activation and restart,
read-only ADS runtime verification, and a fresh linked TcUnit xUnit report.

The repository solution, unit tests, contract tests, focused desktop
integration tests, real-XAE checks, temporary per-user installation smoke, and
portable package build were run warning-free for this release candidate.
The newer project-graph policy, force-sync, and dirty-document scenarios still
require a repeat of their marked real-XAE checks on the configured bench.

### Known MVP limits

- one TcUnit PLC/report publisher per profile; multi-PLC aggregation is planned
  post-MVP;
- structural project changes require `reloadAll` or explicit force
  synchronization; the candidate graph must validate before reload;
- no general ADS client, ADS writes, RPC, PLC login, debugger, or local runtime
  control;
- no TwinCAT 4026/Visual Studio 2022 specialization;
- installer is per-user and repository-driven; there is no external package
  feed, admin installation, shortcut creation, or automatic process launch;
- full-day soak and clean-machine installation remain release-environment
  checks and are not inferred from automated tests.
