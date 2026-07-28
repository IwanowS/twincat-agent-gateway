# Installation and agent setup

## Install the applications

Run from the repository in a normal, non-elevated PowerShell:

```powershell
.\scripts\Install-Gateway.ps1
```

The installer builds the solution in `Release`, creates a deterministic
version directory below `%LOCALAPPDATA%\TwinCatAgentGateway\versions`, and
updates quiet command shims in `%LOCALAPPDATA%\TwinCatAgentGateway\bin`.
It installs only:

- `twincat-gateway`: the existing .NET Framework 4.8 x86 WPF host;
- `twincat-gateway-mcp`: the existing .NET 8 stdio adapter.

The repository CLI remains a development client and is not installed as a
global command. No process, shortcut, project, configuration, or runtime is
created or started by installation.

The PATH prompt defaults to `Y` and modifies only the user PATH. Use
`-NonInteractive` to accept that default without a prompt, or
`-NoPathUpdate` to keep PATH unchanged. `-InstallRoot` supports isolated test
or custom installations. Reinstalling identical artifacts is idempotent.

## Configure a project

Copy [the safe example](../examples/twincat-gateway.json) to
`twincat-gateway.json` in the project or Git root. Relative solution, log, and
TcUnit report paths are resolved from the configuration file's directory.

Discovery order is:

1. explicit `--config`;
2. MCP workspace roots;
3. process current directory;
4. nearest matching file upward, including but not crossing a Git root.

Different configurations found from multiple workspace roots fail with
`GATEWAY_CONFIG_AMBIGUOUS`. No `appsettings.Local.json` is discovered
implicitly; it remains accepted only through explicit `--config`.

## Register Codex MCP

After `twincat-gateway-mcp` resolves in a new PowerShell, run:

```powershell
.\scripts\Install-CodexMcp.ps1
```

The script uses `codex mcp list`, `get`, `remove`, and `add`; it does not edit
the user TOML directly. An alternative project-local configuration is
[provided here](../examples/codex/config.toml). Do not enable global and local
registrations of the same server simultaneously.

## Install skills separately

```powershell
.\scripts\Install-Skills.ps1 -Scope User
.\scripts\Install-Skills.ps1 -Scope Project -ProjectPath C:\repos\Machine
.\scripts\Install-Skills.ps1 -Destination C:\custom\skills
```

The main installer never installs skills. A project can also adopt the
[example agent workflow](../examples/project/AGENTS.md).

The short text printed by the installer and shown by the WPF
`Setup instructions` button has one canonical source:
[SETUP_INSTRUCTIONS.txt](../setup/SETUP_INSTRUCTIONS.txt).

## Uninstall

Exit the gateway and remove only the selected version directories and command
shims under `%LOCALAPPDATA%\TwinCatAgentGateway\versions` and `bin`. Remove the
user PATH entry if it is no longer needed. Do not delete `Logs`, project-local
`twincat-gateway.json`, TwinCAT projects, or runtime state as part of
application removal.
