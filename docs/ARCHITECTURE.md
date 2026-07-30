# Архитектура TwinCAT Agent Gateway

## 1. Контекст

AI-агент должен иметь возможность редактировать PLC-код в файлах, собирать TwinCAT solution, получать компактный список ошибок, активировать конфигурацию на отладочном стенде и читать результаты unit-тестов.

Автоматизация TwinCAT XAE строится поверх COM-интерфейсов Visual Studio DTE и TwinCAT Automation Interface. Эти интерфейсы stateful, чувствительны к apartment model, состоянию IDE, модальным операциям и жизненному циклу COM-объектов. Одноразовые shell-процессы плохо подходят для удержания такой сессии.

Поэтому основой системы является постоянно работающий desktop gateway. Агент
взаимодействует только с MCP; repository CLI остаётся development-клиентом.
Обычная MCP-операция не запускает desktop process автоматически.

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
                               │ Explorer-mediated start
                               │ + versioned local IPC
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
                                       System/PLC ReadState +
                                       fixed TcUnit symbols only

┌─────────────────────────────┐
│ TwinCatGateway.Cli          │ .NET 8, repository development only
│ thin IPC client             │ не устанавливается глобально
└────────────┬────────────────┘
             └──────────────── тот же local IPC
```

### 4.1 Поставка и lifecycle процессов

Per-user установка содержит два независимых приложения:

- `twincat-gateway` — существующий WPF/.NET Framework 4.8 x86 desktop host;
- `twincat-gateway-mcp` — существующий .NET 8 stdio adapter.

Оба доступны через стабильный user-PATH каталог. `dotnet tool` и
универсальный .NET 8 command host не используются.

`twincat-gateway-mcp` использует `System.CommandLine` как единственный источник
root option definitions, defaults, parsing и generated `--help`/`-h`/
`--version`. Help/version завершаются до создания Generic Host. В обычном
server mode stdout остаётся только MCP transport; логи идут в stderr. У MVP
нет console subcommands, а все будущие `Command` автоматически получают
`<subcommand> --help` из той же command model.

Configured gateway имеет один per-user singleton. После захвата mutex desktop
host атомарно публикует в
`%LOCALAPPDATA%\TwinCatAgentGateway\gateway-instance.json` PID, process start
time, pipe, нормализованные config/solution paths, profile, launch source и
effective UI mode. Record не является публичным control API: MCP проверяет PID
и start time, затем подтверждает identity через versioned IPC `status`.
Регистрация удаляется только владеющим instance id, поэтому завершающаяся
старая сессия не может удалить record новой.

Ручной запуск:

```text
twincat-gateway [--config <path>] [--ui-mode auto|window|tray]
```

Если при ручном запуске config не найден и не был явно передан, приложение
открывает отдельный setup-only UI с версией и встроенной справкой. Этот процесс
использует отдельный setup mutex, не запускает gateway host, не открывает Named
Pipe, не публикует instance record и не блокирует запуск configured gateway.
Явный отсутствующий `--config` и agent launch без config остаются
`GATEWAY_CONFIG_NOT_FOUND`.

Agent launch возможен только через MCP tool `gateway_start`. Он:

1. находит и валидирует точный project config;
2. проверяет `agentProcessControl.allowStart`;
3. возвращает успех для уже готового gateway с тем же config/solution;
4. возвращает `GATEWAY_RUNNING_DIFFERENT_PROJECT` для другого singleton, не
   закрывая и не переключая его;
5. передаёт не более одного запуска
   `twincat-gateway --config <absolute> --launch-source agent` в desktop view
   интерактивного Windows Explorer;
6. bounded-wait ожидает IPC и сверяет status identity/ready.

MCP не создаёт desktop gateway как собственный дочерний процесс. Explorer
выполняет `ShellExecute` из своего процесса, поэтому gateway получает обычный
environment block и integrity context интерактивного пользователя, а не
добавленные агентом переменные. Обычный `Process.Start` и прямой fallback
запрещены. Если desktop view Explorer недоступен, `gateway_start` возвращает
`GATEWAY_INTERACTIVE_LAUNCH_UNAVAILABLE`; пользователь может запустить
gateway вручную.

Обычные tools при отсутствии процесса возвращают `GATEWAY_NOT_RUNNING`.
Завершение MCP не закрывает desktop gateway. Отдельный destructive tool
`gateway_shutdown` проверяет загруженный
`agentProcessControl.allowShutdown`. При `false` он возвращает
`GATEWAY_SHUTDOWN_DISABLED`. При `true` desktop gateway сначала записывает
успешный IPC response, затем через completion callback инициирует WPF
shutdown. Это не закрывает XAE instance, открытый пользователем; обычные
правила gateway-owned XAE cleanup сохраняются. XAE, который был запущен
gateway для текущего profile, сохраняет ownership marker на весь lifecycle и
закрывается при `gateway_shutdown`; повторное ROT attach к тому же PID не
должно ошибочно превращать его в user-owned instance.

### 4.2 Project-local configuration

Основное имя — `twincat-gateway.json`. Относительные `solution`,
`logDirectory` и TcUnit `reportPath` разрешаются относительно каталога config.
Порядок discovery одинаков для manual и MCP:

1. явный `--config`;
2. workspace roots, полученные MCP от клиента;
3. current working directory как fallback;
4. ближайший файл вверх, включая Git root, но не выше него; вне Git — до
   корня диска.

Разные config из нескольких workspace roots дают
`GATEWAY_CONFIG_AMBIGUOUS`. Отсутствие даёт
`GATEWAY_CONFIG_NOT_FOUND`, кроме setup-only ручного запуска, описанного выше.
`appsettings.Local.json` не ищется автоматически и принимается только как
явный `--config`.

Полный нормализованный config path включён в локальный status contract как
безопасное диагностическое identity: config не содержит секретов по контракту,
ответ не включает его содержимое, PLC source или большие данные. Status также
возвращает active profile, configured solution path, `manual|agent`,
effective `window|tray` и `ready`.

## 5. Почему desktop gateway

Gateway должен работать в интерактивной пользовательской сессии, потому что:

- XAE является desktop IDE;
- часть проблем требует визуальной проверки человеком;
- пользователь должен видеть выбранный solution и target;
- Windows Service усложняет COM, desktop interaction и session isolation;
- gateway может показывать блокирующие состояния, logs и safety prompts.

Для agent launch интерактивный контекст является проверяемой process boundary:
Desktop gateway запускается через Explorer того же пользователя и session,
после чего сам напрямую запускает XAE. XAE наследует environment gateway, а
gateway сохраняет точный XAE PID для ROT/ProgID selection. Очистка только
`PATH` перед запуском XAE не заменяет эту boundary, потому что не исправляет
session или integrity mismatch.

Gateway не обязан всегда отображать главное окно. В `auto` ручной запуск
показывает окно, agent launch начинает в tray. Явный `window`/`tray` имеет
приоритет над config; config имеет приоритет над `auto`. При скрытом окне
обязательно остаётся tray icon, поэтому gateway не работает полностью
невидимо.

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
2. единственный вызов DTE-команды `TwinCAT.ActivateConfiguration`;
3. детерминированная обработка platform, activation/autostart и Run dialogs;
4. встроенные в XAE build и deployment выбранной конфигурации;
5. ожидание подтверждённого ADS-состояния `Run` при
   `runAfterActivation=true`; при `false` финальный Run dialog отменяется, а
   фактическое runtime state только наблюдается и не считается
   подтверждением новой активной конфигурации;
6. повторная проверка solution и AMS NetId.

Команда XAE уже включает предложение перехода в Run. Поэтому activation
никогда не вызывает `ITcSysManager.StartRestartTwinCAT()` после
`TwinCAT.ActivateConfiguration`: это создавало бы второй переход и могло
запустить ранее активную конфигурацию даже после Cancel в XAE dialog.
Gateway не меняет tri-state `Autostart PLC Boot Project(s)`; значение,
настроенное пользователем для PLC projects, только читается для diagnostics.

Перед каждой изменяющей COM/DTE-командой XAE boundary повторно проверяет точный
`Solution.FullName` и AMS NetId. Необязательное имя target в этих проверках не
участвует, но при наличии записывается в operation log и structured events.

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

ADS adapters не принимают от вызывающего кода произвольные NetId, port или
symbol path, не пишут значения, не вызывают RPC и не меняют runtime state.
Status adapter вызывает `TryReadState` для фиксированного System Service port
10000 и PLC runtime ports, обнаруженных из точного выбранного `.tsproj`.
Activation/restart остаются обязанностью `ActivationService` через Automation
Interface.

Стандартные symbol paths задаются operator-controlled profile и проверяются на закреплённой версии TcUnit. Они не считаются стабильным публичным API TcUnit и не передаются произвольными аргументами MCP/CLI.

Если unit-тесты запускаются автоматически вместе с boot project, test operation может быть связана с activation operation.

При эффективном `waitForTcUnit=true` gateway снимает baseline отчёта
непосредственно перед activation. После успешного Run postcondition он
ставит отдельную `OperationKind.Test` в ту же последовательную очередь и
возвращает её ID в `ActivationResult.testOperationId`. Activation не
становится неуспешной из-за последующего test failure: физическое применение
конфигурации и результат тестов остаются двумя явно связанными операциями.

### 7.8 ProjectChangeClassifier

Определяет шумовые изменения `.tsproj` без их исправления.

Результат используется только для отчёта агенту и UI.

### 7.9 OperationStore и LogStore

OperationStore хранит structured metadata. LogStore хранит большие raw artifacts.

Минимальные artifacts:

- build output;
- XAE activity/diagnostic log, если доступен;
- Error List snapshot/delta;
- retained gateway event stream slice for the operation;
- TcUnit xUnit XML;
- summary `.tsproj` noise.

Desktop-компоненты пишут через `Microsoft.Extensions.Logging.ILogger<T>`.
Serilog является файловым provider и сериализует события
`CompactJsonFormatter`, сохраняя `EventName`, `OperationId`, `SourceContext`,
structured properties, Unicode и полный exception/stack trace.

Каждый запуск создаёт отдельный набор
`gateway-<UTC timestamp>-p<PID>.ndjson`. File sink использует
`RollingInterval.Infinite`, rollover по настраиваемому размеру и ограничение
числа сегментов одного запуска. `FileLifecycleHooks.OnFileOpened` обновляет
потокобезопасный tracker при первом открытии и каждом rollover. Age retention
обрабатывает только строгие session-имена, legacy `gateway.ndjson` и operation
directories; активный session-набор и посторонние файлы сохраняются.

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

`Faulted` не означает обязательное завершение процесса. Gateway может принять
`reconnect` или автоматически восстановить session по ограниченной политике.
Если `VerifyAttached` получает transient `COM_CALL_TIMEOUT`,
`COM_CALL_REJECTED`, `RPC_E_CALL_REJECTED` или
`RPC_E_SERVERCALL_RETRYLATER`, coordinator сохраняет выбранную exact XAE
identity и attach-scoped guard, публикует `Faulted` с `xae.connected=true` и
повторяет `VerifyAttached` без промежуточного `Attaching`. Session
освобождается и reconnect начинается только после доказанного исчезновения
ROT/process, `SOLUTION_MISMATCH` или явного запроса reconnect.

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
  "discardDirtyDocuments": false,
  "detail": "compact"
}
```

