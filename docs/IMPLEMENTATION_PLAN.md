# План разработки TwinCAT Agent Gateway

## 1. Стратегия

Разработка идёт вертикальными инкрементами. Каждый milestone должен давать проверяемый рабочий результат на TwinCAT 3.1.4024.17.

Нельзя сначала написать большой MCP API, а потом пытаться сделать COM-слой надёжным. Сначала создаётся XAE gateway и диагностируется реальное поведение 4024.17, затем добавляются CLI/MCP adapters.

## 2. Критерии MVP

MVP завершён, когда Codex может выполнить следующий цикл:

1. изменить PLC-файлы;
2. вызвать `Rebuild`;
3. получить компактный список compile errors;
4. исправить код и повторить сборку;
5. явно вызвать `Activate` для разрешённого стенда;
6. gateway применит конфигурацию и перезапустит TwinCAT;
7. boot project автоматически запустит TcUnit;
8. gateway подтвердит завершение TcUnit через фиксированный read-only ADS symbol;
9. агент получит summary и failed tests из свежего xUnit XML;
10. при проблеме агент запросит detailed diagnostics или конкретный raw log;
11. изменения project graph, записанные XAE во время gateway-owned operation,
    будут приняты, залогированы и не потребуют полного чтения XML агентом.

Весь цикл выполняется без PowerShell. ADS surface ограничен `ReadState` на
фиксированном System Service port 10000 и PLC runtime ports из точного
выбранного `.tsproj`, а также чтением заранее настроенных TcUnit completion
symbols выбранного target; произвольные reads/writes, caller-selected ports,
RPC и runtime control через ADS не входят в MVP.

Repository CLI не используется в agent workflow, остаётся непроверенным
development-клиентом и не входит в acceptance MVP. Его исправление,
CLI-specific проверки и проверка parity с MCP отложены до post-MVP.

## 3. Milestone 0 — технические spikes

### Цель

Устранить самые рискованные неизвестные до проектирования окончательных abstractions.

### Задачи

#### 0.1 Матрица среды

Зафиксировать:

- точную версию TwinCAT 3.1.4024.17;
- XAE Shell/Visual Studio edition и version;
- bitness процессов;
- зарегистрированные ProgID;
- пути interop/type libraries;
- используемые configuration/platform;
- local/remote target;
- test solution и PLC boot project.

#### 0.2 ROT и multiple XAE

Прототип на C#:

- перечисляет DTE instances;
- выводит process identity, version и solution path;
- выбирает нужный экземпляр по абсолютному пути;
- получает `ITcSysManager`.

Acceptance:

- корректно выбирается solution при двух открытых XAE;
- отсутствие solution возвращает явную ошибку;
- COM exceptions не подавляются.

#### 0.3 STA + IMessageFilter

Прототип:

- отдельный STA thread;
- message pump;
- OLE message filter;
- журнал повторов `RPC_E_CALL_REJECTED`.

Acceptance:

- вызов во время занятости XAE либо завершается после контролируемого retry, либо возвращает timeout с HRESULT;
- нет фиксированного общего `Sleep(5s)`.

#### 0.4 BuildEvents и Error List

Проверить на реальных проектах:

- Build/Rebuild/Clean;
- `OnBuildDone`;
- `LastBuildInfo`;
- Error List fields;
- Build Output delta;
- ошибки PLC compiler;
- license/init errors.

Acceptance:

- успешная и ошибочная сборка различаются без анализа полного stdout;
- есть путь, строка и сообщение хотя бы для типовых PLC compile errors.

#### 0.5 Config Mode без ADS runtime control

Для TwinCAT 3.1.4024.17 подтверждена зарегистрированная DTE command identity
`TwinCAT.RestartTwinCATConfigMode`. Gateway вызывает её через типизированный
`DTE2.ExecuteCommand` на единственном STA и проверяет завершение по read-only
ADS System Service state `Config`.

Acceptance:

- command не зависит от UI coordinates, SendKeys или локализованного caption;
- перед вызовом повторно проверяются solution и AMS NetId;
- recovery выполняется в Silent Mode;
- успех требует ADS postcondition `Config`;
- timeout/failure возвращает `CONFIG_MODE_RECOVERY_FAILED`;
- end-to-end сценарий из реального PLC exception остаётся обязательной
  стендовой проверкой Milestone 7.

#### 0.6 Read-only continuous runtime status

Проверять состояния через общий background polling: System Service port 10000
и PLC ADS ports, обнаруженные из точного выбранного `.tsproj`. NetId брать
только из target, выбранного и проверенного через XAE/profile; не принимать
NetId или port от MCP/CLI.

Acceptance:

- `Run`, `Config/Reconfig`, `Stop/Stopping/Shutdown` и `Error/Exception` проверены на закреплённом ADS client;
- polling продолжается во время XAE build/activation и после временного XAE
  disconnect, используя последний проверенный target;
