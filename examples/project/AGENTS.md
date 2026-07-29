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

Gateway logs are stored in the configured `logDirectory`; when it is omitted,
the default path is `%LOCALAPPDATA%\TwinCatAgentGateway\Logs`. Inspect these
logs only when the TwinCAT Agent Gateway MCP server returns an unknown or
undocumented error, or when its behavior or operation outcome cannot be
determined from the normal MCP response and compact diagnostics. Do not read
them for documented errors or normal successful workflows.

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