Явно переданные `configuration`/`platform` имеют приоритет над profile.
Если значение отсутствует и в запросе, и в profile, сохраняется активный выбор
solution. Пустая строка в явном параметре считается ошибкой запроса.

Перед Build/Clean/Rebuild gateway выбирает типизированный
`EnvDTE.SolutionConfiguration` и вызывает `Activate()`, когда текущий выбор не
совпадает с запрошенным. В EnvDTE нет отдельного типизированного solution-level
переключателя platform, поэтому platform используется для точного выбора среди
одноимённых solution configurations и затем проверяется по
`SolutionContext.PlatformName`. В MVP допустим только один уникальный platform
во всех активных project contexts. Отсутствующая пара
configuration/platform отклоняется как `BUILD_CONFIGURATION_NOT_FOUND`, а
несколько подходящих configurations или смешанный набор активных platforms —
как `BUILD_CONFIGURATION_AMBIGUOUS`; gateway не выбирает один вариант
эвристически.

`changedPaths` необязателен и является только hint. Авторитетное сравнение
всегда выполняется по точному graph выбранного TwinCAT project:
`.tsproj` -> `PrjFilePath` -> `.plcproj` -> `Compile Include`. Относительные
ссылки разрешаются от содержащего их project file, включая ссылки вне
каталога `.sln`. Соседние файлы, не входящие в graph, не сканируются; hint вне
graph отклоняется как `REQUEST_INVALID`.

Baseline подтверждает именно состояние, реально загруженное в XAE. Attach к
существующему пользовательскому XAE устанавливает `syncRequired`, а не
принимает текущий диск автоматически. Gateway-launched XAE может подтвердить
baseline после полного проверенного открытия. Force sync без baseline всегда
перезагружает выбранный `.tsproj`.

`externalChangePolicy`:

| Policy | Modified PLC source | Add/remove | Project metadata |
|---|---|---|---|
| `reloadAll` | typed file reload | разрешено после проверки нового graph; selected TwinCAT project reload | selected TwinCAT project reload |
| `reloadModified` (default) | typed file reload | error | error |
| `error` | error | error | error |

Одинаковый SHA не является изменением. Schema-valid whitespace/reorder
по-прежнему может классифицироваться для компактного отчёта, но classifier не
является safety gate для изменений, записанных самой XAE. После pre-action
синхронизации gateway открывает ограниченное operation window вокруг
Build/Clean/Rebuild или `TwinCAT.ActivateConfiguration`. Любое изменение
файла выбранного project graph между начальным и стабильным конечным
fingerprint принимается как XAE-owned и становится новым baseline.
Предсуществующий reorder, изменение `ProgramVersion` в `.plcproj` и другие
правки до открытия operation window policy не обходят.

Operation window не различает запись XAE и параллельную запись пользователя
на уровне filesystem identity. Поэтому пользователь отвечает за отсутствие
ручных/внешних edits во время изменяющей gateway operation. Gateway сообщает
в local structured log каждый принятый path, kind и graph role, но не
записывает содержимое файла.

### 10.2 Последовательность

1. Валидация profile, solution и action.
2. Read-only ADS preflight текущего System Service state. Если доступный
   runtime находится в `Exception`, не вызывать XAE Build/Clean/Rebuild и
   вернуть `BUILD_BLOCKED_BY_RUNTIME_EXCEPTION` с требованием отдельной
   операции `recover-to-config`. Gateway не восстанавливает runtime
   автоматически и не уничтожает полезные для отладки артефакты предыдущей
   компиляции.
3. Построение и fingerprint scan точного выбранного project graph.
4. Сравнение с confirmed baseline; `changedPaths` проверяется как hint внутри
   graph, но не заменяет scan.
5. Применение `externalChangePolicy` и выбор минимального reload scope:
   source documents либо selected TwinCAT project.
6. Проверка dirty XAE documents. По умолчанию возвращается
   `DIRTY_XAE_DOCUMENT`; gateway никогда не сохраняет buffer. Discard возможен
   только при `discardDirtyDocuments=true` и
   `allowDirtyDocumentDiscard=true`.
7. XSD preflight изменённых PLC objects.
8. Типизированный reload изменённых PLC source документов через VSSDK Running Document
   Table и `IVsPersistDocData.ReloadDocData(...)`.
9. Для structural/metadata changes — проверка candidate graph, reload
   выбранного TwinCAT project через `IVsSolution4.ReloadProject`, повторное
   получение COM objects и проверка точной identity.
10. Повторный fingerprint scan после reload; concurrent change завершает
   retryable error. Baseline обновляется только после postconditions.
11. SHA-256 snapshot всех выбранных solution `.tsproj`, включая проекты вне
   solution root; attach-scoped guard продолжает подавлять их file-change
   notifications.
12. Выбор и проверка configuration/platform.
13. Snapshot текущих Output позиций.
14. Подписка/проверка `BuildEvents`.
15. Запуск Build/Clean через `SolutionBuild`; Rebuild через
    `DTE.ExecuteCommand("Build.RebuildSolution")`.
16. Ожидание точного `OnBuildDone` action/scope и проверка `BuildState`.
17. Проверка `.tsproj` hashes и `SyncFile(...)` при продолжающем действовать
    attach-scoped guard.
18. После terminal build event ожидание 500 ms тишины по
    `FileSystemWatcher`. Новое событие перезапускает quiet period.
19. Повторный авторитетный project-graph fingerprint scan. Watcher служит
    только сигналом стабилизации; coalesced/missed events и buffer overflow не
    заменяют scan. При overflow результат scan принимается, а overflow
    записывается как warning.
20. Все graph changes в operation window принимаются как XAE-owned,
    логируются и становятся confirmed baseline.
21. Чтение `LastBuildInfo`, Error List snapshot и Output delta.
22. Нормализация diagnostics и классификация `.tsproj` changes для отчёта.
23. Сохранение полного Output delta как отдельного build-log resource.
24. Возврат compact result.

Если terminal outcome не установлен из-за timeout, cancellation, COM loss или
неизвестного modal dialog, gateway не подтверждает новый baseline и переводит
workspace в `syncRequired`. Известный обработанный terminal failure, например
отменённый platform-mismatch dialog или подтверждённый fatal-error dialog,
завершает operation window тем же quiet/fingerprint шагом, после чего исходная
ошибка операции возвращается вызывающему коду.

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

Gateway намеренно использует DTE command identity
`TwinCAT.ActivateConfiguration`, то есть тот же workflow, что кнопка
**Activate Configuration** в XAE. На TwinCAT 3.1.4024.17 он последовательно
выполняет platform check, confirmation с tri-state
`Autostart PLC Boot Project(s)`, build, перенос configuration/boot artifacts
на target и отдельный вопрос о переходе в Run.

Метод Automation Interface `ITcSysManager.ActivateConfiguration()` имеет
более узкую и другую семантику: в проверенном platform-mismatch scenario он
не показал UI platform check и применил конфигурацию принудительно. Поэтому
для agent-facing activation он не используется.

### 11.2 Последовательность

1. Проверить `allowActivation` profile.
2. Проверить выбранные solution и AMS NetId.
3. Проверить, что gateway не выполняет build.
4. Если continuous monitor уже подтверждает `Exception`, вернуть
   `RUNTIME_RECOVERY_REQUIRED` до recent-build policy: диагностически
   заблокированная build operation не должна маскировать необходимость
   явного recovery.
5. Проверить policy актуальности последней успешной сборки.
6. Снять baseline XAE Error List и прочитать runtime state через read-only ADS
   `TryReadState` на System Service port 10000; `unknown` завершает операцию до
   изменения состояния.
7. Если runtime находится в `Exception`, завершить activation с
   `RUNTIME_RECOVERY_REQUIRED`. Не выполнять скрытый `RecoverToConfig`:
   пользователь или агент должен отдельно запросить явный переход в Config.
8. При `autoSynchronizeBeforeOperation=true` выполнить тот же authoritative
   fingerprint scan и policy-controlled typed reload, что и перед build.
   Dirty XAE documents и изменения, запрещённые `externalChangePolicy`,
   завершают operation до вызова activation command.
9. Повторно проверить solution и AMS NetId и один раз вызвать
   `DTE2.ExecuteCommand("TwinCAT.ActivateConfiguration")` при выключенном
   Silent Mode.