- неизменившиеся observations не создают повторных domain events;
- PLC `Exception`, ADS disconnect и recovery отражаются в compact status,
  каждом IPC/MCP response и cursor-based diagnostics;
- неподдержанные состояния и ADS failures возвращаются как `unknown`;
- raw ADS/device state и error evidence доступны в detailed diagnostics;
- runtime status read не вызывает XAE dialogs и не меняет runtime state.

#### 0.7 File edit/refresh

Проверить изменения `.TcPOU`, `.TcGVL`, `.TcDUT` снаружи при:

- закрытом editor;
- открытом неизменённом editor;
- открытом unsaved editor;
- открытом solution после build.

Acceptance:

- выбран attach-scoped notification/synchronization ownership для точного
  project graph без automatic save/discard пользовательских XAE buffers;
- изменения обнаруживаются fingerprint scan без предварительного объявления
  paths;
- изменённые документы типизированно reload-ятся перед build;
- project-level и editor-level file-change dialogs подавлены на всё время
  attach, включая документы, открытые после attach;
- открытый dirty editor блокирует build/sync с `DIRTY_XAE_DOCUMENT`;
- нет обязательного полного reload после каждой сборки.

#### 0.8 Read-only ADS completion для TcUnit

На TwinCAT 3.1.4024.17 проверить:

- TcUnit 1.3.1 как начальный candidate; окончательно закрепить версию по результату стенда;
- совместимую с .NET Framework 4.8 x86 версию Beckhoff ADS client;
- подключение к PLC runtime port после activation/restart;
- чтение `GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished`;
- чтение `GVL_TcUnit.NumberOfInitializedTestSuites`;
- проверку этих paths на закреплённой TcUnit version, поскольку они не документированы как стабильный публичный API;
- поведение до загрузки symbols и во время reconnect;
- timeout/cancellation и различение missing symbol от недоступного ADS target;
- соответствие ADS completion свежему xUnit XML текущего запуска.

Acceptance:

- target NetId берётся только из выбранного XAE/profile target;
- точное совпадение AMS NetId является safety identity; необязательное имя
  target используется только как display/audit metadata;
- test program назначена PLC task, инстанцирует suites и циклически вызывает `TcUnit.RUN()` или `TcUnit.RUN_IN_SEQUENCE()`;
- `xUnitEnablePublish=TRUE`, а `xUnitFilePath` доступен gateway;
- завершение определяется без фиксированного общего sleep;
- adapter не принимает произвольные symbol paths и не содержит ADS writes, RPC или `WriteControl`;
- unit tests используют fake ADS seam, а real-XAE acceptance выполняется на удалённом стенде.

Официальный `TcUnit-Runner` рассматривается как reference, а не runtime dependency: его repository архивирован, и его старый ADS/toolchain stack не должен определять архитектуру gateway. Для completion flow переносится только проверенная семантика, подтверждённая на закреплённых версиях TcUnit и TwinCAT 4024.17.

### Выход milestone

- `docs/SPIKE_RESULTS.md`;
- минимальные throwaway prototypes или test harness;
- подтверждённые архитектурные решения;
- список ограничений 4024.17.

## 4. Milestone 1 — solution skeleton и contracts

### Цель

Создать структуру репозитория и versioned domain contracts.

### Предлагаемая структура

```text
src/
  TwinCatGateway.Contracts/
  TwinCatGateway.Core/
  TwinCatGateway.Xae/
  TwinCatGateway.Desktop/
  TwinCatGateway.Ipc/
  TwinCatGateway.Client/
  TwinCatGateway.Cli/
  TwinCatGateway.Mcp/
tests/
  TwinCatGateway.UnitTests/
  TwinCatGateway.ContractTests/
  TwinCatGateway.IntegrationTests/
skills/
  twincat-build/
  twincat-test/
  twincat-activate/
  twincat-diagnose/
docs/
```

Фактическое число проектов можно сократить, если границы останутся явными.

### Задачи

- создать solution;
- добавить analyzers/style settings;
- определить protocol version;
- определить DTO operations;
- определить gateway state и operation state;
- определить error codes;
- определить log/resource references;
- определить project profile schema;
- добавить unit-test framework;
- настроить локальную сборку всего репозитория.

### Acceptance

- contracts собираются и сериализуются в .NET Framework 4.8 и .NET 8 клиентах;
- есть round-trip serialization tests;
- неизвестная protocol major version отклоняется;
- нет ссылок Contracts на COM/WPF/MCP.

## 5. Milestone 2 — desktop gateway foundation

### Цель

Получить постоянно работающий процесс с UI, operation queue и IPC.

### Задачи

- WPF/tray host;
- single-instance guard текущего пользователя;
- application lifecycle;
- immutable status snapshot;
- operation queue;
- operation store;
- standard `ILogger<T>`/Serilog compact structured logging;
- per-session size/count rotation, current-path tracking, raw log store и age
  retention;
