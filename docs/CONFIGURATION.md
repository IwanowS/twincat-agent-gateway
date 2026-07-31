# Gateway configuration reference — target schema v2

> **Status:** approved target contract for the architecture rework. The
> current implementation still uses schema v1, documented in
> [`CONFIGURATION_V1_BASELINE.md`](CONFIGURATION_V1_BASELINE.md).
> Schema v2 is intentionally incompatible with v1.

`twincat-gateway.json` identifies project resources and defines the maximum
capabilities available to the agent. Gateway, UI locks, and the current user
conversation may reduce those capabilities; they cannot expand them.

JSON property names are case-insensitive. Comments and trailing commas may be
accepted by the implementation. Saved examples use the camel-case names below.

## 1. Complete target example

```json
{
  "schemaVersion": 2,
  "defaultProfile": "default",
  "gateway": {
    "pipeName": "TwinCatAgentGateway",
    "processControl": {
      "allowStart": true,
      "allowShutdown": false
    },
    "logging": {
      "directory": ".gateway-logs",
      "minimumLevel": "information",
      "fileSizeLimitBytes": 1048576,
      "retainedFileCountLimit": 10,
      "retentionDays": 14
    }
  },
  "ui": {
    "mode": "auto"
  },
  "profiles": [
    {
      "name": "default",
      "xae": {
        "solution": "Machine.sln",
        "progId": null,
        "configuration": null,
        "platform": null,
        "workspace": {
          "assumeAttachedSynchronized": true,
          "externalChangePolicy": "reloadModified",
          "autoSynchronizeBeforeOperation": true
        },
        "capabilities": {
          "launch": true,
          "close": false,
          "synchronize": true,
          "discardDirtyDocuments": false,
          "build": true,
          "activate": false
        }
      },
      "target": {
        "name": "WIN-T077ADA",
        "amsNetId": "192.168.3.31.1.1",
        "monitoring": {
          "pollIntervalMilliseconds": 1000,
          "readTimeoutMilliseconds": 500
        },
        "capabilities": {
          "config": false,
          "startRestart": false,
          "tcUnitVerification": false
        },
        "tcUnit": {
          "runtimeId": "plc-851",
          "adsPort": 851,
          "finishedSymbol": "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished",
          "suiteCountSymbol": "GVL_TcUnit.NumberOfInitializedTestSuites",
          "reportPath": "reports\\tcunit.xml",
          "allowDeleteExistingReport": false,
          "completionTimeoutSeconds": 120,
          "zeroTests": "fail"
        }
      }
    }
  ]
}
```

Relative paths are resolved from the directory containing
`twincat-gateway.json`.

## 2. Discovery

Configuration discovery order remains:

1. explicit `--config <path>`;
2. workspace roots supplied by the MCP client;
3. process current directory;
4. nearest `twincat-gateway.json` upward, including but not crossing a Git
   root; outside Git, up to the filesystem root.

Different files found from multiple workspace roots produce
`GATEWAY_CONFIG_AMBIGUOUS`. Missing explicit config or agent launch without a
config produces `GATEWAY_CONFIG_NOT_FOUND`.

Manual launch without a discovered config may open setup-only UI. It does not
start the configured Gateway host or publish IPC.

## 3. Top-level options

| Property | Type/default | Meaning |
|---|---|---|
| `schemaVersion` | integer, required `2` | Breaking configuration contract. Schema v1 is not accepted by the v2 implementation. |
| `defaultProfile` | string or `null` | Default profile. Required when more than one profile exists. |
| `gateway` | object, required | Process, IPC, and logging configuration. |
| `ui` | object, default object | Desktop presentation settings. |
| `profiles` | non-empty array | Named solution/target profiles. Names are unique case-insensitively. |

## 4. `gateway`

### 4.1 Core

| Property | Type/default | Meaning |
|---|---|---|
| `pipeName` | string, `"TwinCatAgentGateway"` | Per-user Named Pipe. Must be non-empty and contain no slash. |
| `processControl` | object | Agent-controlled Gateway lifecycle capabilities. |
| `logging` | object | Structured session and operation logging. |

### 4.2 `gateway.processControl`

| Property | Type/default | Meaning |
|---|---|---|
| `allowStart` | Boolean, `true` | Allows `gateway_start` for this exact config. |
| `allowShutdown` | Boolean, `false` | Allows `gateway_shutdown`. It does not imply permission to close XAE. |

### 4.3 `gateway.logging`

