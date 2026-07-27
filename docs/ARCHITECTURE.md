# Архитектура TwinCAT Agent Gateway

## 1. Контекст

AI-агент должен иметь возможность редактировать PLC-код в файлах, собирать TwinCAT solution, получать компактный список ошибок, активировать конфигурацию на отладочном стенде и читать результаты unit-тестов.

Автоматизация TwinCAT XAE строится поверх COM-интерфейсов Visual Studio DTE и TwinCAT Automation Interface. Эти интерфейсы stateful, чувствительны к apartment model, состоянию IDE, модальным операциям и жизненному циклу COM-объектов. Одноразовые shell-процессы плохо подходят для удержания такой сессии.

Поэтому основой системы является постоянно работающий desktop gateway. MCP и CLI являются только внешними протоколами доступа.

## 2. Цели

- надёжно работать с TwinCAT 3.1.4024.17;
- держать одну контролируемую COM-сессию XAE;
- уменьшить расход токенов за счёт структурированных кратких результатов;
- отделить хрупкую XAE automation от конкретного AI-агента;
- сохранить возможность ручной диагностики через UI и CLI;
- использовать ADS только для read-only статуса System Service и проверки завершения TcUnit на явно выбранном target;
- редактировать PLC-код через файлы;
- не исправлять автоматически генерируемый `.tsproj` noise;
- сделать опасные операции явными и ограниченными project profiles.

## 3. Не-цели MVP

- универсальная автоматизация всех функций TwinCAT;
- online variables и symbol browsing;
- произвольные ADS reads/writes, RPC и управление runtime state через ADS;
- полный PLC debugger;
- автоматическое создание и изменение PLC objects через Automation Interface;
- I/O configuration;
- CI/headless XAE;
- автоматическое исправление `.tsproj`;
- PowerShell API;
- обязательная поддержка TwinCAT 4026/Visual Studio 2022.

## 4. Архитектура процессов

```text
┌───────────────────────────────────────────────────────────────┐
│ Codex / Claude Code / другой агент                            │
│                                                               │
│  twincat-build  twincat-test  twincat-activate  diagnose      │
└──────────────────────────────┬────────────────────────────────┘
                               │ MCP stdio / другой MCP transport
                     ┌─────────▼─────────┐
                     │ Gateway MCP       │ .NET 8
                     │ thin adapter      │
                     └─────────┬─────────┘
                               │
                               │ versioned local IPC
                               │
┌──────────────────────────────▼────────────────────────────────┐
│ TwinCatGateway.Desktop                                        │
│ .NET Framework 4.8 x86, интерактивная Windows-сессия          │
│                                                               │
│ UI / tray                                                     │
│ Operation Queue ─ State Machine ─ Operation Store             │
│ Build Service ─ Activation Service ─ Status/Diagnostics       │
│ File Change Classifier ─ TcUnit Test Completion/Report        │
│                                                               │
│ ┌───────────────────────────────────────────────────────────┐ │
│ │ XAE COM Host                                               │ │
│ │ один STA thread + message pump + OLE IMessageFilter        │ │
│ │ DTE/DTE2 + ITcSysManager + BuildEvents + Error List        │ │
│ └───────────────────────────────────────────────────────────┘ │
└───────────────────────┬──────────────────────┬───────────────┘
                        │ COM                  │ read-only ADS
               ┌────────▼────────┐     ┌───────▼──────────────┐
               │ TwinCAT XAE     │     │ selected target      │
               │ VS2019/XAE Shell│     │ system port 10000    │
               └─────────────────┘     │ + PLC runtime port   │
                                       └──────────────────────┘
                                       ReadState + fixed TcUnit
                                       completion symbols only

┌──────────────────────┐
│ twincatctl            │ .NET 8
│ thin IPC client       │
└──────────┬───────────┘
           └────────────── тот же local IPC
```

## 5. Почему desktop gateway

Gateway должен работать в интерактивной пользовательской сессии, потому что:

- XAE является desktop IDE;
- часть проблем требует визуальной проверки человеком;
- пользователь должен видеть выбранный solution и target;
- Windows Service усложняет COM, desktop interaction и session isolation;
- gateway может показывать блокирующие состояния, logs и safety prompts.

Gateway не обязан всегда отображать главное окно. Допустим tray mode с отдельным окном состояния.

## 6. Target frameworks и bitness

### Desktop host

Начальная рекомендация:

- .NET Framework 4.8;
- x86;
- WPF;
- COM interop для установленного поколения XAE.

Причина — минимизация рисков совместимости с TwinCAT 4024 и 32-bit Visual Studio 2019/XAE Shell.

### Contracts

