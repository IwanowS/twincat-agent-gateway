---
name: twincat-build
description: Compile, rebuild, clean, and repair TwinCAT PLC projects through the target TwinCAT Agent Gateway XAE API. Use for source discovery, compile checks, PLC compiler errors or warnings, and external file-edit iterations that must synchronize with an exact profile solution.
---

# TwinCAT Build

1. Accept or determine the intended profile.
2. If the related source checkout is not already known, read
   `twincat-profile://{profile}/sources` once. Edit only the relevant returned
   project roots.
3. Edit PLC source files on disk with normal patch tools.
4. Call `twincat_xae_build` directly:
   - default to `scope: plc`;
   - use `rebuild` for a definitive compile check;
   - use `scope: solution` only when the task requires the complete TwinCAT
     solution build;
   - pass `changedPaths` only as bounded hints.
5. Read the compact result first. Collect the complete bounded diagnostic set,
   fix a coherent batch, and repeat only after that batch is ready.
6. Read the exact operation build/XAE/project-noise resource only when the
   compact result is insufficient.

Do not call a general status preflight. Gateway resolves the solution,
capability, locks, synchronization, and project identity from the profile.

Do not transition Target state because a PLC runtime is in Exception. Target
state is not a Gateway precondition for PLC compilation.

Build never performs Config, activation, restart, or tests. A build-only task
stays compile-only because that is the requested scope.

Treat:

- `CAPABILITY_DISABLED` as a static profile denial;
- `OPERATOR_LOCKED` as a temporary operator decision; report it and stop;
- XAE/solution mismatch as an XAE diagnostic problem;
- `unknown`, timeout, and missing BuildEvents/postconditions as unverified.

Do not inspect or rewrite a full `.tsproj`/`.tmc` when the exact operation
resource classifies it as expected generated noise.

The target API may be temporarily unavailable during the breaking architecture
rework. Report that state; never fall back to v1 tool names or another TwinCAT
automation path.
