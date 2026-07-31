# TwinCAT Agent Gateway MCP reference — target contract

> **Status:** approved target API for the breaking architecture rework.
> Current binaries may still expose the v1 names listed in
> [`ARCHITECTURE_V1_BASELINE.md`](ARCHITECTURE_V1_BASELINE.md).

This file is the human-readable target reference for every MCP tool and
resource. After typed schemas are implemented, the installed
`twincat-doc://mcp` resource and this file must be generated from the same
source metadata.

## 1. Common conventions

### 1.1 Profile

All XAE/Target operations accept:

```json
{
  "profile": "default"
}
```

The caller does not pass solution path, AMS NetId, arbitrary ADS port, or raw
DTE command. Gateway resolves and verifies them from the profile.

### 1.2 Mutating result envelope

```json
{
  "ok": true,
  "operationId": "01J...",
  "component": "xae",
  "stage": "build.complete",
  "completion": "succeeded",
  "sideEffectsStarted": true,
  "result": {},
  "diagnostics": [],
  "resources": [
    {
      "uri": "twincat-operation://01J.../build",
      "mimeType": "text/plain"
    }
  ]
}
```

Every mutating call returns an exact `operationId`, even when it fails before
the intended TwinCAT action.

### 1.3 Errors

```json
{
  "ok": false,
  "operationId": "01J...",
  "component": "target",
  "stage": "target.config.preflight",
  "completion": "failed",
  "sideEffectsStarted": false,
  "error": {
    "code": "OPERATOR_LOCKED",
    "message": "Target Config is temporarily blocked by the operator.",
    "retryable": false
  }
}
```

Errors always identify `component` and `stage`. Timeout, `unknown`, stale
observation, missing report, and missing postcondition are not success.

### 1.4 No mandatory status preflight

Call the intended operation directly. Gateway verifies identity, capability,
locks, and postconditions internally.

If the desktop process is absent:

1. the operation returns `GATEWAY_NOT_RUNNING`;
2. the agent may call `gateway_start` once;
3. retry the original operation once.

## 2. Gateway tools

### 2.1 `gateway_start`

Starts or reuses the configured desktop Gateway.

Input:

```json
{
  "config": null
}
```

`config` is an optional explicit path. Otherwise MCP configuration discovery
is used.

Capability:

```text
gateway.processControl.allowStart
```

Behavior:

- reuse a ready Gateway with the same exact config;
- reject another per-user Gateway with a different config;
- launch through the supported interactive Windows path;
- wait boundedly for IPC identity and readiness.

Success result contains Gateway version, config identity, and readiness.

Typical errors:

```text
GATEWAY_CONFIG_NOT_FOUND
GATEWAY_CONFIG_AMBIGUOUS
GATEWAY_START_DISABLED
GATEWAY_RUNNING_DIFFERENT_PROJECT
GATEWAY_INTERACTIVE_LAUNCH_UNAVAILABLE
GATEWAY_START_TIMEOUT
```

### 2.2 `gateway_shutdown`

Requests graceful shutdown of the configured desktop Gateway.

Input:

```json
{}
```

Capability:

```text
gateway.processControl.allowShutdown
```

Gateway writes the IPC response before WPF shutdown. This tool does not
implicitly grant permission to close an attached user XAE. XAE cleanup follows
the effective XAE close capability and PID-scoped consent.

Typical errors:

```text
GATEWAY_NOT_RUNNING
CAPABILITY_DISABLED
OPERATOR_LOCKED
GATEWAY_SHUTDOWN_FAILED
```

## 3. XAE tools

### 3.1 `twincat_xae_open`

Ensures an XAE session for the profile's exact solution.

Input:

```json
{
  "profile": "default"
}
```

Capability:

```text
profile.xae.capabilities.launch
```

Behavior:

- attach to an exact compatible running solution;
- launch XAE only when needed and allowed;
- reject a mismatched solution rather than switching it silently;
- establish process ownership and close-consent defaults;
- establish or report synchronization state;
- build the profile source manifest when the project graph is available.

Postcondition:

```text
exact solution loaded and responsive, or a typed failure
```

Typical errors:

```text
PROFILE_NOT_FOUND
XAE_NOT_FOUND
XAE_MULTIPLE_MATCHES
XAE_LAUNCH_DISABLED
XAE_LAUNCH_FAILED
XAE_SOLUTION_MISMATCH
SYSMANAGER_NOT_AVAILABLE
COM_CALL_TIMEOUT
```

### 3.2 `twincat_xae_close`

Closes the exact XAE process selected for the profile.

Input:

```json
{
  "profile": "default",
  "saveMode": "prompt"
}
```

`saveMode`:

```text
save | discard | prompt
```

Required effective authority:

- `profile.xae.capabilities.close=true`;
- PID-scoped close consent;
- no XAE lifecycle operator lock;
- additionally `discardDirtyDocuments=true` for `discard`.

Session consent defaults to enabled for Gateway-launched XAE and disabled for
attached user XAE.

Gateway never force-kills the process. Success requires the exact captured PID
to exit.

Typical errors:

