# TwinCAT agent workflow

Use only TwinCAT Agent Gateway MCP tools for XAE/Target operations. Do not
substitute raw COM, ADS, PowerShell, or another automation path after a
Gateway denial.

## Profile and sources

The project-owned `twincat-gateway.json` identifies the solution, optional
remote target, and maximum capabilities.

- Pass the intended profile to the requested operation.
- Do not read configuration flags or call a general status preflight before
  every operation; Gateway validates identities, capabilities, and operator
  locks.
- If the source checkout associated with the profile is not already known,
  read `twincat-profile://{profile}/sources` once and edit only the relevant
  returned project roots.
- An explicit user prohibition in the current conversation overrides an
  enabled profile capability.

## Gateway lifecycle

Call the intended operation directly.

1. If it returns `GATEWAY_NOT_RUNNING`, call `gateway_start` once.
2. Retry the original operation once.
3. For another lifecycle error, stop and report the operation name and concise
   error. Do not silently retry or switch project/Gateway.

Do not shut down Gateway when the task ends unless requested by the workflow.

## Development workflows

- **Code only:** edit files; do not call Gateway.
- **Compile/fix errors:** edit a coherent batch, then call
  `twincat_xae_build` with PLC scope. Build does not change Target state.
- **Activate changed code:** call `twincat_xae_activate`; do not run a
  standalone build first because activation performs its own compilation.
- **Activate and test:** call `twincat_xae_activate` with
  `verification=tcunit`.
- **Repeat tests without code changes:** call
  `twincat_target_start_restart` with `verification=tcunit`.
- **Config:** call `twincat_target_config`; Config is a normal Target
  transition, not recovery.

Batch compatible edits and collect the complete bounded compile/test failure
set before another mutating checkpoint.

## Denials and diagnostics

- `CAPABILITY_DISABLED`: report the static profile denial.
- `OPERATOR_LOCKED`: report the temporary UI lock and stop; do not poll.
- `XAE_CLOSE_CONSENT_REQUIRED`: an attached XAE needs exact-PID operator
  consent.
- Identity mismatch: read only the diagnostics for the reported component.

Diagnostic order:

1. compact operation result;
2. object-specific XAE/Target/PLC diagnostics;
3. exact `twincat-operation://{operationId}/...` artifact;
4. `twincat-log://gateway/current` only for gateway-wide or unknown behavior.

Do not infer one aggregate runtime state. Keep XAE-observed TwinCAT system,
direct Target System Service, and each PLC runtime observation separate.

## Documentation

Use:

- `twincat-doc://configuration` for profile options;
- `twincat-doc://mcp` for the exact tool/resource contract;
- `twincat-doc://setup` for installation/connection.

The Gateway and skills are under active development. Report undocumented
results instead of hiding or working around them.
