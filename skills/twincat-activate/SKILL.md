---
name: twincat-activate
description: Explicitly activate and restart an allow-listed remote TwinCAT target through TwinCAT Agent Gateway. Use only when the user requests deployment or activation and the configured profile, solution, and AMS NetId must be verified before the state-changing operation.
---

# TwinCAT Activate

1. Call `twincat_status`.
2. Confirm the requested profile is activation-enabled and that its exact
   solution and AMS NetId match the intended remote target. The optional target
   name is informational only.
3. Activate only for an explicit runtime verification or debugging request;
   ordinary source validation ends after Build/Rebuild.
4. Treat remote activation as a scarce final validation checkpoint. Batch
   compatible source/test changes and local compile fixes before activation;
   do not activate after each build or individual fix.
5. Reuse a known successful Build or Rebuild for the same profile, solution,
   target, configuration, and platform when no relevant edit, Clean, failed
   build, `syncRequired`, or XAE reconnect followed it. Never rebuild merely
   because activation is next.
6. When the profile requires a recent build and the existing evidence is known
   to be invalid, build once. When its age is merely unknown, let
   `twincat_activate` enforce the fail-closed `RECENT_BUILD_REQUIRED` preflight;
   build once and retry once only when that code is returned. Never turn a
   build-only request into activation implicitly.
7. Call `twincat_activate` with the explicit profile. Use `waitForTcUnit:
   false` unless the requested workflow includes linked TcUnit execution.
8. Report the completed physical stages: configuration activation, TwinCAT
   restart, and verified runtime postcondition.

The normal reuse sequence is `twincat_status` then `twincat_activate`, with no
intervening build. Use only the exact gateway tool names documented here.

On failure, call `twincat_get_diagnostics` and report the exact failure stage.
If the runtime is in `Exception`, do not recover it automatically. Preserve
the diagnostic state and require an explicit user decision before calling
`twincat_recover_to_config`; after confirmed `Config`, build and activation
remain separate explicit operations. `unknown` runtime state remains unknown;
do not infer success from a timeout or from the COM call returning.

Never activate a local target, a disabled profile, or a different AMS NetId.
Never use ADS writes, `WriteControl`, PLC login, or an alternate runtime-control
path.