```text
CAPABILITY_DISABLED
XAE_CLOSE_CONSENT_REQUIRED
OPERATOR_LOCKED
DIRTY_XAE_DOCUMENT
XAE_CLOSE_FAILED
XAE_PROCESS_IDENTITY_CHANGED
```

### 3.3 `twincat_xae_sync`

Synchronizes the exact selected XAE project model with disk.

Input:

```json
{
  "profile": "default",
  "changedPaths": [],
  "discardDirtyDocuments": false
}
```

Capability:

```text
profile.xae.capabilities.synchronize
```

`changedPaths` is an optional hint inside the authoritative project graph.
Gateway always performs its own graph/fingerprint validation.

Behavior:

- validate exact solution/project graph;
- reject or explicitly discard dirty documents according to capability;
- apply the configured external-change policy;
- use typed VSSDK reload;
- refresh the confirmed fingerprint and source manifest.

Typical errors:

```text
CAPABILITY_DISABLED
OPERATOR_LOCKED
DIRTY_XAE_DOCUMENT
EXTERNAL_CHANGE_DETECTED
EXTERNAL_EDIT_UNSUPPORTED
EXTERNAL_EDIT_SYNC_FAILED
SOURCE_GRAPH_CHANGED_CONCURRENTLY
```

### 3.4 `twincat_xae_build`

Compiles a PLC project or builds the complete solution through XAE.

Input:

```json
{
  "profile": "default",
  "action": "rebuild",
  "scope": "plc",
  "project": null,
  "changedPaths": [],
  "detail": "compact"
}
```

`action`:

```text
build | rebuild | clean
```

`scope`:

```text
plc | solution
```

Default scope is `plc`. `project` is an optional logical project identity from
the source manifest; null means the profile-defined/default PLC selection.

Capability:

```text
profile.xae.capabilities.build
```

Behavior:

- ensure exact XAE session;
- synchronize according to workspace policy;
- select configured solution configuration/platform when present;
- build only the requested scope;
- observe BuildEvents and postconditions;
- return bounded diagnostics;
- store full build output and project-noise artifacts.

Target state is not a Gateway precondition for PLC compilation. Build never
performs Config, activation, or restart.

Typical errors:

```text
CAPABILITY_DISABLED
OPERATOR_LOCKED
BUILD_PROJECT_NOT_FOUND
BUILD_CONFIGURATION_NOT_FOUND
BUILD_CONFIGURATION_AMBIGUOUS
BUILD_FAILED
BUILD_RESULT_INCONSISTENT
COM_CALL_TIMEOUT
XAE_UNKNOWN_MODAL_DIALOG
```

Resources:

```text
twincat-operation://{operationId}/build
twincat-operation://{operationId}/xae-messages
twincat-operation://{operationId}/project-noise
```

### 3.5 `twincat_xae_activate`

Runs the native XAE activation pipeline, including its own compilation and
optional verification.

Input:

```json
{
  "profile": "default",
  "finalTargetMode": "run",
  "verification": "none",
  "changedPaths": []
}
```

`finalTargetMode`:

```text
run | unchanged
```

`verification`:

```text
none | tcunit
```

Required capabilities:

```text
profile.xae.capabilities.activate
profile.target.capabilities.tcUnitVerification  # only for tcunit
```

Behavior:

- ensure and synchronize exact XAE session;
- verify XAE-selected target against profile AMS NetId;
- call the native XAE activation command once;
- observe its internal compilation;
- handle known activation dialogs;
- report `sync`, `compile`, `deploy`, `target-transition`, and
  `verification` stages separately;
- never require or run a standalone build first.

`finalTargetMode=unchanged` reports whether configuration was stored and
whether physical activation could be verified; it never treats an old observed
Run state as proof that the new configuration is active.

With `verification=tcunit`, a test failure makes the requested workflow
unsuccessful while preserving successful `compile`/`deploy` stage results.

Typical errors:

```text
CAPABILITY_DISABLED
OPERATOR_LOCKED
TARGET_NOT_CONFIGURED
XAE_TARGET_MISMATCH
ACTIVATION_COMPILE_FAILED
ACTIVATION_DEPLOY_FAILED
TARGET_TRANSITION_FAILED
TARGET_STATE_UNKNOWN
TEST_COMPLETION_TIMEOUT
TEST_REPORT_NOT_PRODUCED
TEST_REPORT_INVALID
TEST_FAILED
```

Resources:

```text
twincat-operation://{operationId}
twincat-operation://{operationId}/events
twincat-operation://{operationId}/build
twincat-operation://{operationId}/xae-messages
twincat-operation://{operationId}/test/xunit
```

## 4. Target tools

### 4.1 `twincat_target_config`

Transitions the profile Target System to Config.

Input:

```json
{
  "profile": "default"
}
```

Capability:

```text
profile.target.capabilities.config
```

Semantics:

- accepted from any observed Target state;
- fresh confirmed Config may return a successful no-op;
- preserve available pre-transition XAE/ADS fault evidence;
- use the supported implementation transport;
- require a fresh direct System Service Config postcondition.

This is a normal Target operation. There is no separate recovery command.

Typical errors:

