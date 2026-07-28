---
name: twincat-test
description: Run the linked TwinCAT Agent Gateway TcUnit workflow on an explicitly allowed remote test profile. Use when PLC changes require rebuild, activation/restart, ADS completion evidence, and a fresh compact TcUnit result.
---

# TwinCAT Test

1. Call `twincat_status`. Confirm the profile, exact solution, activation
   permission, and remote AMS NetId.
2. Call `twincat_build` with `action: rebuild`. Stop on compile errors; build
   does not activate.
3. Call `twincat_activate` explicitly with the same profile and
   `waitForTcUnit: true`. This activates and restarts only the allow-listed
   remote target.
4. Read `testOperationId` from the completed activation result and call
   `twincat_get_test_results`.
5. Use the compact counts and failures to repair tests. Read the xUnit resource
   only when a failure lacks enough context.

Do not accept a timeout, stale report, missing completion symbol, or missing
report as a test result. Do not read arbitrary ADS symbols or write runtime
state.

The MVP profile designates exactly one TcUnit PLC and one report publisher.
Other PLCs may exist in the solution, but multi-PLC aggregation is not
supported.

Never substitute another solution, profile, target, ADS port, symbol, or
report path automatically.
