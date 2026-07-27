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
8. агент получит summary и failed tests;
9. при проблеме агент запросит detailed diagnostics или конкретный raw log;
10. reorder-only `.tsproj` изменения будут отмечены как ожидаемые без полного чтения XML.

Весь цикл выполняется без PowerShell и без собственного ADS client.

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

#### 0.5 Config Mode без ADS client

Исследовать стабильный способ вызвать `Restart TwinCAT (Config Mode)`:

- DTE command identity/GUID/ID;
- официальный XAE command service;
- возможность проверить command availability/completion;
- поведение в Silent Mode;
- сценарий после PLC exception.

Acceptance:

- либо найден и подтверждён автоматический способ;
- либо зафиксировано ограничение MVP: `CONFIG_MODE_REQUIRED` и ручное действие.

Не использовать UI coordinates, SendKeys и локализованный caption как постоянное решение.

#### 0.6 Runtime status без ADS

Проверить, какие состояния можно надёжно получить через:

- `IsTwinCATStarted()`;
- XAE command status;
- другие доступные Automation Interface свойства;
- Error List/last error messages.

Acceptance:

- документировано, какие значения подтверждаются;
- неподтверждённые состояния возвращаются как `unknown`.

#### 0.7 File edit/refresh

Проверить изменения `.TcPOU`, `.TcGVL`, `.TcDUT` снаружи при:

- закрытом editor;
- открытом неизменённом editor;
- открытом unsaved editor;
- открытом solution после build.

Acceptance:

- выбран минимальный refresh workflow;
- определён способ детектировать конфликт;
- нет обязательного полного reload после каждой сборки.

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
- structured logging;
- raw log store и retention;
- Named Pipe server с ACL;
- health/status IPC;
- basic UI recent operations;
- configuration loading;
- profile validation.

### Acceptance

- gateway запускается один раз;
- CLI test client получает status;
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
- Build/Rebuild/Clean implementation;
- configuration/platform resolution;
- BuildEvents lifecycle;
- Error List delta collector;
- Output pane delta collector;
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
- Clean отличается от Build/Rebuild;
- full output не попадает в compact response;
- CLI exit code соответствует результату;
- existing PowerShell helper больше не требуется для нормального workflow.

## 8. Milestone 5 — `.tsproj` noise classifier

### Цель

Не тратить токены на ожидаемые перестановки XAE и не перезагружать проект ради их исправления.

### Задачи

- pre/post operation changed-file detection;
- безопасный XML parser;
- canonical node representation;
- identity rules для известных `.tsproj` blocks;
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
- duplicate identity;
- invalid XML;
- изменение вне разрешённого контейнера.

### Acceptance

- ни один файл не изменяется classifier-ом;
- содержательное изменение не классифицируется как reorder-only;
- ambiguous case возвращает `unknown`;
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
- `IsTwinCATStarted()`;
- `GetLastErrorMessages()`;
- COM retry/error statistics;
- unread/new error cursor;
- CLI `status` и `diagnostics`;
- UI diagnostics page.

### Acceptance

- compact status мал и стабилен;
- detailed status объясняет выбор XAE instance;
- `unknown` используется вместо догадки о runtime mode;
- последняя ошибка не теряется при следующем успешном status call;
- raw stack trace не передаётся по умолчанию.

## 10. Milestone 7 — activation

### Цель

Безопасно применить конфигурацию и запустить Auto Boot project.

### Задачи

- `allowActivation` profile;
- audit fields target/solution/profile;
- preconditions;
- optional recent-build policy;
- recovery-to-Config adapter;
- `ActivateConfiguration()`;
- `StartRestartTwinCAT()`;
- postcondition checks;
- error collection;
- operation timeline;
- UI confirmation policy;
- CLI command;
- integration tests на отдельном стенде.

### Scenarios

- normal activation из Run;
- activation из Config;
- PLC exception требует recovery;
- XAE busy;
- target mismatch;
- activation disabled profile;
- `ActivateConfiguration` fail;
- restart fail;
- TwinCAT started state unknown;
- user cancels до irreversible step.

### Acceptance

- activation никогда не выполняется для запрещённого profile;
- target и solution записаны в audit log;
- ошибка конкретного stage видна агенту;
- `ActivateConfiguration()` не считается полным успехом без последующего restart/postcondition;
- exception recovery либо автоматизирован и протестирован, либо возвращает `CONFIG_MODE_REQUIRED` без ложного success.

## 11. Milestone 8 — TcUnit report flow

### Цель

Получать результаты тестов после activation без собственного ADS client.

### Задачи

