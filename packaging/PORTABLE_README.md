# TwinCAT Agent Gateway portable package

## Prerequisites

- Windows 10/11;
- TwinCAT 3.1.4024.17 with Visual Studio 2019 or compatible XAE Shell;
- .NET Framework 4.8;
- .NET 8 Desktop Runtime x64.

## First run

1. Copy `twincat-gateway.example.json` to the project as
   `twincat-gateway.json`.
2. Set the relative or absolute `solution` path. Keep `allowActivation` false until an
   explicit remote target profile is configured and reviewed.
3. Start `desktop\twincat-gateway.exe`. The UI reports configuration,
   XAE discovery, and connection failures.
4. Start agents through `mcp\twincat-gateway-mcp.exe`.

The desktop gateway, CLI, and MCP adapter must run as the same interactive
Windows user. XAE must also run in the same session and at the same integrity
level.

Logs default to
`%LOCALAPPDATA%\TwinCatAgentGateway\Logs`. Removing the extracted package does
not remove configuration, logs, TwinCAT projects, or runtime state. Delete logs
separately only after reviewing them.

See `docs\TROUBLESHOOTING.md` for failure codes and recovery steps.
