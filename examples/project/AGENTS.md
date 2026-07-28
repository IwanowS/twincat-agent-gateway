# TwinCAT agent workflow

Use only the TwinCAT Agent Gateway MCP tools for TwinCAT operations.

1. Call the required TwinCAT MCP operation.
2. If it returns `GATEWAY_NOT_RUNNING`, call `gateway_start` once.
3. Wait until the returned status reports `ready: true`.
4. Retry the original operation once.
5. For any other lifecycle error, stop and report it to the user.

Do not close the gateway when the task ends. Do not close, replace, or switch a
gateway that reports `GATEWAY_RUNNING_DIFFERENT_PROJECT`.
