# Gateway configuration reference

TwinCAT Agent Gateway uses a project-owned JSON file named
`twincat-gateway.json`. The file selects the exact TwinCAT solution, controls
whether XAE may be launched, and defines the only target on which activation
and TcUnit collection may operate.

JSON property names are case-insensitive. Comments and trailing commas are
accepted. Saved files use the camel-case names shown below.

## Minimal safe configuration

This configuration supports connection and build while keeping activation and
TcUnit disabled:

```json
{
  "schemaVersion": 1,
  "profiles": [
    {
      "name": "default",
      "solution": "Machine.sln",
      "allowActivation": false
    }
  ]
}
```

Place it beside `Machine.sln` or at the project/repository root. Relative
`solution`, `logDirectory`, and `tcUnit.reportPath` values are resolved from
the directory containing `twincat-gateway.json`.

## Complete example

The following example shows every configuration property. Activation remains
disabled until the operator deliberately changes `allowActivation`.

```json
{
  "schemaVersion": 1,
  "pipeName": "TwinCatAgentGateway",
  "defaultProfile": "default",
  "logDirectory": ".gateway-logs",
  "logMinimumLevel": "information",
  "logFileSizeLimitBytes": 1048576,
  "logRetainedFileCountLimit": 10,
  "logRetentionDays": 14,
  "ui": {
    "mode": "auto"
  },
  "agentProcessControl": {
    "allowStart": true,
    "allowShutdown": false
  },
  "runtimeMonitoring": {
    "pollIntervalMilliseconds": 1000,
    "readTimeoutMilliseconds": 500
  },
  "profiles": [
    {
      "name": "default",
      "solution": "Machine.sln",
      "allowXaeLaunch": true,
      "xaeProgId": null,
      "allowActivation": false,
      "expectedTarget": {
        "name": "WIN-T077ADA",
        "amsNetId": "192.168.3.31.1.1"
      },
      "configuration": null,
      "platform": null,
      "assumeAttachedXaeSynchronized": true,
      "externalChangePolicy": "reloadModified",
      "allowForceSynchronization": false,
      "allowDirtyDocumentDiscard": false,
      "autoSynchronizeBeforeOperation": true,
      "requireRecentSuccessfulBuild": true,
      "recentBuildMaxAgeSeconds": 600,
      "autoWaitForTcUnit": false,
      "tcUnit": {
        "adsPort": 851,
        "finishedSymbol": "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished",
        "suiteCountSymbol": "GVL_TcUnit.NumberOfInitializedTestSuites",
        "reportPath": "reports\\tcunit.xml",
        "allowDeleteExistingReport": false,
        "completionTimeoutSeconds": 120,
        "zeroTests": "fail"
      }
    }
  ]
}
```

## Discovery

Configuration discovery uses this order:

1. explicit `--config <path>`;
2. workspace roots supplied by the MCP client;
3. the process current directory;
4. the nearest `twincat-gateway.json` upward, including but not crossing a Git
   root; outside Git, up to the filesystem root.

Different files found from multiple workspace roots produce
`GATEWAY_CONFIG_AMBIGUOUS`. An explicit missing file and an agent launch with
no file produce `GATEWAY_CONFIG_NOT_FOUND`. A manual launch with no discovered
file opens the setup-only UI and does not start the gateway or Named Pipe.

`appsettings.Local.json` is never discovered implicitly. It is accepted only
when passed through `--config`.

## Top-level options