10. Обработать только известную последовательность dialogs точного XAE PID:
   - platform mismatch: нажать Cancel и вернуть подробную ошибку;
   - `Activate Configuration`: прочитать tri-state Autostart, не менять его
     и нажать OK;
   - `Restart TwinCAT System in Run Mode`: нажать OK при
     `runAfterActivation=true`, иначе Cancel;
   - fatal dialog: безопасно закрыть и вернуть ошибку;
   - неизвестный dialog: ничего не подтверждать и завершить operation
     fail-closed.
11. Не выполнять отдельный `StartRestartTwinCAT()`. При
   `runAfterActivation=true` наблюдать переход, не считая сохранённое до
   команды состояние `Run` доказательством нового запуска. При `false`
   отменить только финальный запрос перехода в Run, не выполнять
   принудительный переход в Config и вернуть фактически наблюдаемое runtime
   state вместе с `activeConfigurationVerified=false`. Состояние `Exception`
   является немедленным terminal failure, а не причиной ждать общий timeout.
12. При `runAfterActivation=true` определить через XAE PLC projects с
    `BootProjectAutostart=true` и проверить
    online state каждого такого PLC. Успех требует стабильного `Run` System
    Service и отсутствия `Exception` у всех обязательных Auto Boot PLC.
13. Прочитать дельту XAE Error List и `GetLastErrorMessages()`. Runtime
    exception/page fault и связанные ошибки портов являются fatal; warnings
    возвращаются в diagnostics, но сами по себе не делают activation
    неуспешной.
14. Повторно проверить solution и AMS NetId и обновить XAE/runtime diagnostics.
15. Записать activation resource и stage events в общую event stream.
16. Если это включено profile, запустить связанную test operation: дождаться ADS completion signal и затем свежего TcUnit report.

Gateway не вызывает `SaveAll` перед activation: при agent-owned workspace
источником истины являются внешние файлы. По умолчанию они сверяются по
fingerprint и синхронизируются typed reload непосредственно перед каждой
Build/Rebuild/Clean или activation operation. Standalone Build и activation
остаются разными явными операциями: Build никогда не запускает activation.
При этом сама XAE-команда `TwinCAT.ActivateConfiguration` выполняет внутреннюю
сборку как обязательную часть UI activation workflow; её diagnostics входят
в activation result.

Error List нельзя трактовать как простой счётчик. Gateway сравнивает снимки до
и после activation и классифицирует новые строки. Например, TcUnit может
публиковать успешные итоговые строки (`Tests`, `Successful tests`,
`TESTS FINISHED RUNNING`) с XAE severity `Error`; такие строки сохраняются в
диагностике, но не подменяют runtime fault. Наоборот, строка с
`Exception Code`, `Page Fault`, PLC instance и ADS port должна попадать в
compact activation error даже когда `ITcSysManager2.GetLastErrorMessages()`
возвращает пустой список.

Silent Mode для activation намеренно выключен: скрытые default choices
оказались неэквивалентны безопасной пользовательской последовательности.
Выделенный MTA UI Automation supervisor живёт вместе с выбранной XAE session
и подписан на `WindowPattern.WindowOpenedEvent` во время любых gateway-owned
XAE operations, включая open solution, synchronization, Build/Rebuild/Clean и
activation. Он фильтрует окна по точному XAE process id, классу Win32
`#32770` и modal state. Поскольку UIA event может быть пропущен или дерево
Desktop недоступно в несовпадающем интерактивном контексте, supervisor
дополнительно сверяет top-level HWND через Win32 `EnumWindows`, после чего
читает controls через UI Automation.

Каждый обнаруженный modal dialog, включая неизвестный, записывается в
structured log и общую event stream: сохраняются заголовок, нормализованный
английский content signature, stage, controls и выбранное действие. Известные
dialogs распознаются по частичному английскому content, а кнопки выбираются по
стандартным numeric dialog control IDs, не по локализованным надписям. Если
неизвестный dialog имеет `IDCANCEL`, gateway отменяет его и завершает operation
с `XAE_UNKNOWN_MODAL_DIALOG`. Без безопасной Cancel-кнопки окно остаётся
открытым, а последующие операции блокируются до вмешательства пользователя.
Gateway никогда не переключает Autostart checkbox автоматически.

Если modal dialog появляется после завершения одной operation и до начала
следующей, supervisor только наблюдает его и не изменяет idle XAE. Следующая
явно запрошенная operation повторно классифицирует уже открытый dialog до
выполнения XAE-команды. Известный `Target system reports a fatal error`
закрывается только кнопкой `OK`, его полный текст сохраняется в diagnostics,
а operation завершается `XAE_DIALOG_REPORTED_FAILURE`. Неизвестный dialog
остаётся fail-closed и не подтверждается автоматически.

Отдельно распознаётся close-time dialog `Closing project failed! Visual
Studio will restart now.`. Gateway сохраняет его content, завершает cleanup
как `XAE_DIALOG_REPORTED_FAILURE` и не нажимает `OK`: заявленный dialog
перезапуск XAE нельзя автоматически считать безопасным. На стенде
`WIN-T077ADA` после одного ручного подтверждения restart фактически не
произошёл, но это наблюдение не подтверждено на других XAE/runtime и не
является гарантией. Оператор вручную закрывает XAE, а внешний session probe
подтверждает исчезновение process и ROT.

Если TcUnit executor или profile отсутствует, запрос с эффективным
`waitForTcUnit=true` отклоняется до первой изменяющей команды. Такая же
ошибка возвращается для сочетания `waitForTcUnit=true` и
`runAfterActivation=false`: тесты не могут выполняться в Config Mode.

### 11.3 Recovery to Config

Automation Interface предоставляет `StartRestartTwinCAT()` для Run, но не
предоставляет отдельного метода `StartRestartTwinCATInConfigMode` в базовом
`ITcSysManager`. На TwinCAT 3.1.4024.17 gateway использует зарегистрированную
не локализованную DTE command identity
`TwinCAT.RestartTwinCATConfigMode`.

Recovery выполняется только по отдельному явному запросу на том же STA через
типизированный `DTE2.ExecuteCommand`,
в Silent Mode и только после повторной проверки solution/AMS NetId.
Возврат команды сам по себе не считается успехом: gateway опрашивает
read-only ADS System Service port 10000 до состояния `Config`, cancellation
или общего activation deadline. Невыполнение postcondition возвращает
`CONFIG_MODE_RECOVERY_FAILED`, а не ложный success.

