# TwinCAT agent workflow

Use only the TwinCAT Agent Gateway MCP tools for TwinCAT operations.

## Development status

The TwinCAT Agent Gateway MCP server and its agent skills are under active
development. If an MCP operation or skill fails, behaves unexpectedly, or
returns an undocumented result, stop the affected workflow and notify the user
with the operation name and a concise error summary. Do not silently hide the
failure or substitute another TwinCAT automation path.

1. Call the required TwinCAT MCP operation.
2. If it returns `GATEWAY_NOT_RUNNING`, call `gateway_start` once.
3. Wait until the returned status reports `ready: true`.
4. Retry the original operation once.
5. For any other lifecycle error, stop and report it to the user.

Do not close the gateway when the task ends. Do not close, replace, or switch a
gateway that reports `GATEWAY_RUNNING_DIFFERENT_PROJECT`.

Inspect the gateway session log only when the MCP server returns an unknown or
undocumented error, or when its behavior or operation outcome cannot be
determined from the normal MCP response and compact diagnostics. First read
MCP resource `twincat-log://gateway/current`, then inspect only a bounded tail
of the exact file path it returns. Never infer the active path from
`logDirectory`, scan the log directory, or read every session file. For manual
operator reference only, the default log directory is
`%LOCALAPPDATA%\TwinCatAgentGateway\Logs`.

## Project configuration

The project-owned `twincat-gateway.json` selects the exact solution and safety
profile. Read MCP resource `twincat-doc://configuration` for the complete
option reference and examples before creating or changing this file. Read
`twincat-doc://setup` for installation and agent workflow instructions. The
desktop application's `Setup instructions` view uses the same installed
documentation.

Do not enable `allowActivation`, change `expectedTarget.amsNetId`, replace
TcUnit symbols/ADS port, or enable `allowDeleteExistingReport` without an
explicit user decision. The informational target name is not an identity;
AMS NetId is authoritative.