| Property | Type/default | Meaning |
|---|---|---|
| `directory` | path or `null` | Session/operation log root. `null` uses the per-user application directory. |
| `minimumLevel` | enum, `information` | `verbose`, `debug`, `information`, `warning`, `error`, or `fatal`. |
| `fileSizeLimitBytes` | integer, `1048576` | Size rollover limit for one session segment. |
| `retainedFileCountLimit` | integer, `10` | Segment limit for one Gateway run. |
| `retentionDays` | integer, `14` | Retention for closed session files and operation directories. |

The exact active log is discovered through
`twincat-log://gateway/current`. Agents do not infer it from `directory`.

## 5. `ui`

| Property | Type/default | Meaning |
|---|---|---|
| `mode` | `auto`, `window`, or `tray`; `auto` | Manual `auto` launch shows the window; agent launch starts in tray. Command-line override has priority. |

Configuration does not persist operator locks. Locks are session state and
reset when Gateway restarts.

UI renders:

- a compact Overview;
- a separate operator-lock panel;
- a separate read-only configuration-details view containing every effective
  option, its explicit/default origin, and a description. Boolean
  configuration values use disabled/read-only checkboxes; only session locks
  and PID-scoped consent are interactive controls.

## 6. Profiles

Each profile identifies one solution and optionally one Target System.

| Property | Type/default | Meaning |
|---|---|---|
| `name` | non-empty string | Stable profile identity passed by the agent. |
| `xae` | object, required | Solution identity, XAE workspace behavior, and XAE capabilities. |
| `target` | object or `null` | Remote Target identity, monitoring, transitions, and verification. May be omitted for build-only profiles. |

The agent normally passes only `profile`. It does not repeat solution path,
AMS NetId, ADS ports, or capability flags.

## 7. `profile.xae`

### 7.1 Identity and selection

| Property | Type/default | Meaning |
|---|---|---|
| `solution` | path, required | Exact `.sln`. Attachment matches normalized absolute `Solution.FullName`. |
| `progId` | string or `null` | Optional exact DTE ProgID. `null` uses compatible XAE discovery. |
| `configuration` | string or `null` | Optional solution configuration. `null` keeps and reports the active selection. |
| `platform` | string or `null` | Optional solution platform. `null` keeps and reports the active selection. |

Project variant is intentionally absent from schema v2 phase 1. The operator
selects it when preparing/opening the solution. Gateway reports the active
variant when XAE exposes it but does not change it.

### 7.2 `profile.xae.workspace`

| Property | Type/default | Meaning |
|---|---|---|
| `assumeAttachedSynchronized` | Boolean, `true` | Allows an exact attached XAE with no dirty project documents to establish the initial disk baseline without a forced reload. |
| `externalChangePolicy` | `reloadAll`, `reloadModified`, or `error`; `reloadModified` | Policy for external changes found in the exact project graph. |
| `autoSynchronizeBeforeOperation` | Boolean, `true` | Runs graph scan and policy-controlled typed reload before XAE operations. |

Dirty XAE buffers remain conflicts. The workspace section never grants save
or discard authority.

### 7.3 `profile.xae.capabilities`

| Property | Type/default | Meaning |
|---|---|---|
| `launch` | Boolean, `true` | Allows Gateway to launch XAE for the exact solution. |
| `close` | Boolean, `false` | Maximum permission to close XAE. Effective close also requires PID-scoped session consent. |
| `synchronize` | Boolean, `true` | Allows explicit and operation-required synchronization. |
| `discardDirtyDocuments` | Boolean, `false` | Allows an explicitly requested discard path. Never causes automatic discard. |
| `build` | Boolean, `true` | Allows PLC/solution Build, Rebuild, and Clean. |
| `activate` | Boolean, `false` | Allows XAE activation of the profile target. |

`close=false` is absolute. With `close=true`, session consent defaults to true
for Gateway-launched XAE and false for attached user XAE.

## 8. `profile.target`

### 8.1 Identity

| Property | Type/default | Meaning |
|---|---|---|
| `name` | string or `null` | Informational label for UI/logs. Not an identity check. |
| `amsNetId` | canonical six-octet string, required | Exact ADS target identity. |
| `monitoring` | object | Direct System Service and PLC runtime state polling. |
| `capabilities` | object | Target transitions and verification. |
| `tcUnit` | object or `null` | TcUnit completion/report contract. |

Gateway does not substitute another AMS NetId automatically.

