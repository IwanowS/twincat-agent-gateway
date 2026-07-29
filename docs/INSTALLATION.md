# Installation and agent setup

## Install the applications

Run from the repository in a normal, non-elevated PowerShell:

```powershell
.\scripts\Install-Gateway.ps1
```

All public installation scripts support standard PowerShell help, explicit
full help, and verbose progress:

```powershell
Get-Help .\scripts\Install-Gateway.ps1
Get-Help .\scripts\Install-Gateway.ps1 -Full
Get-Help .\scripts\Install-Gateway.ps1 -Examples
.\scripts\Install-Gateway.ps1 -?
.\scripts\Install-Gateway.ps1 -Help
.\scripts\Install-Gateway.ps1 -Verbose
```

The installer builds the solution in `Release`, installs the applications at
the stable `%LOCALAPPDATA%\TwinCatAgentGateway\app` path, and updates quiet
command shims in `%LOCALAPPDATA%\TwinCatAgentGateway\bin`.
It installs only:

- `twincat-gateway`: the existing .NET Framework 4.8 x86 WPF host;
- `twincat-gateway-mcp`: the existing .NET 8 stdio adapter.

The repository CLI remains a development client and is not installed as a
global command. No process, shortcut, project, configuration, or runtime is
created or started by installation.

The PATH prompt defaults to `Y` and modifies only the user PATH. Use
`-NonInteractive` to accept that default without a prompt, or
`-NoPathUpdate` to keep PATH unchanged. `-InstallRoot` supports isolated test
or custom installations.

When an application or legacy `versions` directory already exists, interactive
installation asks before replacing it. Non-interactive replacement requires
`-Force`; without it the installer fails before changing files. Exit the
installed desktop gateway and MCP adapter before replacement. Configuration
files and logs stored outside `app` are preserved.

When only the desktop gateway changed and the MCP contract/adapter did not,
replace just the gateway:

```powershell
.\scripts\Install-Gateway.ps1 -GatewayOnly -NonInteractive -Force
```

This mode requires an existing full installation, builds only the desktop
project, closes/replaces only `app\gateway`, preserves `app\mcp` and the MCP
command shim byte-for-byte, and does not change PATH. A running MCP adapter may
remain connected, so Codex does not need to be restarted. Do not use this mode
after MCP or shared IPC contract changes; perform a full replacement instead.

## Configure a project

Copy [the safe example](../examples/twincat-gateway.json) to
`twincat-gateway.json` in the project or Git root. Relative solution, log, and
TcUnit report paths are resolved from the configuration file's directory.
See the [complete configuration reference](CONFIGURATION.md) for every option,
default, constraint, and a full example. The same reference is installed with
the desktop application and shown by `Setup instructions`.

Discovery order is:

1. explicit `--config`;
2. MCP workspace roots;
3. process current directory;
4. nearest matching file upward, including but not crossing a Git root.

Different configurations found from multiple workspace roots fail with
`GATEWAY_CONFIG_AMBIGUOUS`. No `appsettings.Local.json` is discovered
implicitly; it remains accepted only through explicit `--config`.

A manual `twincat-gateway` launch with no discovered configuration opens a
setup-only window that displays the product version and embedded instructions.
It does not start the gateway host, open a Named Pipe, or block an agent from
starting a configured gateway. An explicit missing `--config` and an agent
launch without configuration still fail closed.

## Register Codex MCP

After `twincat-gateway-mcp` resolves in a new PowerShell, run:

```powershell
.\scripts\Install-CodexMcp.ps1
```

The script uses `codex mcp list`, `get`, `remove`, and `add`; it does not edit
the user TOML directly. An alternative project-local configuration is
[provided here](../examples/codex/config.toml). Do not enable global and local
registrations of the same server simultaneously.

## MCP console help

The stdio adapter provides generated console help without starting the MCP
server:

```powershell
twincat-gateway-mcp --help
twincat-gateway-mcp -h
twincat-gateway-mcp --version
```

Help, defaults, parsing, and future subcommand help are generated from the same
`System.CommandLine` command and option definitions. The current MVP has no
console subcommands; its root options are `--config`, `--pipe`, and
`--gateway-command`. In normal no-argument server mode, stdout remains reserved
for the MCP protocol.

The MCP server also exposes the installed canonical documentation without
starting or connecting to the desktop gateway:

```text
twincat-doc://setup
twincat-doc://configuration
```

Use the configuration resource before creating or changing a project-owned
`twincat-gateway.json`.

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

Exit the gateway and remove only the `app` directory and command shims under
`%LOCALAPPDATA%\TwinCatAgentGateway`. Remove the user PATH entry if it is no
longer needed. Do not delete `Logs`, project-local `twincat-gateway.json`,
TwinCAT projects, or runtime state as part of application removal.
