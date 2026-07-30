---
name: twincat-test
description: Run the linked TwinCAT Agent Gateway TcUnit workflow on an explicitly allowed remote test profile. Use when PLC runtime behavior needs activation/restart, ADS completion evidence, and a fresh compact TcUnit result, reusing an already verified current build when possible.
---

# TwinCAT Test

1. Call `twincat_status`. Confirm the profile, exact solution, activation
   permission, and remote AMS NetId.
2. Batch compatible implementation and test changes first. Use focused local
   checks/builds as needed, but reserve remote activation and TcUnit for one
   coherent checkpoint, preferably near the end of the task.
3. Reuse a known successful Build or Rebuild for the same profile, solution,
   target, configuration, and platform when no relevant edit, Clean, failed
   build, `syncRequired`, or XAE reconnect followed it. Otherwise call
   `twincat_build` once with `action: rebuild`. Stop on compile errors; build
   does not activate.
4. Call `twincat_activate` explicitly with the same profile and
   `waitForTcUnit: true`. This activates and restarts only the allow-listed
   remote target.
5. Read `testOperationId` from the completed activation result and call
   `twincat_get_test_results`.
6. Collect all bounded failures from that run before editing. Fix the coherent
   failure batch, build once after the fixes, and perform one repeat
   activation/test. Do not reactivate separately for each failed test.

Do not repeat Build/Rebuild before activation when step 3 reused or completed a
valid build and no files changed afterward. If repairing a failure changes PLC
sources, the previous build no longer validates that new source state.
The reuse sequence is `twincat_status`, `twincat_activate` with
`waitForTcUnit: true`, then `twincat_get_test_results`.

Do not leave required runtime verification undone at task completion. Run an
earlier checkpoint only when runtime evidence is necessary to decide the next
implementation step; otherwise prefer the final batched checkpoint.

Do not accept a timeout, stale report, missing completion symbol, or missing
report as a test result. Do not read arbitrary ADS symbols or write runtime
state.

The MVP profile designates exactly one TcUnit PLC and one report publisher.
Other PLCs may exist in the solution, but multi-PLC aggregation is not
supported.

Never substitute another solution, profile, target, ADS port, symbol, or
report path automatically.