| Property | Type and default | Meaning and constraints |
|---|---|---|
| `schemaVersion` | integer, `1` | Required schema identity. Only version 1 is accepted. |
| `pipeName` | string, `"TwinCatAgentGateway"` | Per-user Named Pipe name. Must be non-empty and contain no `/` or `\`. |
| `defaultProfile` | string or `null`, `null` | Profile selected when the caller does not specify one. Optional for exactly one profile and required for multiple profiles. Matching is case-insensitive. |
| `logDirectory` | path or `null`, `null` | Structured and raw log root. Relative paths are resolved from the config directory. When omitted, `%LOCALAPPDATA%\TwinCatAgentGateway\Logs` is used. |
| `logMinimumLevel` | `verbose`, `debug`, `information`, `warning`, `error`, or `fatal`; `information` | Minimum severity written to the gateway session log. |
| `logFileSizeLimitBytes` | integer, `1048576` | Maximum size of one gateway session segment before rollover. Valid range: 65536 through 1073741824 bytes. |
| `logRetainedFileCountLimit` | integer, `10` | Maximum number of segments retained for one application run. Valid range: 1 through 1000. |
| `logRetentionDays` | integer, `14` | Age retention for previous gateway session files and operation-log directories. Valid range: 1 through 3650 days. |
| `ui` | object, default object | UI configuration. Must not be `null`. |
| `agentProcessControl` | object, default object | Agent lifecycle policy. Must not be `null`. |
| `runtimeMonitoring` | object, default object | Read-only ADS runtime polling. Must not be `null`. |
| `profiles` | array, empty by default | One or more unique project profiles are required for a configured gateway. |

Each application run writes compact NDJSON to a separate file named like
`gateway-20260729T063245123Z-p1234.ndjson`. Size rollover adds `_001`, `_002`,
and later segments. Age retention recognizes only these strict session names
and the legacy `gateway.ndjson`; it does not remove the active session or
unrelated files.

Agents must read MCP resource `twincat-log://gateway/current` to discover the
exact segment currently open after any rollover. They must not infer that path
from `logDirectory` or scan all session files. The default directory above is
reference information for manual operator diagnostics.

## `ui` options

| Property | Type and default | Meaning |
|---|---|---|
| `mode` | `auto`, `window`, or `tray`; `auto` | `auto` shows a window for manual launch and starts in the tray for agent launch. An explicit command-line `--ui-mode` overrides this value. |

The setup-only UI always shows a window because it is not a configured gateway
process.

## `agentProcessControl` options

| Property | Type and default | Meaning |
|---|---|---|
| `allowStart` | Boolean, `true` | Permits the MCP `gateway_start` tool to launch the desktop gateway for this exact project. |
| `allowShutdown` | Boolean, `false` | Permits the destructive MCP `gateway_shutdown` tool to close this desktop gateway after its IPC response has been written. It never closes a user-owned XAE instance. |

Neither option permits an agent to select another solution or target.

## `runtimeMonitoring` options

The desktop gateway polls the verified System Service and the PLC ADS ports
discovered from the exact selected `.tsproj`. Unchanged observations remain
inside the gateway and do not create events or consume model tokens.

| Property | Type and default | Meaning |
|---|---|---|
| `pollIntervalMilliseconds` | integer, `1000` | Delay between completed polling rounds. Valid range: 100 through 60000 milliseconds. |
| `readTimeoutMilliseconds` | integer, `500` | Upper bound for each ADS state read. Valid range: 100 through 10000 milliseconds. |

## Project profile options

| Property | Type and default | Meaning and constraints |
|---|---|---|
| `name` | string, empty by default | Required profile name. Names must be unique, case-insensitively. |
| `solution` | path, empty by default | Required `.sln` path. Relative paths are resolved from the config directory. XAE attachment uses the normalized exact solution path. |
| `allowXaeLaunch` | Boolean, `true` | Allows the configured gateway to launch a compatible XAE when the exact solution is not already open. It does not allow activation. |
| `xaeProgId` | string or `null`, `null` | Optional exact DTE ProgID. `null` enables the gateway's compatible XAE candidate discovery. Empty or whitespace values are invalid. |
| `allowActivation` | Boolean, `false` | Enables the explicit activation operation for this profile. Build never performs activation. |
| `expectedTarget` | object or `null`, `null` | Exact target identity. Required when `allowActivation` is true. |
| `configuration` | string or `null`, `null` | Optional XAE solution configuration name used by build. `null` keeps the verified active selection. Empty or whitespace values are invalid. |
| `platform` | string or `null`, `null` | Optional XAE solution platform name used by build. `null` keeps the verified active selection. Empty or whitespace values are invalid. |
| `assumeAttachedXaeSynchronized` | Boolean, `true` | When attaching to an already open exact XAE solution with no dirty project documents, accepts the current disk graph as the initial baseline without reloading XAE. The operator is responsible for ensuring that the XAE in-memory project model is not stale. `false` requires an explicit synchronization before Build/Rebuild/Clean. |
| `externalChangePolicy` | `reloadAll`, `reloadModified`, or `error`; `reloadModified` | Reaction to non-generated disk changes found by the authoritative project-graph fingerprint scan. `reloadModified` reloads only modified `.TcPOU`/`.TcGVL`/`.TcDUT` and rejects graph or metadata changes. `reloadAll` permits them and reloads the selected TwinCAT project. `error` rejects every non-noise difference. |
| `allowForceSynchronization` | Boolean, `false` | Permits the destructive MCP `twincat_sync` operation. The desktop UI may always request synchronization for the selected profile. |
| `allowDirtyDocumentDiscard` | Boolean, `false` | Allows an explicit build/sync request with `discardDirtyDocuments=true` to close dirty project documents without saving. It never enables automatic saving or automatic discard. |
| `autoSynchronizeBeforeOperation` | Boolean, `true` | Runs the authoritative fingerprint scan and policy-controlled typed reload before Build/Rebuild/Clean and activation. It never saves or automatically discards dirty XAE documents. Set to `false` only when the operator deliberately accepts responsibility for keeping the XAE project model synchronized with disk. |
| `requireRecentSuccessfulBuild` | Boolean, `true` | Requires a recent successful build before activation. |
| `recentBuildMaxAgeSeconds` | integer, `600` | Maximum age of that build. Must be positive when the recent-build requirement is enabled. |
| `autoWaitForTcUnit` | Boolean, `false` | Links activation to TcUnit completion and report collection. Requires `tcUnit`. |
| `tcUnit` | object or `null`, `null` | Narrow read-only ADS completion and fresh xUnit report settings. |