### 8.2 `profile.target.monitoring`

| Property | Type/default | Meaning |
|---|---|---|
| `pollIntervalMilliseconds` | integer, `1000` | Delay between completed observation rounds. |
| `readTimeoutMilliseconds` | integer, `500` | Upper bound for one ADS state read. |

Gateway reads:

- System Service at port `10000`;
- PLC runtime ports discovered from the exact selected project graph;
- optional configured TcUnit runtime port.

Each observation remains separate. Monitoring does not publish aggregate
`runtime mode`.

### 8.3 `profile.target.capabilities`

| Property | Type/default | Meaning |
|---|---|---|
| `config` | Boolean, `false` | Allows `twincat_target_config` from any observed Target state. |
| `startRestart` | Boolean, `false` | Allows start from Config/Stopped and restart from Run. |
| `tcUnitVerification` | Boolean, `false` | Allows TcUnit verification attached to activation or target start/restart. Requires `tcUnit`. |

These booleans are maximum capabilities. Operator session locks may
temporarily reduce them.

### 8.4 `profile.target.tcUnit`

| Property | Type/default | Meaning |
|---|---|---|
| `runtimeId` | non-empty string | Logical PLC runtime identity in resources/results. |
| `adsPort` | integer, required | PLC ADS port used for completion reads. |
| `finishedSymbol` | non-empty string | Fixed Boolean completion symbol. |
| `suiteCountSymbol` | non-empty string | Fixed initialized-suite-count symbol. |
| `reportPath` | path, required | Fresh xUnit XML location visible to Gateway. |
| `allowDeleteExistingReport` | Boolean, `false` | Allows removal only of the configured baseline report before a run. |
| `completionTimeoutSeconds` | integer, `120` | Upper bound for completion/report observation. |
| `zeroTests` | `fail`, `warn`, or `allow`; `fail` | Policy for a fresh report containing zero tests. |

Pass/fail comes from a fresh valid xUnit report. Completion symbols prove only
that the designated run completed.

## 9. Source discovery

Source paths are not duplicated in configuration.

Gateway derives them from:

```text
solution -> selected projects -> .tsproj/.plcproj -> source graph
```

The agent reads:

```text
twincat-profile://{profile}/sources
twincat-profile://{profile}/sources/files
```

The compact resource returns minimal roots, project association, supported
extensions, existence, external-to-solution markers, counts, freshness, and a
bounded files reference.

## 10. Effective capability and denial

Effective capability:

```text
configured
AND session consent when required
AND NOT operator session lock
```

The Gateway response distinguishes:

- `CAPABILITY_DISABLED` — static configuration forbids the action;
- `OPERATOR_LOCKED` — temporarily blocked in UI;
- `XAE_CLOSE_CONSENT_REQUIRED` — configured close exists, but PID-scoped
  consent is off.

An explicit conversational prohibition prevents the agent from calling the
operation at all.

## 11. Migration from schema v1

There is no runtime compatibility shim.

Migration implementation must:

1. introduce schema v2 DTOs and validation;
2. update examples/tests;
3. reject schema v1 with `CONFIG_VERSION_UNSUPPORTED`;
4. update UI and generated documentation;
5. remove v1 properties rather than accept both shapes.

Property mapping is documented only to help the rework:

| Schema v1 | Schema v2 |
|---|---|
| `pipeName` | `gateway.pipeName` |
| log fields | `gateway.logging.*` |
| `agentProcessControl` | `gateway.processControl` |
| `solution` | `profile.xae.solution` |
| `xaeProgId` | `profile.xae.progId` |
| `allowXaeLaunch` | `profile.xae.capabilities.launch` |
| `allowCloseXae` | `profile.xae.capabilities.close` |
| `allowForceSynchronization` | `profile.xae.capabilities.synchronize` |
| `allowDirtyDocumentDiscard` | `profile.xae.capabilities.discardDirtyDocuments` |
| `allowActivation` | `profile.xae.capabilities.activate` |
| `expectedTarget` | `profile.target` identity |
| `runtimeMonitoring` | `profile.target.monitoring` |
| `tcUnit` | `profile.target.tcUnit` |
| `requireRecentSuccessfulBuild` | removed |
| `recentBuildMaxAgeSeconds` | removed |
| `autoWaitForTcUnit` | removed; verification is requested per operation |

The complete implementation sequence is in
[`ARCHITECTURE_REWORK_PLAN.md`](ARCHITECTURE_REWORK_PLAN.md).