Recovery является самостоятельной serial operation и доступна через
MCP/CLI как `twincat_recover_to_config`, а не только как внутренний шаг
activation. Она использует тот же allow-listed profile, target verification,
deadline и audit trail; произвольный ADS state control не добавляется.
Build и activation никогда не запускают recovery автоматически.
Recovery не требует recent successful build: в состоянии `Exception` XAE
может успешно собрать отдельные PLC projects, но завершить solution build
ошибкой на верхнем TwinCAT system project. Требование сначала обновить build
создало бы неразрешимый цикл `recovery requires build` / `build requires
recovery`.

На выделенном стенде `WIN-T077ADA` наблюдалось дополнительное ограничение:
после нескольких повторных переходов в `Exception` TwinCAT иногда перестаёт
завершать автоматический переход в `Config`. Это наблюдение не подтверждено
на других runtime и не считается общей семантикой TwinCAT. Timeout или ошибка
`recover-to-config` в таком состоянии немедленно останавливает pipeline без
автоматического повтора. Оператор должен войти в удалённую систему и вручную
перевести runtime в `Config`, после чего gateway обязан заново подтвердить
состояние через read-only ADS.

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

AMS NetId является авторитетной identity target и перед activation должен точно
совпасть между выбранным XAE target и profile. Target name необязателен,
не участвует в safety decision и используется только как display/audit
metadata. ADS completion adapter получает NetId только из target, выбранного и
проверенного XAE/profile; отдельный произвольный NetId от MCP/CLI запрещён.

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
  "eventStreamId": "887c1e5c1c8e4c889510cf4c612ce5bb",
  "latestEventCursor": 42
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
- список non-generated project-graph файлов, отличающихся от confirmed
  fingerprint baseline, с path/change kind/role;
- raw ADS state и device state с System Service port 10000;
- `GetLastErrorMessages()`;
- последний HRESULT;
- COM retry counts и latency;
- retained gateway events;
- ссылки на raw logs;
- build diagnostics;
- `.tsproj` noise classification;
- IPC/log-store health.

Gateway использует одну немутирующую ленту событий для lifecycle,
состояний и ошибок. Примеры типов: `gateway.started`, `xae.connected`,
`runtime.stateChanged`, `build.queued`, `build.started`,
`build.succeeded`, `build.failed`. Ошибка является терминальным событием с
`severity=error` и вложенным `error`, а не записью во втором журнале.

Cursor protocol:

- desktop process создаёт новый `eventStreamId`, а события внутри него
  получают монотонный числовой cursor;
- compact status возвращает `eventStreamId` и `latestEventCursor`;
- первый вызов использует
  `getDiagnostics(afterEventCursor=0, maximumEvents, minimumSeverity?)`;
- для продолжения клиент передаёт пару `eventStreamId` и
  `afterEventCursor`; cursor без stream ID недопустим;
- ответ содержит `events`, `nextScanCursor`, `latestEventCursor`,
  `moreMatchingEventsAvailable` и `eventHistoryTruncated`;
- чтение не меняет глобальное состояние: WPF, CLI, MCP и отдельные UI views
  хранят собственную пару stream/cursor;
- фильтр severity использует ту же систему координат. Если после страницы
  больше совпадений нет, `nextScanCursor` продвигается также через
  отфильтрованные события. Поэтому запрос только ошибок не перечитывает
  информационные события;
- `eventHistoryTruncated` означает retention gap, cursor из другого
  `eventStreamId` или cursor впереди текущей ленты. Клиент принимает
  возвращённые stream ID и scan cursor.

MVP хранит последние 1000 событий только в памяти desktop gateway.
Перезапуск gateway начинает новую ленту; CLI/MCP/WPF restart её не сбрасывает.
Долговременная подробная история остаётся в локальных structured logs, но
они не объявляются replayable event journal в MVP. Получение только ошибок
задаёт `minimumSeverity=error` и сканирует не более bounded retained window,
поэтому отдельный error index для MVP не нужен.

Когда нет активной изменяющей operation, XAE coordinator обновляет этот
список read-only fingerprint scan вместе с периодической проверкой точной
XAE-сессии. Ненулевой список переводит synchronization state в
`syncRequired`; совпадение с существующим confirmed baseline возвращает
`confirmed`. Если baseline ещё не подтверждён, список пуст и
`syncRequired` означает, что точное сравнение пока невозможно. Scan не
reload-ит документы, не сохраняет XAE buffers и не меняет runtime.

### 12.3 Runtime status

Gateway читает состояние выбранного target через `AdsClient.TryReadState` на фиксированном ADS System Service port 10000. NetId поступает только из типизированного `ITcSysManager2.GetTargetNetId()` и не задаётся MCP/CLI caller. Это отдельный узкий read-only adapter, а не general-purpose ADS surface.

Поле `mode` принимает:

```text
run | config | exception | stopped | unknown
```

`Run`, `Config/Reconfig`, `Stop/Stopping/Shutdown` и `Error/Exception` отображаются соответственно в `run`, `config`, `stopped` и `exception`. Переходные, неподдержанные состояния и ошибки ADS возвращают `unknown`. Detailed diagnostics сохраняет NetId, port, raw ADS state, device state, timestamp и error code. При подтверждённом `exception` gateway выбирает связанную строку `Exception Code`/`Page Fault` из текущего XAE Error List и сохраняет её в `RuntimeAlert.details`; та же строка попадает в `GatewayError.details`, когда runtime блокирует build или activation. Runtime status failure не делает исправную XAE-сессию disconnected.

### 12.4 Continuous ADS runtime monitoring

PLC `Exception` не является частью activation transaction. Активация может
успешно завершиться, после чего пользовательский код перейдёт в `Exception`
немедленно или в любой более поздний момент. Такой переход записывается как
отдельное runtime event и сам по себе не меняет уже завершённый результат
activation operation.

Когда System Service уже находится в `Exception`, это штатное
диагностическое состояние, а не повод для автоматического восстановления.
TwinCAT 3.1.4024.17 сохраняет предыдущие PLC build artifacts и может завершить
solution-level Build/Rebuild ошибкой, пока runtime остаётся в Exception.
Gateway поэтому останавливает новую Build/Clean/Rebuild до XAE-команды,
возвращает `BUILD_BLOCKED_BY_RUNTIME_EXCEPTION` и требует отдельный
`recover-to-config`. После подтверждённого `Config` та же сборка может быть
повторена явно.

Continuous monitor использует единый background polling:

- monitor работает независимо от XAE STA operation loop, поэтому продолжает
  наблюдение во время build/activation и при временной потере XAE session;
- после выбора и проверки target он удерживает долгоживущий `AdsClient` для
  каждого endpoint. NetId берётся из XAE, а PLC runtime ports обнаруживаются
  только из точного выбранного `.tsproj`; caller не может передать произвольный
  NetId или port;