- Named Pipe server с ACL;
- health/status IPC;
- basic UI recent operations;
- configuration loading;
- profile validation.

### Acceptance

- gateway запускается один раз;
- IPC test client получает status;
- concurrent modifying requests сериализуются;
- operation можно отменить до начала выполнения;
- crash одной операции не завершает gateway без необходимости;
- log по operationId открывается из UI.

## 6. Milestone 3 — XAE session

### Цель

Надёжно подключаться к XAE и удерживать COM session.

### Задачи

- `ComStaDispatcher`;
- OLE message filter;
- ROT enumerator;
- ProgID discovery;
- DTE selection;
- solution open/attach;
- `ITcSysManager` acquisition;
- XAE responsiveness health check;
- reconnect policy;
- Silent Mode setting;
- status fields;
- detailed diagnostics найденных instances.

### Acceptance

- attach к уже открытому solution;
- запуск нового XAE при разрешённой политике;
- корректная работа при нескольких instances;
- reconnect после ручного закрытия/перезапуска XAE;
- нет COM calls вне STA dispatcher;
- `RPC_E_CALL_REJECTED` виден в telemetry;
- статус не зависает вместе с busy COM call.

## 7. Milestone 4 — Build/Rebuild/Clean

### Цель

Заменить существующий PowerShell build helper основным gateway workflow.

### Задачи

- operation contract `build`;
- необязательный `changedPaths` hint плюс авторитетный fingerprint diff;
- session fingerprint state `uninitialized -> syncRequired ->
  synchronizing -> confirmed`;
- exact project-graph manifest (`.tsproj` -> `.plcproj` -> `Compile Include`),
  включая относительные ссылки вне каталога `.sln`;
- profile-level `externalChangePolicy`;
- profile option `assumeAttachedXaeSynchronized` с default `true`: чистый
  user-opened XAE принимает disk graph как initial baseline под
  ответственностью оператора; `false` сохраняет explicit-sync boundary;
- dirty-document fail-closed policy без automatic save/discard;
- явная UI synchronization и permission-gated MCP `twincat_sync`;
- versioned XSD bundle для XAE 3.1.4024.17;
- preflight изменённых `.TcPOU`, `.TcGVL`, `.TcDUT` по
  `TcPlcObject.xsd`;
- typed reload изменённых PLC sources перед операцией;
- Build/Rebuild/Clean implementation;
- read-only runtime preflight: наблюдаемый `Exception` завершает операцию с
  `BUILD_BLOCKED_BY_RUNTIME_EXCEPTION` до XAE build command; автоматический
  переход в Config запрещён;
- `Build.RebuildSolution` через `DTE.ExecuteCommand` с ожиданием
  `vsBuildActionRebuildAll`;
- configuration/platform resolution;
- BuildEvents lifecycle;
- Error List snapshot collector;
- Output pane delta collector;
- `.tsproj` same-content rewrite guard через `IVsFileChangeEx`;
- `FileSystemWatcher` operation window с 500 ms quiet debounce и обязательным
  итоговым fingerprint scan;
- принятие и structured logging любых project-graph changes между началом и
  terminal outcome Build/Clean/Rebuild;
- `LastBuildInfo` validation;
- diagnostic normalization;
- compact response limits;
- raw log artifact;
- CLI commands;
- UI build actions;
- integration fixtures с intentional compile errors.

### Acceptance

- success build возвращает `errors=0`;
- compile error содержит файл/строку/сообщение;
- warning-only build остаётся success;
- infrastructure failure не выдаётся за compile error;
- runtime `Exception` возвращается как штатный diagnostic blocker и требует
  отдельного `recover-to-config`;
- Clean отличается от Build/Rebuild;
- внешнее изменение открытого или закрытого `.TcPOU` попадает в следующую
  сборку без modal dialog;
- solution может собирать выбранный `.tsproj` из относительного пути за
  пределами solution root; его PLC sources входят в agent-owned workspace;
- несохранённая XAE версия PLC source блокирует операцию и не сохраняется и не
  отбрасывается автоматически;
- XSD-invalid PLC object отклоняется до typed reload/build;
- same-content `.tsproj` rewrite самой XAE не вызывает modal dialog;
- содержательное `.tsproj` изменение до operation window не скрывается и
  возвращается согласно `externalChangePolicy`;
- любое `.tsproj`/`.plcproj`/PLC source/`.tmc` изменение внутри
  gateway-owned operation window принимается и логируется;
- watcher overflow логируется и не отменяет обязательный итоговый fingerprint
  scan;
- timeout/cancellation/COM loss/unknown dialog оставляет baseline в
  `syncRequired`;
- добавленный/удалённый source file до operation window завершается явной
  unsupported error при default `reloadModified`;
- full output не попадает в compact response;
- MCP result соответствует фактическому результату операции;
- existing PowerShell helper больше не требуется для нормального workflow.

## 8. Milestone 5 — `.tsproj` noise classifier

### Цель

