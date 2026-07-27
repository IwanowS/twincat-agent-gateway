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
- не использовать ADS client в MVP;
- редактировать PLC-код через файлы;
- не исправлять автоматически генерируемый `.tsproj` noise;
- сделать опасные операции явными и ограниченными project profiles.

## 3. Не-цели MVP

- универсальная автоматизация всех функций TwinCAT;
- online variables и symbol browsing;
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
│ File Change Classifier ─ TcUnit Report Reader                 │
│                                                               │
│ ┌───────────────────────────────────────────────────────────┐ │
│ │ XAE COM Host                                               │ │
│ │ один STA thread + message pump + OLE IMessageFilter        │ │
│ │ DTE/DTE2 + ITcSysManager + BuildEvents + Error List        │ │
│ └───────────────────────────────────────────────────────────┘ │
└──────────────────────────────┬────────────────────────────────┘
                               │ COM
                      ┌────────▼────────┐
                      │ TwinCAT XAE     │
                      │ VS2019/XAE Shell│
                      └─────────────────┘

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
- Error List delta;
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

### 7.7 TcUnitReportService

В MVP не управляет PLC через ADS. Он:

- ожидает появление/изменение настроенного xUnit XML;
- проверяет timestamp и целостность;
- парсит report;
- возвращает counts и failed tests;
- хранит исходный XML как resource.

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
  "detail": "compact"
}
```

Configuration/platform берутся из profile или активного solution, если не заданы явно и это разрешено политикой.

### 10.2 Последовательность

1. Валидация profile и solution.
2. Проверка conflict между external file edits и unsaved XAE documents.
3. `SaveAll`, если политика допускает.
4. Snapshot текущих Error List/Output позиций.
5. Подписка/проверка `BuildEvents`.
6. Запуск Build/Rebuild/Clean.
7. Ожидание `OnBuildDone` и проверка `BuildState`.
8. Чтение `LastBuildInfo`.
9. Сбор новых Error List entries.
10. Сбор нового Output delta.
11. Нормализация diagnostics.
12. Классификация `.tsproj` changes.
13. Сохранение raw artifacts.
14. Возврат compact result.

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
10. Проверить `IsTwinCATStarted()`.
11. Прочитать `GetLastErrorMessages()` и XAE diagnostics.
12. Запустить ожидание свежего TcUnit report, если это включено profile.

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
tcUnitReportPath: C:\TwinCAT\3.1\Boot\tcunit_xunit_testresults.xml
```

Target identity следует показывать и сохранять в audit log. Даже без собственного ADS client доступный через Automation Interface target NetId может использоваться как дополнительная проверка.

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
- `IsTwinCATStarted()`;
- `GetLastErrorMessages()`;
- последний HRESULT;
- COM retry counts и latency;
- operation timeline;
- ссылки на raw logs;
- build diagnostics;
- `.tsproj` noise classification;
- IPC/log-store health.

### 12.3 Ограничение без ADS

`IsTwinCATStarted()` сообщает, запущена ли система, но не является полным runtime-state API. Поэтому exact `Run/Config/Exception` нельзя обещать без проверенного XAE-specific источника.

Поле `mode` принимает:

```text
run | config | exception | stopped | unknown
```

но gateway возвращает конкретное значение только при наличии надёжного подтверждения. Иначе — `unknown` плюс evidence в detailed diagnostics.

## 13. Редактирование через файлы

### 13.1 Основной workflow

```text
agent edits files
    -> gateway pre-build conflict check
    -> минимальный XAE refresh при необходимости
    -> build
    -> diagnostics
```

Преимущества:

- стандартный `git diff`;
- обычные patch-инструменты Codex;
- легко откатывать изменения;
- не нужно передавать PLC code через MCP;
- меньше COM surface.

### 13.2 Конфликт с XAE

Опасный сценарий:

1. файл открыт и изменён в XAE, но не сохранён;
2. агент меняет тот же файл на диске;
3. XAE позднее сохраняет старую in-memory версию.

Gateway должен детектировать это до build/save и вернуть:

```text
EXTERNAL_EDIT_CONFLICT
```

с перечнем файлов. Автоматический выбор версии запрещён.

## 14. `.tsproj` reorder-only noise

### 14.1 Требования

