---
name: twincat-diagnose
description: Diagnose target TwinCAT Agent Gateway failures by routing from a structured component and stage to Gateway, profile, XAE, Target System, PLC runtime, verification, or exact-operation resources. Use for failed, stalled, unknown, mismatched, or divergent state observations without collapsing them into one runtime status.
---

# TwinCAT Diagnose

1. Read the failed tool result and identify:
   - `operationId`;
   - `component`;
   - `stage`;
   - stable error code;
   - whether side effects started.
2. Read only the matching object diagnostics:
   - `gateway` → `twincat-gateway://diagnostics`;
   - `profile` → capabilities or source manifest;
   - `xae` → profile XAE diagnostics/messages;
   - `target` → direct System Service diagnostics;
   - `plc` → the exact PLC runtime diagnostics;
   - `verification` → root operation and xUnit artifact.
3. Read the exact `twincat-operation://{operationId}` summary/events when the
   workflow crossed multiple components.
4. Read one exact operation artifact when structured diagnostics are
   insufficient.
5. Read `twincat-log://gateway/current` only for a gateway-wide, unknown, or
   undocumented failure that remains unexplained.

Keep these observations separate:

- XAE-observed TwinCAT system state;
- direct profile Target System Service state at port 10000;
- each PLC runtime state at its ADS port.

Do not derive a single aggregate mode. Compare source, raw `AdsState`, raw
`DeviceState`, AMS address, timestamp, freshness, and error.

Do not repeat a mutating operation as a diagnostic probe. Do not poll an
`OPERATOR_LOCKED` operation. Do not substitute another solution, target,
NetId, port, or automation route.

Use exact operation IDs. Never infer `last`, scan all logs, or fetch every raw
resource.

The target API may be temporarily unavailable during the breaking architecture
rework. Report that state and the remaining v1 consumer; do not silently use
deprecated tool names.