- на каждом цикле сначала читается System Service port 10000. PLC ports
  опрашиваются только когда System Service находится в `Run` или `Exception`;
- `runtimeMonitoring.pollIntervalMilliseconds` и
  `runtimeMonitoring.readTimeoutMilliseconds` задают bounded период и timeout.
  Значения по умолчанию — 1000 ms и 500 ms;
- одинаковые observations coalesce. Только изменения System/PLC state,
  disconnect, reconnect и read failure записываются в retained gateway event
  stream с timestamp, NetId, port, предыдущим и новым состоянием;
- aggregate `mode` становится `exception`, если в `Exception` находится System
  Service или хотя бы один PLC runtime. Недоступный активный endpoint даёт
  `unknown`; в остальных случаях aggregate mode следует за System Service;
- сетевой disconnect является отдельным runtime-health event. `unknown`
  остаётся представлением недостоверного текущего состояния, а detailed
  diagnostics сохраняет endpoint, последнюю попытку и error code.

MVP намеренно не смешивает polling с `AdsStateChanged`: это событие недоступно
на System Service port 10000, а обнаружение сетевого disconnect всё равно
требует активного ADS-вызова. Единый цикл проще дедуплицировать, тестировать и
ограничивать по времени. Это внутренний обмен gateway с runtime; неизменившиеся
observations не создают MCP-запросов и не расходуют токены модели.

### 12.5 Доставка runtime events агенту

Gateway всегда сохраняет runtime transitions в существующей cursor-based
event stream, чтобы временно отсутствующий клиент мог дочитать события после
reconnect. Последний активный runtime alert также входит в `TwinCatStatus` и
в корень каждого последующего IPC/MCP response. Поэтому агент видит exception
или disconnect при следующем обычном обращении к gateway без обязательного
`twincat_watch_runtime` и без дополнительного tool call.

Transport notification нельзя считать гарантированным способом автономно
разбудить модель после завершения её turn. Для этого нужна поддержка delivery
и wakeup со стороны MCP host. Будущий `subscriptions/listen` может доставлять
best-effort runtime resource updates поверх существующего cursor replay, но не
заменяет retained event stream и не входит в текущий public contract.

## 13. Редактирование через файлы

### 13.1 Основной workflow