- ничего не перезаписывать;
- не вызывать reload ради cleanup;
- не скрывать содержательные изменения;
- не заставлять агента читать большой XML diff;
- не считать рабочее дерево чистым, если Git показывает изменение;
- явно помечать изменение как ожидаемый generated noise.

### 14.2 Предлагаемый классификатор

1. Найти изменённые `.tsproj` после XAE operation.
2. Получить baseline из pre-operation snapshot или Git base.
3. Безопасно распарсить оба XML.
4. Построить semantic representation.
5. Для известных XAE-контейнеров сравнить дочерние блоки как multiset по стабильному identity:
   - element type;
   - object Id/GUID;
   - name/path;
   - canonicalized content.
6. Проверить, что:
   - набор блоков одинаков;
   - содержимое каждого блока одинаково;
   - изменён только порядок;
   - вне разрешённых контейнеров нет изменений.
7. Вернуть `reorder-only` и количество перемещённых блоков.

Если identity неоднозначен или XML некорректен, результат — `unknown`, а не `reorder-only`.

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

## 15. TcUnit без ADS client

Предполагаемый workflow:

1. Агент исправляет код.
2. Gateway выполняет Build/Rebuild.
3. Агент явно вызывает Activate.
4. `StartRestartTwinCAT()` запускает boot project при включённом Auto Boot.
5. TcUnit выполняет тесты в PLC.
6. TcUnit публикует xUnit XML.
7. Gateway проверяет свежесть и парсит XML.
8. Агент получает counts и failures.

Необходимо избегать чтения старого отчёта как результата нового запуска. Минимальная проверка:

- сохранить baseline timestamp/size/hash до activation;
- дождаться нового изменения;
- дождаться стабильного размера;
- успешно распарсить XML;
- проверить наличие test suite/test case данных.

Лучшее будущее улучшение — run identifier внутри тестового harness/report, но оно не обязательно для первого MVP.

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
- отображение unresolved external edit conflicts.

UI не должен содержать отдельную реализацию операций; он вызывает тот же application service, что IPC.

## 18. Ошибки

Примеры error codes:

```text
GATEWAY_NOT_READY
XAE_NOT_FOUND
XAE_MULTIPLE_MATCHES
SOLUTION_NOT_FOUND
SOLUTION_MISMATCH
SYSMANAGER_NOT_AVAILABLE
COM_CALL_REJECTED
COM_CALL_TIMEOUT
BUILD_FAILED
BUILD_RESULT_INCONSISTENT
EXTERNAL_EDIT_CONFLICT
ACTIVATION_NOT_ALLOWED
CONFIG_MODE_REQUIRED
ACTIVATE_CONFIGURATION_FAILED
TWINCAT_RESTART_FAILED
TWINCAT_STATE_UNKNOWN
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
- TcUnit report wait duration.

## 21. Открытые технические вопросы

До фиксации реализации провести spikes:

1. Стабильный вызов `Restart TwinCAT (Config Mode)` в XAE 4024.17 без собственного ADS client.
2. Надёжный источник exact runtime mode без ADS; допустимый результат spike — подтверждение, что MVP показывает только `started/unknown`.
3. Поведение внешнего редактирования `.TcPOU/.TcGVL/.TcDUT` при открытых editors и варианты минимального refresh.
4. Полнота Error List по сравнению с Build Output на реальных PLC compile errors.
5. Точный lifecycle `BuildEvents` в нескольких открытых XAE instances.
6. Silent Mode и поведение confirmation dialogs при activation на тестовом стенде.

## 22. Источники

- Beckhoff: TwinCAT projects with AI-supported engineering — https://www.beckhoff.com/en-en/products/automation/twincat-projects-with-ai-supported-engineering/
- Beckhoff: TwinCAT Automation Interface — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242681355.html
- Beckhoff: ITcSysManager — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242753675.html
- Beckhoff: ActivateConfiguration — https://infosys.beckhoff.com/content/1031/tcautomationinterface/12425796491.html
- Beckhoff: StartRestartTwinCAT — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242762891.html
- Beckhoff: Silent Mode — https://infosys.beckhoff.com/content/1033/tc3_automationinterface/2489025803.html
- Existing minimal build skill — https://github.com/IwanowS/codex-skill-twincat-build
- Reference all-in-one project — https://github.com/Lance0901/AI-TwinCAT-Skill
