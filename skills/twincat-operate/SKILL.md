---
name: twincat-operate
description: Operate an exact TwinCAT Agent Gateway profile through XAE lifecycle, synchronization, activation, Target Config, Target start/restart, and ownership-aware XAE close actions. Use when the user wants deployment, runtime transitions, XAE lifecycle control, or an operator-lock/capability-aware state-changing workflow that is not primarily compile repair or TcUnit analysis.
---

# TwinCAT Operate

1. Accept or determine the intended profile.
2. Call the requested object operation directly:
   - ensure XAE: `twincat_xae_open`;
   - synchronize: `twincat_xae_sync`;
   - activate: `twincat_xae_activate`;
   - Target Config: `twincat_target_config`;
   - Target start/restart: `twincat_target_start_restart`;
   - close XAE: `twincat_xae_close`.
3. Let Gateway resolve the solution, target, capability, ownership consent,
   operator locks, and postconditions. Do not perform a general status
   preflight.
4. Read object-specific diagnostics only when the operation fails.

Profile capability is standing authorization for its exact configured
resources. An explicit user prohibition in the current conversation still
wins.

Activation performs its own XAE compilation. Do not run a standalone build
first unless the user separately requested compile evidence.

`twincat_target_config` is a standard operation from any Target state. Do not
look for or call a recovery-specific tool.

`twincat_target_start_restart` is intentionally non-idempotent:

- Config/Stopped starts TwinCAT;
- Run restarts TwinCAT.

XAE close requires configured capability and exact-PID session consent.
Gateway-launched XAE defaults consent on; attached user XAE defaults it off.
Never force-kill XAE or bypass a consent denial.

Treat:

- `CAPABILITY_DISABLED` as static;
- `OPERATOR_LOCKED` as temporary and non-pollable;
- `XAE_CLOSE_CONSENT_REQUIRED` as an operator UI decision;
- timeout/unknown/missing postcondition as unverified.

The target API may be temporarily unavailable during the breaking architecture
rework. Report that state; never fall back to v1 tool names or another TwinCAT
automation path.