Не тратить токены на ожидаемые перестановки XAE и не перезагружать проект ради их исправления.

### Задачи

- pre/post operation changed-file detection;
- 500 ms quiet debounce после terminal XAE outcome;
- XSD reorder classification после завершённых Build/Rebuild, но не Clean;
- versioned schema manifest и закрытый XSD resolver;
- `TcSmProject.xsd` вместе с полным dependency closure;
- валидация baseline/current одной и той же схемой;
- canonical node representation;
- hash полного canonical subtree;
- recursive same-parent sibling multiset comparison;
- identity fields только для отчёта;
- reorder-only classifier;
- mixed-change detection;
- compact summary;
- raw classifier artifact;
- skill rule `doNotInspectFullFile`.

### Unit fixtures

- только перестановка двух блоков;
- перестановка десятков блоков;
- whitespace-only;
- содержательное изменение внутри перемещённого блока;
- добавленный/удалённый блок;
- повторяющиеся одинаковые subtree hashes;
- перенос неизменённого блока между разными parents;
- изменение attribute/text;
- invalid XML и XSD-invalid XML;
- отсутствующий или несовместимый schema set.

### Acceptance

- ни один файл не изменяется classifier-ом;
- baseline и current валидируются одним закреплённым schema set;
- только same-parent permutation полных неизменённых subtrees даёт
  `expected-reorder-only`;
- завершённая компиляция считается источником истины итогового порядка;
- после Clean content hash change может остаться `unknown` в отчёте, но
  принимается как XAE-owned change;
- содержательное изменение не классифицируется как reorder-only;
- XSD-invalid и недоказуемый case возвращают `unknown`;
- compact build result не содержит полный `.tsproj` diff;
- агенту передаётся явная рекомендация не читать файл полностью.

## 9. Milestone 6 — status и diagnostics

### Цель

Сделать состояние системы понятным человеку и агенту.

### Задачи

- compact status DTO;
- detailed diagnostics DTO;
- gateway/XAE/solution health;
- current/last operation;
- last build/activation/test summaries;
- read-only ADS System Service runtime state;
- `GetLastErrorMessages()`;
- COM retry/error statistics;
- единая bounded event stream для gateway/XAE/runtime/operation lifecycle и
  ошибок;
- пара `eventStreamId`/монотонный cursor с независимым paging и severity
  filtering для каждого клиента;
- CLI `status` и `diagnostics`;
- UI diagnostics page.

### Acceptance

- compact status мал и стабилен;
- detailed status объясняет выбор XAE instance;
- `unknown` используется вместо догадки о runtime mode;
- retained события не теряются при следующем успешном status call;
- фильтр `minimumSeverity=error` использует общий cursor и продвигается через
  несовпавшие события без отдельного мутирующего error state;
- gateway restart обнаруживается по смене `eventStreamId`, retention gap
  возвращается как `eventHistoryTruncated`;
- последние 1000 событий хранятся в памяти; долговременная история остаётся
  в локальных structured logs;
- raw stack trace не передаётся по умолчанию.

## 10. Milestone 7 — activation

### Цель

Безопасно применить конфигурацию и по явному параметру подтвердить или
отклонить переход в Run, не меняя пользовательскую настройку Auto Boot.

### Задачи

- `allowActivation` profile;
- audit fields AMS NetId/optional target name/solution/profile;
- preconditions;
- optional recent-build policy;
- recovery-to-Config adapter только как отдельная явная operation; build и
  activation не восстанавливают runtime автоматически;
- отдельные CLI/MCP `recover-to-config` / `twincat_recover_to_config`;
- DTE command `TwinCAT.ActivateConfiguration`;
- `runAfterActivation` с default `true`;
- platform mismatch -> Cancel и подробная ошибка;
- activation confirmation -> OK без изменения tri-state
  `Autostart PLC Boot Project(s)`;
- Run confirmation -> OK/Cancel согласно `runAfterActivation`;
- отсутствие отдельного `StartRestartTwinCAT()` после XAE-команды;
- stable Run postcondition при `runAfterActivation=true`; при `false` вернуть
  фактически наблюдаемое state без принудительного Config transition;
- discovery Auto Boot PLC через `BootProjectAutostart` и проверка online state
  каждого обязательного PLC;
- Error List baseline/delta с классификацией runtime faults, TcUnit summary и
  warnings;
- общий UI Automation supervisor для modal dialogs точного XAE process id на
  всём lifecycle любых gateway-owned XAE operations;
- XAE-owned project-graph operation window вокруг
  `TwinCAT.ActivateConfiguration` с 500 ms quiet/fingerprint rebaseline;
- объединение Error List, `GetLastErrorMessages()` и ADS runtime evidence в
  compact activation error;
- события activation/dialog/postcondition в общей event stream;
- UI confirmation policy;
- CLI command;
- integration tests на отдельном удалённом стенде; локальная activation/restart запрещена.

### Scenarios

