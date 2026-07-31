---
name: twincat-test
description: Run fresh TcUnit verification through the target TwinCAT Agent Gateway workflow. Use when changed PLC code must be activated and tested, when tests must be repeated without code changes, or when TcUnit completion and xUnit evidence must be diagnosed without a redundant standalone build.
---

# TwinCAT Test

1. Accept or determine the intended profile.
2. If relevant sources are not already known, read
   `twincat-profile://{profile}/sources`.
3. Batch compatible implementation and test edits before a remote checkpoint.
4. Choose one workflow:
   - changed code: call `twincat_xae_activate` with
     `verification: tcunit`;
   - unchanged deployed code: call `twincat_target_start_restart` with
     `verification: tcunit`.
5. Read the root operation stage results:
   - compile;
   - deploy, when activation was requested;
   - Target transition;
   - verification.
6. Collect all bounded failures from the same fresh run before editing.
7. Repeat one coherent activation/test or restart/test checkpoint after the
   failure batch is ready.

Do not run a standalone build before activation merely to satisfy a Gateway
precondition. Native XAE activation performs its own compilation. Use a
standalone PLC build only when compile evidence is independently useful for
the development loop.

Project variant selection is manual in phase 1. Do not ask Gateway to switch
normal/test variants.

Require fresh evidence:

- a new Target Run/restart postcondition;
- completion belonging to the requested run;
- a new stable valid xUnit report;
- the configured zero-test policy.

Do not accept stale `TRUE`, timeout, missing symbols, missing/stale report, or
invalid XML as a test result.

`CAPABILITY_DISABLED` is a static profile denial. `OPERATOR_LOCKED` is a
temporary operator decision; report it and stop instead of polling.

The target API may be temporarily unavailable during the breaking architecture
rework. Report that state; never fall back to v1 tool names or another TwinCAT
automation path.