- profile report path;
- pre-activation report baseline;
- file watcher + polling fallback;
- stable-file detection;
- XML parser;
- xUnit variants/fixtures;
- compact failures;
- report resource;
- timeout/error semantics;
- linking activationId -> test report;
- UI test summary;
- CLI command.

### Acceptance

- старый report не принимается за новый;
- invalid/partial XML не принимается за success;
- zero tests явно отражается и настраивается как fail/warning;
- successful test cases не перечисляются в compact result;
- failed tests содержат suite/name/message;
- полный XML доступен отдельно.

## 12. Milestone 9 — MCP adapter и skills

### Цель

Интегрировать gateway с Codex и другими агентами без дублирования логики.

### MCP tools

```text
twincat_status
twincat_build
twincat_activate
twincat_get_diagnostics
twincat_get_test_results
```

### MCP resources

```text
twincat-log://<operation-id>/build
twincat-log://<operation-id>/xae
twincat-test://<operation-id>/xunit
twincat-diff://<operation-id>/project-noise
```

### Skills

#### `twincat-build`

- редактировать файлы обычными patch-инструментами;
- вызвать build;
- читать только compact diagnostics;
- запросить raw log только при инфраструктурной/непонятной ошибке;
- не читать reorder-only `.tsproj`.

#### `twincat-test`

- сначала успешный rebuild;
- затем явный activate;
- ждать связанный свежий TcUnit report;
- исправлять failed tests;
- не загружать весь xUnit XML без необходимости.

#### `twincat-activate`

- проверять profile/target;
- не активировать автоматически после каждого build;
- ясно сообщать irreversible stage и failure stage.

#### `twincat-diagnose`

- сначала compact status;
- затем detailed diagnostics;
- затем один конкретный raw resource;
- не собирать все логи сразу.

### Acceptance

- schemas tools короткие;
- обычная compile-fix итерация не требует чтения raw Build Output;
- MCP process можно перезапустить без потери XAE session;
- CLI и MCP возвращают одинаковую domain semantics;
- skills не содержат абсолютные пользовательские пути.

## 13. Milestone 10 — hardening и release

### Задачи

- installer/portable packaging;
- first-run environment diagnostics;
- config migration;
- log retention settings;
- crash recovery;
- UI polishing;
- integration test checklist;
- user documentation;
- troubleshooting guide;
- security review Named Pipe ACL;
- protocol compatibility tests;
- performance/token-size measurements.

### Acceptance

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
- local IPC;
- safe activation profile;
- `ActivateConfiguration + StartRestartTwinCAT`;
- Config recovery spike;
- TcUnit report parser;
- `.tsproj` reorder detector;
- CLI;
- MCP core tools.

### P1

- полноценный WPF log viewer;
- improved external edit conflict detection;
- operation cancellation UI;
- report run identifier;
- optional automatic reconnect;
- project-specific skill configuration;
- multiple named project profiles.

### Позже, вне MVP

- ADS client;
- online symbols;
- code modification through Automation Interface;
- PLC login/download control;
- debugger;
- I/O tree;
- remote gateway;
- CI/headless execution;
- TwinCAT 4026/VS2022 specialization.

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
| External edit conflict | Да | Да | Да |
| `.tsproj` reorder-only | Да | Да | Да |
| Activation allowed/denied | Да | Да | Да |
| Recovery after exception | Нет | Да | Да |
| TcUnit fresh report | Да | Да | Да |
| MCP/CLI parity | Нет | Да | Smoke |

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
3. После достижения parity новый skill переключается на CLI/MCP.
4. Старый helper остаётся отдельным fallback до стабилизации activation/test workflow.
5. Затем PowerShell helper может быть архивирован в отдельной ветке/репозитории, но не включается в новый продукт.

## 17. Definition of Done MVP

- Desktop gateway стабильно работает полный рабочий день без накопления зависших COM operations.
- Проверена TwinCAT 3.1.4024.17.
- Build/Rebuild/Clean корректно диагностируют success/failure.
- Agent получает compile errors без полного Build Output.
- Activation ограничена profile и включает restart.
- Exception recovery честно работает или честно сообщает ручной Config Mode.
- TcUnit report связан с текущим запуском и не берётся из старого файла.
- `.tsproj` reorder-only noise определяется без изменения файла.
- Нет PowerShell runtime dependency.
- Нет собственного ADS client.
- MCP можно перезапустить без потери gateway/XAE session.
- CLI и MCP используют общий IPC/domain contract.
- Все P0 сценарии имеют тесты соответствующего уровня.
- AGENTS.md, architecture и troubleshooting документация актуальны.