- normal activation из Run;
- activation из Config;
- activation без Run для ручной отладки;
- PLC exception требует recovery;
- recent build истёк в `Exception`, а solution build не может завершиться:
  standalone recovery остаётся доступен без build precondition;
- XAE busy;
- target mismatch;
- activation disabled profile;
- `ActivateConfiguration` fail;
- Run confirmation fail;
- platform mismatch dialog;
- кратковременный `Run` с последующим PLC `Exception`;
- System Service `Run`, но один Auto Boot PLC не в `Run`;
- runtime `Exception` до достижения timeout;
- известный fatal XAE dialog;
- неизвестный XAE dialog не подтверждается автоматически;
- TcUnit summary строки имеют Error severity, но тесты успешны;
- warning-only activation diagnostics;
- TwinCAT started state unknown;
- user cancels до irreversible step.

### Acceptance

- activation никогда не выполняется для запрещённого profile;
- target и solution записаны в audit log;
- AMS NetId является единственной обязательной target identity; имя target
  необязательно и не участвует в safety decision;
- ошибка конкретного stage видна агенту;
- XAE-команда не считается полным успехом без ожидаемых activation и Run
  confirmation dialogs; при `runAfterActivation=true` дополнительно требуется
  выбранный runtime postcondition, а при `false` результат явно остаётся
  `activeConfigurationVerified=false`;
- после `TwinCAT.ActivateConfiguration` не вызывается второй
  `StartRestartTwinCAT`;
- gateway читает, но никогда не меняет tri-state Autostart checkbox;
- `waitForTcUnit=true` требует `runAfterActivation=true`;
- состояние `Exception` после activation немедленно завершает operation, а не
  расходует весь timeout;
- сохранённое до activation состояние `Run` и кратковременный `Run` не считаются
  достаточным postcondition;
- успешная activation с `runAfterActivation=true` подтверждает `Run` System
  Service и healthy online state каждого PLC с
  `BootProjectAutostart=true`; при `false` gateway отменяет финальный Run
  dialog, не переводит runtime в Config и возвращает
  `activeConfigurationVerified=false` вместе с фактически наблюдаемым
  runtime state;
- runtime fault из дельты Error List возвращается даже при пустом
  `GetLastErrorMessages()`, а TcUnit summary строки не считаются runtime fault
  только из-за XAE severity `Error`;
- warnings читаются и возвращаются отдельно от fatal activation errors;
- project-graph changes после успешного или известного terminal activation
  outcome принимаются и логируются; неизвестный/незавершённый outcome
  оставляет `syncRequired`;
- диалог принадлежит точному XAE process id, его текст записан в diagnostics,
  а неизвестная кнопка никогда не нажимается автоматически;
- exception recovery требует подтверждённого ADS-состояния `Config`, иначе
  возвращает `CONFIG_MODE_RECOVERY_FAILED` без ложного success.
- recovery-to-Config не требует recent successful build и разрывает цикл, в
  котором XAE не может завершить верхнеуровневую system-project build из
  runtime `Exception`.

## 11. Milestone 8 — TcUnit ADS completion и report flow

### Цель

После activation подтверждать завершение тестов через узкий read-only ADS adapter и получать результат из свежего xUnit XML.

### Граница MVP

- один project profile назначает ровно один TcUnit PLC;
- `tcUnit.adsPort`, completion symbols и `reportPath` относятся только к нему;
- другие PLC в solution не входят в test result и не должны публиковать в тот
  же файл;
- агрегация нескольких PLC и несколько TcUnit publishers не входят в MVP.

### Задачи

- profile PLC runtime port и фиксированные TcUnit symbol paths;
- pinned TcUnit library version и validation её completion symbols;
- read-only ADS connection seam;
- reconnect после runtime restart;
- polling `AllTestSuitesFinished` с deadline и cancellation;
- чтение `NumberOfInitializedTestSuites`;
- profile report path;
- validation `GVL_Param_TcUnit.xUnitEnablePublish=TRUE` и настроенного `xUnitFilePath`;
- pre-activation report baseline;
- безопасное удаление старого report только для разрешённого локального path;
- file watcher + polling fallback;
- stable-file detection;
- XML parser;
- xUnit variants/fixtures;
- compact failures;
- report resource;
- timeout/error semantics;
- linking activationId -> ADS evidence -> test report;
- отдельная serial `OperationKind.Test`, ID которой возвращается из activation;
- общий event cursor для activation и связанной test operation;
- UI test summary;
- CLI command.

### Acceptance

- ADS target совпадает с target связанной activation operation;
- test project содержит назначенную task test program и не требует production I/O;
- missing ADS route/target и missing completion symbol имеют разные error codes;
- completion timeout не считается success;
- adapter не предоставляет arbitrary symbol access и не выполняет ADS writes;
- старый report не принимается за новый;
- invalid/partial XML не принимается за success;
- ADS completion без свежего XML не принимается за pass/fail success;
- XML без подтверждённого ADS completion не принимается как результат текущей test operation;
- zero tests явно отражается и настраивается как fail/warning;
- successful test cases не перечисляются в compact result;
- failed tests содержат suite/name/message;
- полный XML доступен отдельно.