## `expectedTarget` options

| Property | Type and default | Meaning and constraints |
|---|---|---|
| `name` | string or `null`, `null` | Informational target label for UI and logs. It is not used for identity matching. |
| `amsNetId` | string or `null`, `null` | Exact six-part AMS NetId. Required for activation; every part must be a canonical byte value from 0 to 255. |

The AMS NetId is the safety identity. The gateway never substitutes another
target automatically.

## `tcUnit` options

| Property | Type and default | Meaning and constraints |
|---|---|---|
| `adsPort` | integer, `851` | PLC ADS port used only for the fixed completion reads. Valid range: 1 through 65535. |
| `finishedSymbol` | string, `"GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished"` | Fixed Boolean completion symbol. Must be non-empty. |
| `suiteCountSymbol` | string, `"GVL_TcUnit.NumberOfInitializedTestSuites"` | Fixed initialized-suite count symbol. Must be non-empty. |
| `reportPath` | path, empty by default | Required fresh xUnit report path. Relative paths are resolved from the config directory. |
| `allowDeleteExistingReport` | Boolean, `false` | Allows deletion only of the configured old report before a linked run. A filesystem root is rejected. |
| `completionTimeoutSeconds` | integer, `120` | Upper bound for completion polling. Must be positive; timeout is not success evidence. |
| `zeroTests` | `fail`, `warn`, or `allow`; `fail` | Policy when a fresh valid report contains zero tests. |

ADS remains read-only and limited to `finishedSymbol` and `suiteCountSymbol`.
Pass/fail comes from the fresh xUnit report, not from XAE/VSTest exit code.

## Safety rules

- Keep `allowActivation` false until the exact remote target is verified.
- Activation is always a separate explicit operation and never follows build
  implicitly.
- `assumeAttachedXaeSynchronized=true` transfers responsibility for the initial
  XAE/disk agreement to the operator. Set it to `false` when an explicit typed
  reload is required before the first operation after gateway startup.
- Keep `autoSynchronizeBeforeOperation=true` when agents edit PLC project files
  externally. Disabling it means Build and activation use the current XAE
  project model without automatic pre-action change detection or typed reload.
- Keep `allowForceSynchronization` and `allowDirtyDocumentDiscard` false
  unless the operator deliberately accepts those independent capabilities.
- The gateway never saves an XAE editor buffer. Dirty documents fail with
  `DIRTY_XAE_DOCUMENT` unless discard was explicitly requested and allowed.
- Local activation, restart, runtime state changes, ADS writes, and arbitrary
  symbol access are outside the MVP.
- The agent may select a configured profile but may not supply a different
  solution, AMS NetId, ADS port, or symbol path.
- Do not put secrets in this file. Its normalized path and selected profile may
  appear in local status and logs, but the file contents are not returned by
  default.
