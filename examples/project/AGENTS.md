# TwinCAT agent workflow

Use only the TwinCAT Agent Gateway MCP tools for TwinCAT operations.

1. Call the required TwinCAT MCP operation.
2. If it returns `GATEWAY_NOT_RUNNING`, call `gateway_start` once.
3. Wait until the returned status reports `ready: true`.
4. Retry the original operation once.
5. For any other lifecycle error, stop and report it to the user.

Do not close the gateway when the task ends. Do not close, replace, or switch a
gateway that reports `GATEWAY_RUNNING_DIFFERENT_PROJECT`.

## Project configuration

The project-owned `twincat-gateway.json` selects the exact solution and safety
profile. The complete option reference and examples are available from the
desktop application's `Setup instructions` view and from
`docs/CONFIGURATION.md` in the TwinCAT Agent Gateway repository.

Do not enable `allowActivation`, change `expectedTarget.amsNetId`, replace
TcUnit symbols/ADS port, or enable `allowDeleteExistingReport` without an
explicit user decision. The informational target name is not an identity;
AMS NetId is authoritative.
