---
name: twincat-diagnose
description: Diagnose TwinCAT Agent Gateway, XAE connection, build, activation, and TcUnit failures with compact status, cursor-based events, and one focused raw resource. Use when an operation fails, stalls, reports unknown state, or needs evidence beyond its compact result.
---

# TwinCAT Diagnose

1. Call `twincat_status` and identify the gateway, XAE, solution, target,
   runtime, and latest-operation state.
2. Call `twincat_get_diagnostics` with the last `eventStreamId` and
   `afterCursor`. Use `minimumSeverity: error` when only errors are needed.
3. Follow the unified event sequence around the failed operation. Preserve the
   returned cursor for the next read.
4. If the runtime is in `exception` and the retained `details` are absent or
   insufficient, call `twincat_get_xae_messages` once for the bounded current
   XAE Error List error/warning snapshot.
5. Read one referenced raw resource only when the compact events do not explain
   the failure. Choose the build log, XAE log, xUnit report, or focused project
   diff that corresponds to the failing stage.

Do not fetch every log, repeatedly read the Error List, inspect the full
`.tsproj`, or fetch the full xUnit report by default. Do not repeat
state-changing operations as a diagnostic probe.

Treat `unknown`, timeout, missing event, and execution-context mismatch as
evidence gaps. Report them honestly. For interactive XAE discovery, the
desktop gateway must run under the same Windows user, session, and integrity
level as XAE.