### Post-MVP: несколько TcUnit PLC

Поддержка нескольких PLC планируется как отдельное versioned расширение
контракта, а не как неявное изменение single-PLC semantics:

1. добавить список test PLC descriptors с устойчивой logical identity,
   `adsPort`, фиксированными completion symbols и уникальным `reportPath`;
2. валидировать уникальность ADS ports, logical identities и report paths,
   запрещая несколько publishers для одного файла;
3. снимать baselines всех отчётов до activation и связывать все результаты с
   одной successful activation;
4. выполнять completion/report flow отдельно для каждого PLC с общим
   cancellation/deadline и per-PLC stage events;
5. возвращать per-PLC counts/failures/resources и агрегированный overall
   result, который завершается неуспешно при неуспехе любого обязательного PLC;
6. сохранить обратную совместимость single-PLC profile либо предоставить
   явную миграцию schema version;
7. добавить contract tests и реальный стендовый acceptance с двумя PLC,
   двумя ADS ports и двумя различными xUnit files.

## 12. Milestone 9 — MCP adapter и skills

### Цель

Интегрировать gateway с Codex и другими агентами без дублирования логики.

### Реализация adapter

- отдельный .NET 8 project `TwinCatGateway.Mcp`;
- официальный NuGet package `ModelContextProtocol` `1.4.1` (stable);
- stdio server transport через `WithStdioServerTransport()`;
- tool/resource handlers являются тонким отображением
  `TwinCatGateway.Client` и versioned gateway contracts;
- project config discovery и process lifecycle orchestration используют
  общие Core policies, но не вызывают COM/XAE;
- MCP SDK не подключается к Desktop, XAE, Core, Contracts или Ipc;
- HTTP/AspNetCore transport, prerelease `2.x` и MCP Tasks extension не входят
  в MVP; длительные операции используют существующий `operationId`.

### MCP tools

```text
gateway_start
gateway_shutdown
twincat_status
twincat_build
twincat_activate
twincat_recover_to_config
twincat_get_diagnostics
twincat_get_xae_messages
twincat_get_test_results
```

`twincat_get_xae_messages` читает только error/warning из Error List точной
выбранной XAE solution, ограничивает ответ и не изменяет XAE. При runtime
`Exception` связанная строка `Exception Code`/`Page Fault` сохраняется в
`RuntimeAlert.details` и в details блокирующей operation error.

### MCP resources

```text
twincat-log://<operation-id>/build
twincat-log://<operation-id>/xae
twincat-test://<operation-id>/xunit
twincat-diff://<operation-id>/project-noise
twincat-doc://setup
twincat-doc://configuration
twincat-log://gateway/current
```

### Skills

#### `twincat-build`

- редактировать файлы обычными patch-инструментами;
- группировать связанные правки и вызывать build только в полезной точке
  проверки, а не после каждого файла;
- читать только compact diagnostics;
- запросить raw log только при инфраструктурной/непонятной ошибке;
- не читать reorder-only `.tsproj`.

#### `twincat-test`

- считать удалённую activation/TcUnit редким checkpoint и по возможности
  выполнять один раз после стабилизации пакета правок;
- переиспользовать актуальный успешный Build/Rebuild либо выполнить один
  rebuild после последней серии правок;
- затем явный activate;
- ждать связанный ADS completion, затем свежий TcUnit report;
- собрать bounded набор failed tests, исправить их одним пакетом и только затем
  выполнить одну повторную activation/test;
- не загружать весь xUnit XML без необходимости.

#### `twincat-activate`

- проверять profile/target;
- не активировать автоматически после каждого build;
- группировать совместимые правки перед удалённой activation и предпочитать
  финальный checkpoint;
- ясно сообщать irreversible stage и failure stage.

#### `twincat-diagnose`

- сначала compact status;
- затем detailed diagnostics;
- затем один конкретный raw resource;
- не собирать все логи сразу.

### Acceptance

- schemas tools короткие;
- обычные tools не запускают gateway и возвращают `GATEWAY_NOT_RUNNING`;
- `gateway_start` проверяет workspace config, `allowStart`, singleton identity
  и Ready, делает не более одной попытки через desktop view интерактивного
  Explorer и идемпотентен для того же проекта;
- `gateway_shutdown` помечен destructive, проверяет загруженный
  `allowShutdown`, отправляет успешный response до остановки desktop gateway
  и не закрывает user-owned XAE;
- gateway-launched XAE сохраняет ownership после ROT reconnect и закрывается
  при `gateway_shutdown`; XAE, открытый пользователем, не закрывается;
- Desktop gateway не наследует agent-added environment; отсутствие
  интерактивного Explorer завершается fail-closed без прямого process fallback;