- `netstandard2.0` или небольшой multi-targeted проект;
- DTO, enums, error codes и protocol version;
- без ссылок на COM/UI/MCP.

### MCP и CLI

- .NET 8;
- thin adapters;
- только IPC client + contracts.

MCP adapter реализуется отдельным проектом `TwinCatGateway.Mcp` на официальном
[`modelcontextprotocol/csharp-sdk`](https://github.com/modelcontextprotocol/csharp-sdk).
Для MVP закрепляется stable NuGet package `ModelContextProtocol` версии
`1.4.1` и stdio transport (`WithStdioServerTransport()`). SDK отвечает только
за MCP protocol, hosting, schemas, tools и resources; все операции выполняются
через `TwinCatGateway.Client` и versioned local IPC.

`ModelContextProtocol.AspNetCore`, HTTP transport, prerelease `2.x` и
`ModelContextProtocol.Extensions.Tasks` в MVP не используются. Длительные
операции сохраняют собственную gateway-семантику `operationId`/polling. Ни
Desktop/XAE host, ни Contracts/Core/Ipc не должны зависеть от MCP SDK.

После подтверждения реальной совместимости можно пересмотреть target framework desktop host, не меняя внешний protocol.

## 7. Внутренние компоненты

### 7.1 XaeSession

Отвечает за:

- обнаружение DTE через Running Object Table;
- выбор экземпляра по абсолютному `Solution.FullName`;
- запуск XAE, если это разрешено;
- открытие solution;
- получение `ITcSysManager`;
- проверку responsiveness;
- восстановление после закрытия или перезапуска XAE;
- фиксацию process identity и версии.

Сессия не должна автоматически подключаться к случайному XAE с другим solution.

### 7.2 ComStaDispatcher

Единственная точка выполнения COM-кода:

- выделенный STA thread;
- message pump;
- очередь делегатов;
- cancellation до начала COM-вызова;
- deadline операции;
- OLE `IMessageFilter`;
- telemetry повторённых `RPC_E_CALL_REJECTED`.

`Task.Run` не используется для параллельного вызова DTE.

### 7.3 OperationQueue

Изменяющие операции выполняются строго последовательно:

- build/rebuild/clean;
- открытие solution;
- activation;
- recovery to Config.

Status endpoint читает immutable snapshot и не блокирует UI на COM-вызове. Обновление snapshot выполняется gateway контролируемо.

### 7.4 BuildService

Функции:

- Build/Rebuild/Clean;
- работа с configuration/platform;
- `BuildEvents`;
- Error List snapshot;
- Output pane delta;
- `LastBuildInfo`;
- нормализация diagnostics;
- полный raw log;
- запуск `.tsproj` noise classifier после операции.

### 7.5 ActivationService

Высокоуровневая операция `activate`.

Физическая семантика:

1. при необходимости recovery to Config;
2. `ITcSysManager.ActivateConfiguration()`;
3. `ITcSysManager.StartRestartTwinCAT()`;
4. ожидание postconditions;
5. чтение новых ошибок.

`ActivateConfiguration()` сам по себе соответствует сохранению текущей конфигурации как активной. Отдельный start/restart необходим для физического применения. При включённом `Autostart boot project` boot project запускается после рестарта.

### 7.6 StatusService

Создаёт два представления:

- compact status — для частых агентских вызовов;
- detailed diagnostics — для расследования проблемы.

### 7.7 TcUnitTestService

В MVP использует узкий read-only ADS adapter и файловый report reader. Он:

- после связанной activation подключается к PLC runtime выбранного target;
- опрашивает фиксированный symbol `GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished`;
- читает `GVL_TcUnit.NumberOfInitializedTestSuites` для дополнительной проверки;
- после подтверждения завершения ожидает свежий настроенный xUnit XML;
- проверяет timestamp, стабильность файла и целостность XML;
- возвращает counts и failed tests;
- хранит исходный XML как resource.

ADS adapters не принимают от вызывающего кода произвольные NetId, port или symbol path, не пишут значения, не вызывают RPC и не меняют runtime state. Status adapter вызывает только `TryReadState` на фиксированном System Service port 10000 выбранного XAE target. Activation/restart остаются обязанностью `ActivationService` через Automation Interface.

Стандартные symbol paths задаются operator-controlled profile и проверяются на закреплённой версии TcUnit. Они не считаются стабильным публичным API TcUnit и не передаются произвольными аргументами MCP/CLI.

Если unit-тесты запускаются автоматически вместе с boot project, test operation может быть связана с activation operation.

### 7.8 ProjectChangeClassifier

Определяет шумовые изменения `.tsproj` без их исправления.

Результат используется только для отчёта агенту и UI.

### 7.9 OperationStore и LogStore

OperationStore хранит structured metadata. LogStore хранит большие raw artifacts.

Минимальные artifacts:

- build output;
- XAE activity/diagnostic log, если доступен;
- Error List snapshot/delta;
- activation timeline;
- TcUnit xUnit XML;
- summary `.tsproj` noise.

## 8. IPC

### 8.1 Transport

Для локального Windows MVP рекомендуется Named Pipes:

- доступ только текущему пользователю или настроенной ACL;
- отсутствие открытого TCP-порта;
- поддержка request/response и чтения resources;
- независимость от MCP transport.

Transport скрыт за интерфейсом, чтобы позднее можно было добавить localhost HTTP/gRPC без изменения domain operations.

### 8.2 Protocol

Каждый запрос содержит:

```json
{
  "protocolVersion": 1,
  "requestId": "01J...",
  "method": "build",
  "params": {}
}
```

Ответ:

```json
{
  "protocolVersion": 1,
  "requestId": "01J...",
  "ok": true,
  "result": {}
}
```

Для длительной операции возможны два режима:

- `wait=true`: ответ после окончания;
- `wait=false`: немедленно вернуть `operationId`, затем использовать `getOperation`.

В MVP CLI и MCP могут по умолчанию ожидать завершения, сохраняя operationId для диагностики.

### 8.3 Versioning

- protocol version передаётся в каждом запросе;
- неизвестная major version отклоняется;
- добавление optional fields обратно совместимо;
- изменение семантики error code требует migration note.

## 9. State machines

### 9.1 Gateway state

```text
Starting
  -> Disconnected
  -> Attaching
  -> OpeningSolution
  -> Ready
  -> Building
  -> Activating
  -> RecoveringToConfig
  -> Ready
  -> Faulted
  -> Stopping
```

`Faulted` не означает обязательное завершение процесса. Gateway может принять `reconnect` или автоматически восстановить session по ограниченной политике.

### 9.2 Operation state

```text
Queued -> Running -> Succeeded
                  -> Failed
                  -> TimedOut
                  -> Cancelled
```

Operation deadline не заменяет postcondition.

## 10. Build pipeline

### 10.1 Вход

```json
{
  "profile": "default",
  "action": "rebuild",
  "configuration": null,
  "platform": null,
  "changedPaths": [
    "TC3_SimpleProject/PlcProject1/POUs/MAIN.TcPOU"
  ],
  "detail": "compact"
}
```

Configuration/platform берутся из profile или активного solution, если не заданы явно и это разрешено политикой.
`changedPaths` необязателен: gateway в любом случае обнаруживает изменения по
fingerprint baseline, а список от caller используется только как
дополнительная явная подсказка.

### 10.2 Последовательность

1. Валидация profile, solution и action.
2. Сравнение текущих PLC source fingerprints с session baseline.
3. Объединение обнаруженных файлов с необязательным `changedPaths`.
4. Отказ для добавленных/удалённых source files, пока structural sync не
   реализован.
5. Закрытие поддерживаемых XAE editors без сохранения; dirty in-memory
   изменения отбрасываются.
6. Типизированный reload изменённых документов через VSSDK Running Document
   Table и `IVsPersistDocData.ReloadDocData(...)`.
7. Повторный fingerprint scan; concurrent external change завершает операцию
   ошибкой.
8. SHA-256 snapshot всех `.tsproj` и временное подавление их file-change
   notifications через `SVsFileChangeEx` / `IVsFileChangeEx.IgnoreFile(...)`.
9. Snapshot текущих Output позиций.
10. Подписка/проверка `BuildEvents`.
11. Запуск Build/Clean через `SolutionBuild`; Rebuild через
    `DTE.ExecuteCommand("Build.RebuildSolution")`.
12. Ожидание точного `OnBuildDone` action/scope и проверка `BuildState`.
13. Проверка `.tsproj` hashes, синхронизация file watcher и обязательное
    восстановление notifications.
14. Чтение `LastBuildInfo`, Error List snapshot и Output delta.
15. Нормализация diagnostics.
16. Классификация содержательных `.tsproj` changes.
17. Сохранение полного Output delta как отдельного build-log resource.
18. Возврат compact result.

`DTE.ExecuteCommand(...)` допустим для стабильной встроенной команды XAE/VS,
если нет надёжного отдельного typed automation method. Он всегда вызывается
на единственном STA, а его возврат не считается завершением операции:
необходимы соответствующее событие и проверяемые postconditions. Для Rebuild
ожидается `vsBuildActionRebuildAll` со scope `Solution`.

### 10.3 Diagnostic DTO

```json
{
  "severity": "error",
  "source": "plc-compiler",
  "code": "C0032",
  "message": "Cannot convert type ...",
  "file": "Plc/POUs/FB_Test.TcPOU",
  "line": 48,
  "column": 17
}
```

Если код compiler message выделить надёжно нельзя, `code` может быть `null`. Не извлекай его хрупким regex без тестов на реальных сообщениях 4024.17.

### 10.4 Compact result

```json
{
  "ok": false,
  "operationId": "01J...",
  "action": "rebuild",
  "durationMs": 18472,
  "counts": {
    "errors": 2,
    "warnings": 1
  },
  "diagnostics": [],
  "moreDiagnostics": 0,
  "expectedProjectNoise": [
    {
      "file": "Machine.tsproj",
      "kind": "reorder-only",
      "movedBlocks": 18,
      "doNotInspectFullFile": true
    }
  ],
  "logRef": "twincat-log://01J.../build"
}
```

## 11. Activation pipeline

### 11.1 Важное различие

В XAE пользовательская команда **Activate Configuration** может включать диалоги и предложение рестарта. Метод Automation Interface `ActivateConfiguration()` имеет более узкую семантику: сохраняет конфигурацию как активную. После него вызывается `StartRestartTwinCAT()`.

### 11.2 Последовательность

1. Проверить `allowActivation` profile.
2. Проверить выбранный target и solution.
3. Проверить, что gateway не выполняет build.
4. Проверить policy актуальности последней успешной сборки.
5. Сохранить solution.
6. Если предыдущая ошибка или состояние требует Config Mode, выполнить `RecoverToConfig`.
7. Вызвать `ActivateConfiguration()`.
8. Вызвать `StartRestartTwinCAT()`.
9. Дождаться окончания команды по доступным XAE/Automation Interface признакам.
10. Проверить runtime state через read-only ADS `TryReadState` на System Service port 10000.
11. Прочитать `GetLastErrorMessages()` и XAE diagnostics.
12. Если это включено profile, запустить связанную test operation: дождаться ADS completion signal и затем свежего TcUnit report.

### 11.3 Recovery to Config

Automation Interface предоставляет `StartRestartTwinCAT()` для Run, но не даёт столь же очевидного отдельного метода `StartRestartTwinCATInConfigMode` в базовом `ITcSysManager`.

Для TwinCAT 4024 требуется технический spike:

- определить стабильную XAE command identity для `Restart TwinCAT (Config Mode)`;
- вызывать её через DTE command service или другой официальный XAE extension API;
- не зависеть от локализованного menu caption;
- проверить доступность и завершение команды;
- проверить сценарий PLC exception на тестовом стенде.

Если надёжная автоматизация Config Mode не подтверждена, MVP не должен скрывать проблему. Операция возвращает `CONFIG_MODE_REQUIRED` и инструкцию для ручного перехода, пока adapter не реализован.

### 11.4 Safety profile

Пример удалённого тестового стенда:

```yaml
name: bench-remote
solution: C:\Projects\Machine\Machine.sln
allowActivation: true
requireRecentSuccessfulBuild: true
autoWaitForTcUnit: true
tcUnitAdsPort: 851
tcUnitFinishedSymbol: GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished
tcUnitSuiteCountSymbol: GVL_TcUnit.NumberOfInitializedTestSuites
tcUnitReportPath: C:\TwinCAT\3.1\Boot\tcunit_xunit_testresults.xml
```

Target identity следует показывать и сохранять в audit log. ADS completion adapter получает NetId только из target, выбранного и проверенного XAE/profile; отдельный произвольный NetId от MCP/CLI запрещён.

## 12. Status и detailed diagnostics

### 12.1 Compact

Предназначен для агента и частого polling:

```json
{
  "gateway": {
    "state": "ready",
    "version": "0.1.0"
  },
  "xae": {
    "connected": true,
    "version": "16.0",
    "solution": "C:\\Projects\\Machine\\Machine.sln"
  },
  "twinCat": {
    "started": true,
    "mode": "unknown"
  },
  "currentOperation": null,
  "lastBuild": {
    "ok": true,
    "errors": 0,
    "warnings": 2
  },
  "lastActivation": {
    "ok": true
  },
  "unreadErrors": 0
}
```

### 12.2 Detailed

Под «полной диагностикой» понимается не полный дамп проекта, а расширенный health snapshot:

- все найденные DTE instances;
- причина выбора текущего экземпляра;
- ProgID, process id, DTE version;
- solution load state;
- `ITcSysManager` availability;
- active configuration/platform;
- target NetId;
- raw ADS state и device state с System Service port 10000;
- `GetLastErrorMessages()`;
- последний HRESULT;
- COM retry counts и latency;
- operation timeline;
- ссылки на raw logs;
- build diagnostics;
- `.tsproj` noise classification;
- IPC/log-store health.

### 12.3 Runtime status

Gateway читает состояние выбранного target через `AdsClient.TryReadState` на фиксированном ADS System Service port 10000. NetId поступает только из типизированного `ITcSysManager2.GetTargetNetId()` и не задаётся MCP/CLI caller. Это отдельный узкий read-only adapter, а не general-purpose ADS surface.

Поле `mode` принимает:

```text
run | config | exception | stopped | unknown
```

`Run`, `Config/Reconfig`, `Stop/Stopping/Shutdown` и `Error/Exception` отображаются соответственно в `run`, `config`, `stopped` и `exception`. Переходные, неподдержанные состояния и ошибки ADS возвращают `unknown`. Detailed diagnostics сохраняет NetId, port, raw ADS state, device state, timestamp и error code. Runtime status failure не делает исправную XAE-сессию disconnected.

## 13. Редактирование через файлы

### 13.1 Основной workflow

```text
agent edits files
    -> build(changedPaths?)
    -> gateway detects all changed PLC sources
    -> gateway synchronizes XAE project model
    -> build
    -> diagnostics
```

Преимущества:

- стандартный `git diff`;
- обычные patch-инструменты Codex;
- легко откатывать изменения;
- не нужно передавать PLC code через MCP;
- меньше COM surface.

### 13.2 Agent owns workspace

Пока gateway подключён к solution, поддерживаемые PLC source files принадлежат
агенту. Агент может менять любой из них в любое время после подключения и не
обязан заранее объявлять paths. Несохранённые изменения тех же документов в
XAE не сохраняются: gateway закрывает такие editors с
`vsSaveChangesNo`. Отдельных `SaveAll|Reject` policy и
`prepare_external_edit` / `complete_external_edit` handshake нет.

При подключении gateway вычисляет SHA-256 fingerprint всех `.TcPOU`, `.TcGVL`
и `.TcDUT` под solution root. Непосредственно перед каждой
Build/Rebuild/Clean выполняется новый scan. Фактический diff является
авторитетным; `changedPaths` только дополняет его.

Проверка на TwinCAT 3.1.4024.17 уточнила границу:

- после обычного внешнего edit открытого сохранённого `.TcPOU`
  `EnvDTE.Document.Saved` остаётся `true`; при следующем build XAE может
  показать project-level и editor-level reload dialogs;
- внешний edit открытого несохранённого document без предварительного захвата
  workspace создаёт file-modification conflict dialog; Silent Mode его не
  подавляет;
- после обычного внешнего edit закрытого `.TcPOU` modal dialog не появляется,
  но build использует stale project model; одного `Documents.Open(...)`
  недостаточно;
- типизированный VSSDK reload через Running Document Table и
  `IVsPersistDocData.ReloadDocData(...)` с
  `RDD_IgnoreNextFileChange|RDD_RemoveUndoStack` устраняет modal dialog;
  последующий build подтверждённо использует внешне изменённый ST source;
- для закрытого `.TcPOU` временное открытие с последующим
  `ReloadDocData(...)` обновляет project model; XAE после reload снова не
  оставляет editor открытым;
- закрытие dirty editor с `vsSaveChangesNo` надёжно отбрасывает in-memory
  версию до reload.

Перед build gateway повторно захватывает ownership, временно открывает каждый
изменённый закрытый document, выполняет typed reload и снова закрывает editor.
После reload выполняется второй fingerprint scan: изменение файлов во время
синхронизации возвращает retryable error. В MVP добавление и удаление project
sources не синхронизируется автоматически и завершается явной ошибкой.

## 14. `.tsproj` reorder-only noise

### 14.0 XAE file watcher guard

TwinCAT 3.1.4024.17 может во время обычной Build/Clean/Rebuild перезаписать
`.tsproj` теми же байтами, изменив только filesystem timestamp. XAE file
watcher способен увидеть эту собственную запись и показать modal
`File Modification Detected`; Silent Mode этого не предотвращает.

Поэтому gateway перед запуском операции вычисляет SHA-256 всех `.tsproj` под
solution root и временно вызывает
`IVsFileChangeEx.IgnoreFile(0, path, 1)`. После `OnBuildDone`:

- если файл существует и hash совпадает, gateway вызывает `SyncFile(path)`
  при ещё подавленных notifications, затем возвращает
  `IgnoreFile(0, path, 0)`;
- если hash изменился, gateway запускает classifier до восстановления
  notifications; подтверждённые `whitespace-only` и `reorder-only` changes
  синхронизируются при ещё подавленных notifications;
- `content-changed`, `unknown`, добавление или удаление `.tsproj` завершают
  операцию явной `EXTERNAL_EDIT_UNSUPPORTED` со ссылкой на classifier
  artifact; перед ошибкой notifications обязательно восстанавливаются;
- восстановление notifications выполняется также при исключении и Dispose.

Guard и classifier не перезаписывают файл и не скрывают содержательные
изменения. Проверенный
`IVsRunningDocumentTable5.HandsOffDocument/HandsOnDocument` для этой задачи
не используется: XAE Shell на базе Visual Studio 2019 в тестовой конфигурации
не зарегистрировал COM proxy этого интерфейса.

### 14.1 Требования

- ничего не перезаписывать;
- не вызывать reload ради cleanup;
- не скрывать содержательные изменения;
- не заставлять агента читать большой XML diff;
- не считать рабочее дерево чистым, если Git показывает изменение;
- явно помечать изменение как ожидаемый generated noise.

### 14.2 Классификатор

MVP хранит проверенный schema bundle для TwinCAT XAE `3.1.4024.17` в
versioned repository path. Root schema `TcSmProject.xsd` поставляется вместе
со всем dependency closure:

- `TcSmItem.xsd`;
- `TcUserManagement.xsd`;
- `TcModuleBase.xsd`.

Manifest набора содержит XAE version, поддерживаемые `TcSmVersion` /
`TcVersion`, исходный installation path и SHA-256 каждого XSD. Сама
`TcSmProject.xsd` не задаёт patch XAE: `TcVersion` имеет тип `xs:string`, а
`TcSmVersion` ограничен только форматом. Поэтому выбор schema set является
нашей versioned compatibility policy, а не выводится из XSD автоматически.
Resolver разрешает только файлы внутри выбранного bundle; DTD, сеть и
произвольные external paths запрещены. Если подходящего набора нет,
classification — `unknown`.

Алгоритм:

1. Найти изменённые `.tsproj` после завершённого Build/Rebuild и использовать
   точный pre-operation byte snapshot как baseline.
2. Выбрать один закреплённый schema set по версии attached XAE и root
   `TcSmVersion` / `TcVersion`.
3. Провалидировать baseline и current одной и той же официальной XSD.
4. Построить canonical XML без незначащего formatting noise: attribute order
   не учитывается, но имена, namespaces, attributes, значащий text/CDATA,
   comments, processing instructions и состав элементов сохраняются.
5. Рекурсивно доказать равенство полного XML tree с точностью только до
   перестановок sibling-subtrees внутри того же родителя:
   - hash вычисляется от полного canonical subtree;
   - для каждого соответствующего parent совпадает multiset child hashes;
   - перенос блока между разными parents запрещён;
   - отличается только позиция неизменённых children.
6. Поскольку оба полных документа XSD-valid в своих итоговых порядках, а
   завершившаяся компиляция является источником истины для XAE project model,
   вернуть `expected-reorder-only` и количество наблюдаемых перемещений.

GUID/Id/Name/Path не участвуют в доказательстве равенства и могут
использоваться только как human-readable labels и для подсчёта перемещений.
Повторяющиеся одинаковые subtree hashes безопасно сравниваются как multiset.

Если ordered canonical XML одинаков, результат — `whitespace-only`. Если оба
документа XSD-valid, но изменились attribute, text, состав или parent блока,
результат — `content-changed`. XSD-invalid XML, отсутствие совместимого
schema set или невозможность доказать только sibling permutation дают
`unknown`, а не `expected-reorder-only`.

Для этой classification не требуется отдельный whitelist контейнеров,
которым gateway приписывает order-insensitive semantics. Доказательство
ограничено XSD-valid full-tree sibling permutation, а XAE/compiler после
завершённой компиляции является авторитетом итогового порядка. PLC compile
errors не отменяют это основание, если build lifecycle завершился штатно;
infrastructure failure или незавершённая операция не дают classification.
Classifier ничего не перезаписывает и не откатывает.

Clean не выполняет компиляцию. Для него MVP автоматически принимает только
exact-same-content `.tsproj` rewrite; content hash change после Clean
возвращается как `unknown`.

Compact response содержит только counts и рекомендацию, а локальный
`ProjectNoise` resource — classification reason без полного XML diff.

### 14.3 Ответ агенту

```json
{
  "file": "Machine.tsproj",
  "classification": "expected-reorder-only",
  "movedBlocks": 18,
  "doNotInspectFullFile": true,
  "contentChanges": 0
}
```

Skill должен инструктировать агента не читать полный `.tsproj`, пока classifier не сообщил содержательное изменение.

### 14.4 Валидация PLC object files

Тот же versioned schema bundle содержит `TcPlcObject.xsd`. В MVP gateway
валидирует изменённые `.TcPOU`, `.TcGVL` и `.TcDUT` до typed reload/build.
Схема также описывает Interface, Task, Visualization и другие PLC objects,
но их поддержка добавляется только вместе с соответствующим file-edit
contract.

PLC XSD используется как fail-fast structural validation, а не как
noise-classifier. Любое изменение declaration, implementation, attributes
или другого XML content остаётся содержательным. Gateway не форматирует и не
переписывает PLC object.

Другие XSD из `C:\TwinCAT\3.1\Config\Modules` не копируются автоматически.
Новый root schema и его полный dependency closure добавляются в bundle,
только когда соответствующий формат входит в поддерживаемый gateway
contract. Например, `TcModuleClass.xsd` не нужен MVP, пока generated TMC не
становится входом операции.

## 15. TcUnit с read-only ADS completion

Предполагаемый workflow:

1. Агент исправляет код.
2. Gateway выполняет Build/Rebuild.
3. Агент явно вызывает Activate.
4. `StartRestartTwinCAT()` запускает boot project при включённом Auto Boot.
5. Назначенная PLC task циклически выполняет отдельную test program, которая инстанцирует suites и вызывает `TcUnit.RUN()` или `TcUnit.RUN_IN_SEQUENCE()`.
6. Gateway подключается к тому же target по ADS на настроенный PLC port.
7. Gateway опрашивает `GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished` до `TRUE`, cancellation или deadline.
8. Gateway читает `GVL_TcUnit.NumberOfInitializedTestSuites`.
9. После ADS completion TcUnit публикует xUnit XML.
10. Gateway проверяет свежесть, стабильность и парсит XML.
11. Агент получает counts и failures.

Перед activation gateway сохраняет baseline report и удаляет старый файл только при явно разрешённом локальном report path. Минимальная проверка текущего запуска:

- связать test operation с конкретным successful activation/restart;
- использовать NetId только из выбранного activation profile/XAE target;
- дождаться доступности двух фиксированных TcUnit symbols;
- получить `AllTestSuitesFinished=TRUE` в пределах deadline;
- сохранить ADS evidence и suite count в operation timeline;
- дождаться нового изменения;
- дождаться стабильного размера;
- успешно распарсить XML;
- проверить наличие test suite/test case данных.

ADS completion является доказательством окончания выполнения, но не источником pass/fail. Авторитетный результат — свежий валидный xUnit XML текущей operation. Лучшее будущее улучшение — run identifier внутри test harness/report.

Требования к test project:

- TcUnit library version закреплена и доступна в library repository стенда;
- test program назначена PLC task и не зависит от production I/O;
- `GVL_Param_TcUnit.xUnitEnablePublish=TRUE`;
- `GVL_Param_TcUnit.xUnitFilePath` указывает на разрешённый и доступный gateway path;
- tests, которые используют `TEST_FINISHED()`, могут занимать несколько PLC cycles, поэтому fixed delay не заменяет completion signal;
- для separate test solution production code подключается как library/source reference, а activation разрешена только test profile.

## 16. MCP и token economy

### Tools

```text
twincat_status
twincat_build
twincat_activate
twincat_get_diagnostics
twincat_get_test_results
```

### Resources

```text
twincat-log://<operation-id>/build
twincat-log://<operation-id>/xae
twincat-test://<operation-id>/xunit
twincat-diff://<operation-id>/project-noise
```

Tool result должен быть достаточен для обычного исправления compile error. Resource читается только для нестандартной диагностики.

## 17. Пользовательский интерфейс

Минимальный UI:

- Gateway state;
- XAE connection и solution;
- текущая операция и progress stage;
- последние Build/Activate/Test результаты;
- выбранный activation profile и target identity;
- список recent operations;
- просмотр structured и raw logs;
- кнопки reconnect, build, activate и open log folder;
- явный индикатор, когда activation запрещён profile;
- индикатор agent-owned workspace и количества отброшенных dirty documents;
- отображение ошибок fingerprint/reload synchronization.

UI не должен содержать отдельную реализацию операций; он вызывает тот же application service, что IPC.

## 18. Ошибки

Примеры error codes:

```text
GATEWAY_NOT_READY
XAE_NOT_FOUND
XAE_MULTIPLE_MATCHES
XAE_SILENT_MODE_FAILED
SOLUTION_NOT_FOUND
SOLUTION_MISMATCH
SYSMANAGER_NOT_AVAILABLE
COM_CALL_REJECTED
COM_CALL_TIMEOUT
BUILD_FAILED
BUILD_RESULT_INCONSISTENT
XAE_WORKSPACE_OWNERSHIP_FAILED
EXTERNAL_EDIT_UNSUPPORTED
EXTERNAL_EDIT_SYNC_FAILED
ACTIVATION_NOT_ALLOWED
CONFIG_MODE_REQUIRED
ACTIVATE_CONFIGURATION_FAILED
TWINCAT_RESTART_FAILED
TWINCAT_STATE_UNKNOWN
TEST_ADS_UNAVAILABLE
TEST_COMPLETION_SYMBOL_UNAVAILABLE
TEST_COMPLETION_TIMEOUT
TEST_REPORT_NOT_PRODUCED
TEST_REPORT_INVALID
IPC_VERSION_MISMATCH
```

Каждая ошибка содержит:

- code;
- message;
- retryable;
- operationId;
- stage;
- rawLogRef;
- HRESULT во внутренней диагностике.

## 19. Безопасность

- Named Pipe ACL ограничена текущим пользователем.
- Activation запрещена по умолчанию.
- Для этого репозитория локальная activation/restart и другие изменения состояния TwinCAT runtime запрещены; такие сценарии выполняются только на явно разрешённом удалённом тестовом стенде.
- ADS разрешён только для `ReadState` на фиксированном System Service port 10000 и чтения фиксированных TcUnit completion symbols на target, выбранном и проверенном через XAE/profile. Произвольные NetId, ports, symbol paths, ADS writes, RPC и `WriteControl` не входят в gateway API.
- Profile задаётся локальной конфигурацией, а не произвольными аргументами агента.
- Solution/target выводятся перед activation в UI и operation log.
- MCP не получает произвольный COM invoke tool.
- Нет инструмента «выполнить DTE command по строке».
- Нет произвольного чтения файлов через MCP resource.
- Log resource принимает только существующий operationId и известный artifact kind.

## 20. Наблюдаемость

Metrics не обязательны для MVP, но structured events должны позволять измерить:

- длительность attach/open/build/activate;
- количество COM retries;
- timeout rate;
- XAE reconnect count;
- build success rate;
- размер compact response;
- число diagnostics, обрезанных лимитом;
- число reorder-only classifications;
- ADS connect/reconnect count и TcUnit completion wait duration;
- TcUnit report wait duration.

## 21. Открытые технические вопросы

До фиксации реализации провести spikes:

1. Стабильный вызов `Restart TwinCAT (Config Mode)` в XAE 4024.17 без ADS runtime control.
2. Поддержка structural sync для добавленных и удалённых PLC source files.
3. Полнота Error List по сравнению с Build Output на реальных PLC compile errors.
4. Точный lifecycle `BuildEvents` в нескольких открытых XAE instances.
5. Silent Mode и поведение confirmation dialogs при activation на тестовом стенде.
6. Reconnect ADS client после restart и доступность TcUnit completion symbols на реальном стенде.
7. Закреплённая версия TcUnit, стабильность внутренних completion symbol paths и поведение `xUnitEnablePublish/xUnitFilePath` на 4024.17.

## 22. Источники

- Beckhoff: TwinCAT projects with AI-supported engineering — https://www.beckhoff.com/en-en/products/automation/twincat-projects-with-ai-supported-engineering/
- Beckhoff: TwinCAT Automation Interface — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242681355.html
- Beckhoff: ITcSysManager — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242753675.html
- Beckhoff: ActivateConfiguration — https://infosys.beckhoff.com/content/1031/tcautomationinterface/12425796491.html
- Beckhoff: StartRestartTwinCAT — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242762891.html
- Beckhoff: Silent Mode — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/2489025803.html
- Beckhoff: AdsClient.TryReadState — https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407838987.html
- Beckhoff: ADS System Service port 10000 — https://infosys.beckhoff.com/content/1033/tcadscommon/12439473419.html
- Microsoft: IVsDocDataFileChangeControl — https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsdocdatafilechangecontrol
- Microsoft: IVsPersistDocData.ReloadDocData — https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivspersistdocdata.reloaddocdata
- Microsoft: IVsFileChangeEx — https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsfilechangeex
- Existing minimal build skill — https://github.com/IwanowS/codex-skill-twincat-build
- Reference all-in-one project — https://github.com/Lance0901/AI-TwinCAT-Skill
- TcUnit documentation — https://tcunit.org/
- TcUnit user guide and source — https://github.com/tcunit/TcUnit
- Archived TcUnit-Runner reference implementation — https://github.com/tcunit/TcUnit-Runner
- Reference ADS completion flow reviewed in TcKit — https://github.com/georgeturneruk/tckit/blob/4a4953f/dotnet/src/TcKit.Adapters.Ads/RuntimeOperations.cs