```text
agent edits files
    -> build(changedPaths?) or explicit sync
    -> gateway scans the exact selected project graph
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

### 13.2 Confirmed disk baseline

Gateway не объявляет agent ownership над XAE buffers. Он хранит подтверждённый
SHA-256 manifest точного выбранного project graph. По умолчанию
`assumeAttachedXaeSynchronized=true`: при attach к существующему exact XAE
session без dirty project documents gateway принимает текущий disk graph как
начальный baseline без reload. Это осознанно переносит на пользователя
ответственность за то, что in-memory XAE project model не stale.

При `assumeAttachedXaeSynchronized=false` attach к существующему XAE даёт
`syncRequired`; только реальная reload operation с проверенными postconditions
может установить `confirmed`. Dirty documents никогда не попадают под
assumption и остаются конфликтом. `changedPaths` остаётся необязательным hint.

Dirty XAE buffers всегда имеют приоритет как конфликт: build/sync возвращает
`DIRTY_XAE_DOCUMENT`, никогда не вызывает Save/SaveAll и не закрывает document
автоматически. `vsSaveChangesNo` разрешён только при явном
`discardDirtyDocuments=true` и profile
`allowDirtyDocumentDiscard=true`; результат сообщает paths/count.

После проверки exact `Solution.FullName` и построения выбранного project graph
gateway до публикации `Ready` захватывает attach-scoped file-change guard:

- `IVsFileChangeEx.IgnoreFile(...)` подавляет project-level notifications для
  файлов точного graph, кроме generated `.tmc`;
- `IVsDocDataFileChangeControl.IgnoreFileChanges(...)` подавляет
  editor-level notifications для открытых PLC documents;
- Running Document Table sink включает editor-level защиту для документов,
  открытых после attach;
- path вне точного graph не получает защиту;
- частичный acquire полностью откатывается и возвращает стабильную ошибку
  `XAE_FILE_CHANGE_GUARD_FAILED`;
- disconnect/Dispose отписывает RDT sink и восстанавливает notifications в
  обратном порядке в `finally`.

`xae.agentWorkspaceOwned=true` означает только владение file-change
notifications и synchronization lifecycle выбранного graph. Это не ownership
пользовательских buffers и не разрешение сохранять или отбрасывать их.

Guard действует всё время attach, поэтому обычные внешние изменения выбранного
graph, включая ручные, не вызывают XAE file-modification dialog. До следующего
sync/build открытый editor может показывать старое сохранённое содержимое;
источником истины остаётся диск. Изменение всё равно обнаруживается
authoritative fingerprint scan, проверяется `externalChangePolicy` и
типизированно синхронизируется перед операцией. `twincat_sync` остаётся
принудительным полным synchronization workflow.

Проверка на TwinCAT 3.1.4024.17 уточнила механизм reload:

- после обычного внешнего edit открытого сохранённого `.TcPOU`
  `EnvDTE.Document.Saved` остаётся `true`; attach-scoped guard предотвращает
  project-level и editor-level reload dialogs;
- внешний edit открытого несохранённого document остаётся конфликтом:
  fingerprint scan видит disk change, но build/sync возвращает
  `DIRTY_XAE_DOCUMENT` без save/discard;
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
  версию до reload, поэтому используется только как явно разрешённое
  destructive действие.

Перед build gateway, не снимая attach-scoped guard, проверяет dirty state,
временно открывает каждый изменённый закрытый document, выполняет validation,
typed `ReloadDocData`, `SyncFile` и снова закрывает editor. Вложенный
build-specific lease использует общий guard и не включает notifications
преждевременно. Изменения, обнаруженные до gateway-owned operation window,
по-прежнему проходят `externalChangePolicy`. После запуска
Build/Clean/Rebuild или activation сама XAE считается автором записей
выбранного project graph: gateway ждёт 500 ms тишины, выполняет итоговый
fingerprint scan, логирует и принимает любые `.tsproj`, `.plcproj`, PLC source
и `.tmc` changes.

## 14. `.tsproj` reorder-only noise

### 14.0 XAE file watcher guard и operation settle

TwinCAT 3.1.4024.17 может во время обычной Build/Clean/Rebuild перезаписать
`.tsproj` теми же байтами, изменив только filesystem timestamp. XAE file
watcher без attach-scoped guard способен увидеть эту собственную запись и
показать modal
`File Modification Detected`; Silent Mode этого не предотвращает.

Attach-scoped guard уже удерживает `IVsFileChangeEx.IgnoreFile(...)` для
негенерируемых файлов выбранного graph. Поэтому build-specific lease не
переключает notification state для уже защищённых `.tsproj`, включая проекты
вне solution root. После `OnBuildDone`:

- если файл существует и hash совпадает, gateway вызывает `SyncFile(path)` при
  всё ещё подавленных notifications;
- если hash изменился, gateway запускает classifier для отчёта и вызывает
  `SyncFile(path)` при ещё подавленных notifications независимо от
  classification: запись находится внутри XAE-owned operation window;
- notification state восстанавливается только при завершении attach-scoped
  guard; исключение или Dispose используют тот же обратный cleanup.

Параллельно до запуска действия создаётся `FileSystemWatcher` для корней
выбранного project graph. Он наблюдает все filesystem events, но используется
только для 500 ms debounce. После quiet period gateway заново вычисляет
authoritative SHA-256 graph snapshot; если событие пришло во время scan,
quiet/scan повторяется. `InternalBufferOverflowException` не делает watcher
источником истины: overflow логируется, затем выполняется тот же полный scan.

Guard, watcher и classifier не перезаписывают файл и не скрывают факт
содержательного изменения: path/kind/role остаются в structured log, а Git
working tree не объявляется чистым. Проверенный
`IVsRunningDocumentTable5.HandsOffDocument/HandsOnDocument` для этой задачи
не используется: XAE Shell на базе Visual Studio 2019 в тестовой конфигурации
не зарегистрировал COM proxy этого интерфейса.

### 14.1 Требования

- ничего не перезаписывать;
- не вызывать reload ради cleanup;
- не скрывать и логировать содержательные изменения;
- не заставлять агента читать большой XML diff;
- не считать рабочее дерево чистым, если Git показывает изменение;
- принимать любые изменения project graph внутри XAE-owned operation window;
- не подтверждать baseline после неизвестного/незавершённого outcome.

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
contract. `TcModuleClass.xsd` не нужен для PLC `.tmc`: этот файл является
generated artifact и не используется как авторитетный source input.

### 14.3 PLC `.tmc` generated artifacts

PLC `.tmc` содержит описание типов и символов и автоматически
регенерируется при компиляции PLC project. Beckhoff допускает хранение файла
в source control, чтобы описание было доступно сразу после checkout, но
прямо запрещает merge PLC `.tmc`.

Gateway классифицирует как `expectedGeneratedArtifact` любой added, modified
или deleted `.tmc`, который явно принадлежит выбранному PLC project graph:

- путь указан через `TmcFilePath`/`TmcPath` в выбранном `.tsproj`; или
- файл включён в соответствующий `.plcproj`.

Такие изменения:

- никогда не создают external-edit/synchronization conflict;
- сохраняются на диске и в Git working tree;
- не откатываются, не форматируются и не сравниваются семантически;
- возвращаются в `expectedProjectNoise` с `doNotInspectFullFile=true`, если
  наблюдались во время tracked XAE operation.

Произвольный `.tmc`, не входящий в выбранный project graph, не получает это
исключение.

## 15. TcUnit с read-only ADS completion

MVP поддерживает ровно один назначенный TcUnit PLC на project profile.
Объект `tcUnit` содержит один ADS port, одну пару completion symbols и один
report path; он не является списком. Solution может содержать другие PLC,
но они не входят в результат этой test operation. Их xUnit publisher должен
быть выключен либо писать в другой файл: gateway не агрегирует несколько
PLC и не допускает нескольких writers для настроенного report path.

Предполагаемый workflow:

1. Агент исправляет код.
2. Gateway выполняет Build/Rebuild.
3. Агент явно вызывает Activate.
4. Gateway подтверждает Run dialog внутри `TwinCAT.ActivateConfiguration`;
   XAE запускает boot projects, для которых пользователь заранее включил
   Auto Boot.
5. Назначенная PLC task циклически выполняет отдельную test program, которая инстанцирует suites и вызывает `TcUnit.RUN()` или `TcUnit.RUN_IN_SEQUENCE()`.
6. Gateway подключается к тому же target по ADS на настроенный PLC port.
7. Gateway опрашивает `GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished` до `TRUE`, cancellation или deadline.
8. Gateway читает `GVL_TcUnit.NumberOfInitializedTestSuites`.
9. После ADS completion TcUnit публикует xUnit XML.
10. Gateway проверяет свежесть, стабильность и парсит XML.
11. Агент получает counts и failures.

Report transport — обычный read-only filesystem path, локальный или UNC,
заданный operator-controlled profile. Gateway не загружает файл через ADS и
не расширяет ADS surface до произвольного file access. Для remote runtime
каталог отчёта должен быть опубликован отдельным read-only share или иным
образом доступен desktop gateway под его Windows account.

Перед activation gateway сохраняет baseline report и удаляет старый файл только при явно разрешённом локальном report path. Минимальная проверка текущего запуска:

- связать test operation с конкретным successful activation с
  `runAfterActivation=true`;
- использовать NetId только из выбранного activation profile/XAE target;
- дождаться доступности двух фиксированных TcUnit symbols;
- получить `AllTestSuitesFinished=TRUE` в пределах deadline;
- сохранить ADS evidence и suite count в событиях test operation;
- дождаться нового изменения;
- дождаться стабильного размера;
- успешно распарсить XML;
- проверить наличие test suite/test case данных.

ADS completion является доказательством окончания выполнения, но не источником pass/fail. Авторитетный результат — свежий валидный xUnit XML текущей operation. Лучшее будущее улучшение — run identifier внутри test harness/report.

Test operation имеет собственные lifecycle events `tcunit.queued`,
`tcunit.started`, `tcunit.succeeded|failed|timedOut|cancelled`. Дополнительные
stage events `tcunit.completionObserved`, `tcunit.reportProduced` и
`tcunit.zeroTests` используют тот же общий event cursor. Missing ADS
route/port возвращает `TEST_ADS_UNAVAILABLE`, missing fixed symbol —
`TEST_COMPLETION_SYMBOL_UNAVAILABLE`, отсутствие completion —
`TEST_COMPLETION_TIMEOUT`, stale/missing report —
`TEST_REPORT_NOT_PRODUCED`, invalid stable XML —
`TEST_REPORT_INVALID`.

Требования к test project:

- TcUnit library version закреплена и доступна в library repository стенда;
- profile однозначно назначает один test PLC через его ADS port;
- test program назначена PLC task и не зависит от production I/O;
- `GVL_Param_TcUnit.xUnitEnablePublish=TRUE`;
- `GVL_Param_TcUnit.xUnitFilePath` указывает на разрешённый и доступный gateway path;
- другие PLC в той же solution не публикуют в этот report path;
- tests, которые используют `TEST_FINISHED()`, могут занимать несколько PLC cycles, поэтому fixed delay не заменяет completion signal;
- для separate test solution production code подключается как library/source reference, а activation разрешена только test profile.

## 16. MCP и token economy

### Tools

```text
gateway_start
gateway_shutdown
twincat_status
twincat_build
twincat_sync
twincat_activate
twincat_recover_to_config
twincat_get_diagnostics
twincat_get_xae_messages
twincat_get_test_results
```

`twincat_get_xae_messages(maximumMessages=50)` является read-only bounded
снимком текущих error/warning строк XAE Error List. Gateway сначала проверяет
точное выбранное `Solution.FullName`; caller не передаёт произвольную XAE
session. Ответ содержит solution, timestamp, общие counts, не более 200 строк
и `moreMessages`.

### Resources

```text
twincat-log://<operation-id>/build
twincat-log://<operation-id>/xae
twincat-test://<operation-id>/xunit
twincat-diff://<operation-id>/project-noise
twincat-doc://setup
twincat-doc://configuration
twincat-log://gateway/current
```

Tool result должен быть достаточен для обычного исправления compile error.
Operation resource читается только для нестандартной диагностики. Статические
`twincat-doc` resources доступны без запущенного desktop gateway и возвращают
установленные канонические setup instructions и configuration reference.
Фиксированный `twincat-log://gateway/current` проходит через существующий
bounded `getResource` IPC и возвращает только абсолютный путь из live
file-lifecycle tracker. Он не читает файл и не вычисляет путь из config.

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
- кнопка `Setup instructions`, читающая тот же канонический файл, который
  печатает installer.