- gateway другого проекта не закрывается и не переключается;
- обычная compile-fix итерация не требует чтения raw Build Output;
- MCP process можно перезапустить без потери XAE session;
- MCP возвращает gateway domain semantics; CLI parity отложен до post-MVP;
- repository build остаётся warning-free с закреплённым stable MCP SDK;
- ни один MCP handler не вызывает COM/XAE напрямую;
- skills не содержат абсолютные пользовательские пути.

## 13. Milestone 10 — hardening и release

### Задачи

- per-user installer двух независимых приложений с одним стабильным
  application directory, подтверждаемой заменой, стабильным command directory
  и user PATH;
- отдельная глобальная Codex MCP registration через поддерживаемый CLI;
- отдельная установка skills в user/project/explicit destination;
- portable packaging как дополнительный формат;
- project-local `twincat-gateway.json` и одинаковый manual/MCP discovery;
- WPF `auto|window|tray`, manual/agent launch identity, product version и
  configurationless setup-only UI без gateway singleton/IPC;
- Explorer-mediated agent launch отделяет Desktop/XAE environment и integrity
  context от MCP process;
- first-run environment diagnostics;
- config migration;
- standard `Microsoft.Extensions.Logging.ILogger<T>` pipeline с Serilog compact
  file provider, настраиваемым minimum level и сохранением operationId,
  structured properties, Unicode и полного exception context;
- отдельные session log sets, size/count rollover, age retention и фиксированный
  MCP resource для discovery текущего реально открытого segment;
- проверить shutdown lifecycle: отличить реально оставшийся desktop process
  от устаревшей tray icon, подтвердить удаление icon при штатном и аварийном
  завершении;
- crash recovery;
- UI polishing;
- integration test checklist;
- user documentation;
- troubleshooting guide;
- security review Named Pipe ACL;
- protocol compatibility tests;
- performance/token-size measurements.

### Acceptance

- `dotnet build` формирует оба устанавливаемых комплекта без VS msbuild;
- подтверждённая повторная установка заменяет application directory, удаляет
  legacy versions и не удаляет configs/logs; non-interactive replacement
  требует `-Force`;
- installed `twincat-gateway` и `twincat-gateway-mcp` доступны через user PATH;
- MCP stdio wrapper не пишет setup/diagnostic text в stdout;
- global Codex registration и project-local alternative документированы как
  взаимоисключающие варианты;
- MCP root options, `--help`/`-h` и `--version` определены одной
  `System.CommandLine` model; help/version не запускают stdio host;
- чистая установка обнаруживает совместимую XAE среду;
- ошибка отсутствующей зависимости понятна;
- gateway корректно восстанавливается после собственного restart;
- новый MCP adapter подключается к уже работающей XAE session;
- documented uninstall не удаляет пользовательские TwinCAT projects/logs без подтверждения;
- release notes содержат проверенную матрицу версий.

## 14. Приоритетный backlog

### P0

- STA dispatcher + message filter;
- ROT selection by solution;
- Build/Rebuild/Clean;
- Error List normalization;
- compact operations/status;
- read-only ADS System Service status adapter;
- local IPC;
- safe activation profile;
- `TwinCAT.ActivateConfiguration` dialog controller без второго restart;
- Config recovery spike;
- read-only ADS TcUnit completion adapter;
- TcUnit report parser;
- `.tsproj` reorder detector;
- CLI;
- MCP core tools.
- continuous System/PLC runtime polling с compact runtime alerts.

### P1

- точная причина и позиция exception из XAE/TcUnit logs; при недостаточной
  structured evidence не перезапускать TcUnit вслепую;
- MCP resource/command для полного TcUnit log или только failed tests с
  возвратом файла и строки;
- compact status/diagnostics по умолчанию с углублением через focused
  resources;
- полноценный WPF log viewer;
- structural sync добавленных/удалённых PLC source files;
- operation cancellation UI;
- report run identifier;
- project-specific skill configuration;
- multiple named project profiles.

### P2

- повторно использовать актуальную успешную сборку перед activation и не
  запускать принудительную дублирующую build без изменения graph/configuration;
- закрепить допустимые переходы `any -> config`, `config -> run`,
  `run -> run`, `config -> activation`, `run -> activation`;
- устранить необязательные DTE/UI-вызовы, из-за которых XAE перехватывает
  фокус во время фоновой работы gateway.

### Позже, вне MVP

- специализированный read-only TwinCAT EventLogger receiver для runtime
  messages и alarms с фиксированного, проверенного profile target. Использовать
  Beckhoff EventLogger ADS proxy: принимать `MessageSent`, `AlarmRaised`,
  `AlarmCleared`, `AlarmConfirmed` и читать cache после reconnect; сохранять
  severity, EventClass/Event-ID, text, Source Info и JSON attribute. Receiver
  не отправляет сообщения, не подтверждает и не очищает alarms, не открывает
  general-purpose ADS surface и не заменяет XAE Error List: XAE показывает
  EventLogger events в Error List только при включённом `Output as Task Item`
  и с учётом severity filter;