```text
CAPABILITY_DISABLED
OPERATOR_LOCKED
TARGET_NOT_CONFIGURED
TARGET_ADS_UNAVAILABLE
TARGET_CONFIG_FAILED
TARGET_CONFIG_POSTCONDITION_MISSING
```

### 4.2 `twincat_target_start_restart`

Starts or restarts the profile Target System.

Input:

```json
{
  "profile": "default",
  "verification": "none"
}
```

`verification`:

```text
none | tcunit
```

Required capabilities:

```text
profile.target.capabilities.startRestart
profile.target.capabilities.tcUnitVerification  # only for tcunit
```

Semantics:

- Config/Stopped → start;
- Run → restart;
- success requires fresh direct System Service Run evidence;
- optional TcUnit verification uses the same root `operationId`.

This tool is intentionally non-idempotent in Run.

Typical errors:

```text
CAPABILITY_DISABLED
OPERATOR_LOCKED
TARGET_NOT_CONFIGURED
TARGET_ADS_UNAVAILABLE
TARGET_START_RESTART_FAILED
TARGET_RUN_POSTCONDITION_MISSING
TEST_COMPLETION_TIMEOUT
TEST_REPORT_NOT_PRODUCED
TEST_FAILED
```

## 5. State and diagnostic resources

### 5.1 `twincat-gateway://state`

Current Gateway process state, version, active config/profile identity, current
operation, and journal cursors. It does not contain XAE/Target aggregate mode.

### 5.2 `twincat-gateway://diagnostics`

Gateway-wide IPC, configuration, queue, storage, logging, and host failures.
Use only when the error component is `gateway` or no narrower component can be
identified.

### 5.3 `twincat-profile://{profile}/capabilities`

Sanitized configured/session/effective capability matrix. It does not return
the complete raw config.

### 5.4 `twincat-profile://{profile}/sources`

Compact source manifest:

- solution directory;
- minimal source roots;
- project/role association;
- supported extensions;
- existence and outside-solution markers;
- discovery state/freshness;
- file count and bounded files resource.

### 5.5 `twincat-profile://{profile}/sources/files`

Bounded or paged exact project-graph source entries. Generated and unsupported
objects are marked explicitly.

### 5.6 `twincat-xae://profile/{profile}/state`

XAE process/session/solution/synchronization state plus the separately sourced
XAE TwinCAT system observation.

### 5.7 `twincat-xae://profile/{profile}/diagnostics`

ROT/DTE/COM, solution identity, project graph, dirty documents, synchronization,
BuildEvents, dialogs, and XAE observation freshness.

### 5.8 `twincat-xae://profile/{profile}/messages/current`

Bounded current XAE Error List error/warning snapshot after exact solution
verification.

### 5.9 `twincat-target://profile/{profile}/state`

Direct System Service observation at profile AMS NetId port 10000. Contains raw
`AdsState`, raw `DeviceState`, normalized state, timestamp, and read error.

It may also list references to discovered PLC runtime state resources, but it
does not aggregate their states.

### 5.10 `twincat-target://profile/{profile}/diagnostics`

ADS route/connection, System Service state, Target transitions, and comparison
with XAE-observed system state.

### 5.11 `twincat-plc://profile/{profile}/{runtime}/state`

Direct observation of one PLC ADS server. Contains runtime id, port, project
association, raw states, normalized PLC state, timestamp, and read error.

### 5.12 `twincat-plc://profile/{profile}/{runtime}/diagnostics`

PLC-port connection/state and verification evidence for that runtime. Future
debug details may extend this resource without merging it into Target state.

## 6. Operation resources

### 6.1 `twincat-operation://{operationId}`

Immutable compact summary with:

- root operation kind;
- requested profile;
- timestamps/duration;
- overall outcome;
- per-component/per-stage outcome;
- diagnostics counts;
- artifact links.

### 6.2 `twincat-operation://{operationId}/events`

Bounded event slice for the exact operation. Events retain component, stage,
severity, code, and timestamp.

### 6.3 Artifact resources

```text
twincat-operation://{operationId}/build
twincat-operation://{operationId}/xae-messages
twincat-operation://{operationId}/test/xunit
twincat-operation://{operationId}/project-noise
```

An unsupported or non-produced artifact returns `RESOURCE_NOT_FOUND`; it does
not fall back to the latest operation.

## 7. Documentation and log resources

### 7.1 `twincat-doc://setup`

Installed setup and agent-connection instructions.

### 7.2 `twincat-doc://configuration`

Installed generated copy of [`CONFIGURATION.md`](CONFIGURATION.md).

### 7.3 `twincat-doc://mcp`

Installed generated copy of this target reference.

### 7.4 `twincat-log://gateway/current`

Tracked current Gateway session log path and metadata. It does not read the
whole file and does not infer the path from configuration.

## 8. Removed v1 surface

The following names have no compatibility aliases:

```text
twincat_status
twincat_build
twincat_sync
twincat_close_xae
twincat_activate
twincat_recover_to_config
twincat_get_diagnostics
twincat_get_xae_messages
twincat_get_test_results
```

Migration is complete only when schemas, IPC, MCP adapter, UI, docs, examples,
skills, tests, and installed resources all use the target names.
