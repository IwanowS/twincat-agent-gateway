---
name: twincat-activate
description: Explicitly activate and restart an allow-listed remote TwinCAT target through TwinCAT Agent Gateway. Use only when the user requests deployment or activation and the configured profile, solution, and AMS NetId must be verified before the state-changing operation.
---

# TwinCAT Activate

1. Call `twincat_status`.
2. Confirm the requested profile is activation-enabled and that its exact
   solution and AMS NetId match the intended remote target. The optional target
   name is informational only.
3. Require a successful recent build for the same profile. Never turn a build
   request into activation implicitly.
4. Call `twincat_activate` with the explicit profile. Use `waitForTcUnit:
   false` unless the requested workflow includes linked TcUnit execution.
5. Report the completed physical stages: configuration activation, TwinCAT
   restart, and verified runtime postcondition.

On failure, call `twincat_get_diagnostics` and report the exact failure stage.
If the runtime is in `Exception`, do not recover it automatically. Preserve
the diagnostic state and require an explicit user decision before calling
`twincat_recover_to_config`; after confirmed `Config`, build and activation
remain separate explicit operations. `unknown` runtime state remains unknown;
do not infer success from a timeout or from the COM call returning.

Never activate a local target, a disabled profile, or a different AMS NetId.
Never use ADS writes, `WriteControl`, PLC login, or an alternate runtime-control
path.