- general-purpose ADS client;
- arbitrary online symbol reads/writes;
- ADS RPC и runtime `WriteControl`;
- code modification through Automation Interface;
- PLC login/download control;
- debugger;
- I/O tree;
- remote gateway;
- CI/headless execution;
- TwinCAT 4026/VS2022 specialization.
- Claude-specific global MCP registration; до отдельного решения поддерживать
  только документируемый manual stdio command, без installer integration.
- MCP `2026-07-28` `subscriptions/listen` для optional runtime resource
  notifications после подтверждения поддержки со стороны Codex host и
  официального C# SDK. Subscription остаётся transport optimization: gateway
  не полагается на него как на гарантию нового agent turn и сохраняет
  cursor-based event replay.

## 15. Test matrix

| Сценарий | Unit | Contract | Integration 4024.17 |
|---|---:|---:|---:|
| Protocol version | Да | Да | Нет |
| Operation queue | Да | Да | Нет |
| COM message filter | Частично | Нет | Да |
| Multiple XAE instances | Нет | Нет | Да |
| Build success/fail | Парсер | Да | Да |
| Warning-only build | Парсер | Да | Да |
| XAE busy | Нет | Нет | Да |
| Agent-owned external edit sync | Да | Да | Да |
| `.tsproj` reorder-only | Да | Да | Да |
| PLC `.tmc` generated artifact | Да | Да | Да |
| XAE-owned arbitrary graph rewrite | Да | Нет | Да |
| Activation allowed/denied | Да | Да | Да |
| Recovery after exception | Да | Да | Да |
| Public recover-to-Config tool | Да | Да | Да |
| Run-to-Exception short-circuit | Да | Да | Да |
| Auto Boot PLC readiness | Да | Да | Да |
| Activation Error List classification | Парсер | Да | Да |
| XAE fatal dialog capture | Нет | Да | Да |
| ADS System/PLC continuous runtime status | Да | Да | Да |
| TcUnit ADS completion | Да (fake ADS) | Да | Да |
| ADS target/profile mismatch | Да | Да | Да |
| TcUnit fresh report | Да | Да | Да |
| Repository CLI verification/parity | После MVP | После MVP | После MVP |
| Config discovery/Git root | Да | Нет | Smoke |
| MCP explicit gateway start | Да | Да | Smoke |
| Per-user install/PATH idempotency | Script smoke | Нет | Smoke |

## 16. Миграция с текущего build skill

Текущий `codex-skill-twincat-build` полезен как эталон поведения и аварийный fallback на ранней стадии.

Перенести концепции:

- компактный terminal output;
- явный build log;
- критерии успеха;
- configuration/platform settings;
- полезные сообщения для агента.

Не переносить:

- PowerShell transport;
- запуск отдельного `devenv.com` для каждой основной операции;
- автоматическое исправление `.tsproj`;
- зависимость agent workflow от чтения файлов `_CompileInfo` напрямую.

Порядок перехода:

1. Gateway build сравнивается с текущим helper на test solutions.
2. Результаты и diagnostics сопоставляются.
3. После достижения parity новый skill переключается на MCP.
4. Старый helper остаётся отдельным fallback до стабилизации activation/test workflow.
5. Затем PowerShell helper может быть архивирован в отдельной ветке/репозитории, но не включается в новый продукт.

## 17. Definition of Done MVP

- Desktop gateway стабильно работает полный рабочий день без накопления зависших COM operations.
- Проверена TwinCAT 3.1.4024.17.
- Build/Rebuild/Clean корректно диагностируют success/failure.
- Agent получает compile errors без полного Build Output.
- Activation ограничена profile и имеет явный `runAfterActivation`; отдельный
  restart после XAE activation command отсутствует.
- Exception recovery честно работает или честно сообщает ручной Config Mode.
- TcUnit completion подтверждён через read-only ADS на target связанной activation.
- TcUnit report связан с текущим запуском и не берётся из старого файла.
- `.tsproj` reorder-only noise определяется без изменения файла.
- Нет PowerShell runtime dependency.
- ADS client ограничен System Service/selected-project PLC `ReadState` и
  фиксированными TcUnit completion reads выбранного target; general-purpose
  ADS access отсутствует.
- MCP можно перезапустить без потери gateway/XAE session.
- Agent может явно запустить отсутствующий gateway один раз, но обычные MCP
  tools никогда не auto-start process.
- Agent launch проходит через интерактивный Explorer; Desktop и XAE не
  наследуют agent-added environment, а отсутствие Explorer не вызывает
  прямой fallback.
- Per-user installer предоставляет WPF и MCP commands без `dotnet tool` и
  без дополнительного command host.
- CLI и MCP используют общий IPC/domain contract.
- Все P0 сценарии имеют тесты соответствующего уровня.
- AGENTS.md, architecture и troubleshooting документация актуальны.