- product version в configured и setup-only окнах.
- setup-only окно при ручном запуске без обнаруженной конфигурации; оно не
  является gateway process и не предоставляет IPC.

UI не должен содержать отдельную реализацию операций; он вызывает тот же application service, что IPC.

## 18. Ошибки

Примеры error codes:

```text
GATEWAY_NOT_RUNNING
GATEWAY_NOT_READY
GATEWAY_CONFIG_NOT_FOUND
GATEWAY_CONFIG_AMBIGUOUS
GATEWAY_START_DISABLED
GATEWAY_START_FAILED
GATEWAY_INTERACTIVE_LAUNCH_UNAVAILABLE
GATEWAY_START_TIMEOUT
GATEWAY_RUNNING_DIFFERENT_PROJECT
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
- ADS разрешён только для `ReadState` на фиксированном System Service port
  10000, `ReadState` на PLC runtime ports из точного выбранного `.tsproj` и
  чтения фиксированных TcUnit completion symbols на target, выбранном и
  проверенном через XAE/profile. Произвольные NetId, caller-selected ports,
  symbol paths, ADS writes, RPC и `WriteControl` не входят в gateway API.
- Profile задаётся локальной конфигурацией, а не произвольными аргументами агента.
- Solution/target выводятся перед activation в UI и operation log.
- MCP не получает произвольный COM invoke tool.
- Нет инструмента «выполнить DTE command по строке».
- Нет произвольного чтения файлов через MCP resource.
- Operation log resource принимает только существующий operationId и известный
  artifact kind. Единственный gateway-wide log resource имеет фиксированный
  URI `twincat-log://gateway/current` и возвращает только tracked path.

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

1. Поддержка structural sync для добавленных и удалённых PLC source files.
2. Полнота Error List по сравнению с Build Output на реальных PLC compile errors.
3. Точный lifecycle `BuildEvents` в нескольких открытых XAE instances.
4. Silent Mode и поведение confirmation dialogs при ошибочных activation paths на тестовом стенде.
5. Reconnect ADS client после restart и доступность TcUnit completion symbols на реальном стенде.
6. Закреплённая версия TcUnit, стабильность внутренних completion symbol paths и поведение `xUnitEnablePublish/xUnitFilePath` на 4024.17.

## 22. Источники

- Beckhoff: TwinCAT projects with AI-supported engineering — https://www.beckhoff.com/en-en/products/automation/twincat-projects-with-ai-supported-engineering/
- Beckhoff: TwinCAT Automation Interface — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242681355.html
- Beckhoff: ITcSysManager — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242753675.html
- Beckhoff: ActivateConfiguration — https://infosys.beckhoff.com/content/1031/tcautomationinterface/12425796491.html
- Beckhoff: StartRestartTwinCAT — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242762891.html
- Beckhoff: Command Activate configuration — https://infosys.beckhoff.com/content/1033/tc3_plc_intro/2953964811.html
- Beckhoff: Loading the program automatically — https://infosys.beckhoff.com/content/1033/tc3_plc_intro/8102877579.html
- Beckhoff: TwinCAT project files and generated PLC TMC — https://infosys.beckhoff.com/content/1033/tc3_sourcecontrol/406303499.html
- Beckhoff: Silent Mode — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/2489025803.html
- Beckhoff: AdsClient — https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407705867.html
- Beckhoff: RegisterAdsStateChangedAsync — https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407816459.html
- Beckhoff: AdsStateChanged event and unsupported port 10000 — https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407905547.html
- Beckhoff: ConnectionStateChanged polling example — https://infosys.beckhoff.com/content/1033/tc3_adsnetref/7312679051.html
- Beckhoff: AdsClient.TryReadState — https://infosys.beckhoff.com/content/1033/tc3_ads.net/9407838987.html
- Beckhoff: ADS System Service port 10000 — https://infosys.beckhoff.com/content/1033/tcadscommon/12439473419.html
- MCP: Subscriptions/listen pattern — https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/subscriptions
- Microsoft: IVsDocDataFileChangeControl — https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsdocdatafilechangecontrol
- Microsoft: IVsPersistDocData.ReloadDocData — https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivspersistdocdata.reloaddocdata
- Microsoft: IVsFileChangeEx — https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsfilechangeex
- Existing minimal build skill — https://github.com/IwanowS/codex-skill-twincat-build
- Reference all-in-one project — https://github.com/Lance0901/AI-TwinCAT-Skill
- TcUnit documentation — https://tcunit.org/
- TcUnit user guide and source — https://github.com/tcunit/TcUnit
- Archived TcUnit-Runner reference implementation — https://github.com/tcunit/TcUnit-Runner
- Reference ADS completion flow reviewed in TcKit — https://github.com/georgeturneruk/tckit/blob/4a4953f/dotnet/src/TcKit.Adapters.Ads/RuntimeOperations.cs
