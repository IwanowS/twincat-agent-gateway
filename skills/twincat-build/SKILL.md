---
name: twincat-build
description: Build, rebuild, clean, and repair TwinCAT PLC projects through TwinCAT Agent Gateway. Use for compile checks, PLC build errors or warnings, and normal external source-edit iterations against an open or gateway-launched XAE solution.
---

# TwinCAT Build

1. Call `twincat_status` and confirm that the selected profile, exact solution,
   XAE connection, and target identity are the intended ones.
2. Edit PLC source files with normal patch tools. Do not edit through COM or
   Automation Interface.
3. Call `twincat_build` with the profile and the requested action. Prefer
   `rebuild` for a definitive compile check. Pass explicit
   configuration/platform only when the task requires them.
4. Read the compact result first. Fix the reported source diagnostics and
   repeat the build.
5. Read the referenced build log only for an infrastructure failure, an
   inconsistent result, or diagnostics that are insufficient to act on.

Build never activates TwinCAT. Keep activation as a separate explicit task.
If the compact result reports `BUILD_BLOCKED_BY_RUNTIME_EXCEPTION`, treat it
as a diagnostic stop: do not retry and do not recover the runtime
automatically. Report that the previous PLC artifacts are being preserved and
require an explicit user decision before calling
`twincat_recover_to_config`.

Gateway owns the workspace while attached: it fingerprints project sources,
discards conflicting dirty XAE documents, and reloads changed files before the
operation. Added or removed PLC source files are unsupported by the MVP and
must be surfaced instead of worked around.

The verified workspace includes the selected `.tsproj` directory when the
solution references that TwinCAT project outside its own directory.

Trust `ExpectedReorderOnly` for `.tsproj` noise. Do not load the full project
file, rewrite it, or revert it merely to restore ordering. Inspect only the
focused diff resource when the classifier reports another result.

Use the CLI only when MCP is unavailable; preserve the same compact-first
workflow with `twincat-gateway build`.
