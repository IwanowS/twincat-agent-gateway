# Ключевые решения архитектуры v2

> **Статус:** решения утверждены в архитектурном диалоге 2026-07-31.
> Они определяют target design, даже если соответствующая реализация ещё не
> начата. План реализации:
> [`ARCHITECTURE_REWORK_PLAN.md`](ARCHITECTURE_REWORK_PLAN.md).

## D-001. Profile является maximum authority

**Статус:** accepted.

**Решение:** включённая profile capability означает, что пользователь заранее
разрешил Gateway выполнять это действие на идентифицированных profile
resources. Агент не запрашивает повторное подтверждение и не читает config
перед каждой операцией.

**Почему:** повторная agent-side policy увеличивает число tool calls, расход
контекста и вероятность расхождения с реальной Gateway configuration.

**Следствия:**

- Gateway проверяет capabilities и identities сам;
- явный conversational запрет пользователя уменьшает полномочия;
- agent не обходит denial другим automation path;
- static `false` нельзя повысить через UI.

## D-002. Agent передаёт profile, Gateway разрешает resources

**Статус:** accepted.

**Решение:** normal tool input содержит profile, а не solution path/AMS NetId.
Gateway разрешает solution, target и PLC ports из configuration/project graph.

**Почему:** profile уже содержит две необходимые identities: что открыть и
куда загрузить. Дублирование identities в каждом request создаёт лишние
проверки у агента.

**Следствия:**

- mismatch возвращает expected/observed detail;
- compact success не повторяет paths/NetId;
- source discovery становится отдельным resource.

## D-003. Gateway, XAE, Target System и PLC runtime — разные объекты

**Статус:** accepted.

**Решение:** публичный API и diagnostics имеют object namespaces:

```text
gateway
xae
target
plc
operation
```

**Почему:** build/activation идут через XAE, а direct ADS state/symbol access —
через ADS Router без XAE. Ошибки и state принадлежат разным узлам.

**Следствия:** ошибки всегда содержат `component` и `stage`; unified internal
journal не превращается в unified public status.

## D-004. Три наблюдения TwinCAT/PLC state не агрегируются

**Статус:** accepted.

**Решение:** хранить отдельно:

1. состояние TwinCAT system, наблюдаемое XAE;
2. direct System Service state на profile AMS NetId port 10000;
3. direct PLC runtime state на каждом PLC ADS port.

**Почему:** даже когда они относятся к одному target, у них разные source,
freshness и failure modes. System Service Run не означает, что каждая PLC
работает.

**Следствия:**

- удалить aggregate `runtime mode`;
- сохранять raw `AdsState`, raw `DeviceState`, address и timestamp;
- mapping нормализованного state является device-specific;
- UI health summary не заменяет raw observations;
- divergence является диагностическим фактом.

## D-005. Config — стандартная Target operation

**Статус:** accepted.

**Решение:** заменить special recovery surface на
`twincat_target_config(profile)`.

**Почему:** Config — нативный режим TwinCAT, допустимый из любого состояния.
Потеря live exception state не оправдывает отдельную policy-команду.

**Следствия:**

- best-effort fault snapshot выполняется до transition;
- dump collection не блокирует Config;
- success требует свежего System Service postcondition;
- legacy recovery tool удаляется без alias.

## D-006. Start/restart имеет явную non-idempotent семантику

**Статус:** accepted.

**Решение:** использовать `twincat_target_start_restart`.

**Почему:** нативная операция запускает TwinCAT из Config/Stopped и
перезапускает его из Run. Имя `run` скрывало бы side effect Run → Run.

**Следствия:** test-only rerun может использовать Target restart без
activation.

## D-007. Standalone build — compile check; activation компилирует сама

**Статус:** accepted.

**Решение:**

- default build scope — PLC project;
- при единственном PLC `project=null` выбирает его автоматически;
- при нескольких PLC caller обязан передать logical project id; профиль не
  задаёт default PLC;
- неизвестный id отклоняется как `BUILD_PROJECT_NOT_FOUND`, а отсутствующий
  или дублирующийся выбор при нескольких PLC — как
  `BUILD_PROJECT_AMBIGUOUS`;
- `project` недопустим для `scope=solution`;
- target state не является Gateway policy-precondition PLC compilation;
- activation не требует standalone/recent build;
- activation наблюдает собственную встроенную XAE compilation.

**Почему:** основная ценность build в agent loop — синтаксическая проверка.
Предварительный build перед activation дублирует нативный XAE pipeline.

**Следствия:**

- удалить `BUILD_BLOCKED_BY_RUNTIME_EXCEPTION` policy gate;
- удалить recent-build configuration;
- сохранить явный solution build только для задач, которым он нужен.

## D-008. Tests — verification stage activation или restart

**Статус:** accepted.

**Решение:** TcUnit подключается параметром `verification=tcunit` к:

```text
twincat_xae_activate
twincat_target_start_restart
```

**Почему:** новый код требует activation, а повтор тестов без изменения кода
требует только restart. Отдельная обязательная цепочка Build → Activate →
GetResults создаёт лишние операции.

**Следствия:**

- один root `operationId`;
- stage outcomes остаются раздельными;
- test failure не переписывает успешную deploy stage;
- normal workflow получает summary/result resource сразу.

## D-009. Dynamic operator locks только уменьшают capabilities

**Статус:** accepted.

**Решение:** UI предоставляет profile-scoped session locks и master
«block mutating agent operations».

**Почему:** оператору нужен быстрый временный контроль, когда он сам работает
со стендом, без редактирования/restart configuration.

**Следствия:**

- locks не персистентны;
- read-only state/diagnostics остаются доступны;
- denial возвращает `OPERATOR_LOCKED`;
- admission и safe-stage boundaries повторно проверяют lock;
- cancellation текущей operation остаётся отдельной функцией.

## D-010. XAE close зависит от configuration, ownership и PID consent

**Статус:** accepted.

**Решение:** configured close capability является maximum authority. Gateway
launched XAE получает consent по умолчанию, attached user XAE — нет.

**Почему:** пользовательский XAE нельзя закрыть только потому, что profile
вообще разрешает close; Gateway-owned lifecycle, наоборот, должен быть
управляемым без повторного ручного подтверждения.

**Следствия:**

- consent привязан к exact PID;
- restart/re-attach без ownership record сбрасывает consent;
- UI показывает configured/session/effective values;
- `Process.Kill` не используется.

## D-011. Main UI отделён от полного configuration view

**Статус:** accepted direction; layout details require implementation design.

**Решение:**

- Overview показывает objects/states/current operation;
- operator locks находятся в отдельной compact panel;
- все options находятся в отдельном read-only details block с descriptions;
- Boolean configuration options показываются disabled/read-only checkboxes;
- interactive checkboxes используются только для session locks и PID consent;
- colors дополняют text/icons, а не заменяют их.

**Почему:** отображение всех booleans на главном экране перегружает интерфейс.

**Цветовая основа:**

- Run — green;
- Config — blue;
- Stop/Exception/fault — red family с разными labels/icons;
- unknown — gray;
- temporary lock — amber;
- disabled capability — neutral gray.

## D-012. Source manifest принадлежит profile

**Статус:** accepted.

**Решение:** Gateway возвращает связанные с solution source roots/files через:

```text
twincat-profile://{profile}/sources
twincat-profile://{profile}/sources/files
```

**Почему:** source может находиться далеко от `.sln` и текущего agent
workspace. Агенту нужен authoritative список редактируемого project graph.

**Следствия:**

- paths выводятся из solution/project graph, а не дублируются в config;
- compact resource возвращает roots/counts, exact files — отдельно;
- Gateway сообщает path existence, но не обещает agent filesystem write
  authority;
- source manifest и synchronization используют один resolver.

## D-013. Operation resources используют exact ID

**Статус:** accepted.

**Решение:** Gateway-owned mutating tool возвращает `operationId`; artifacts
доступны только по этому ID. `gateway_start` и `gateway_shutdown` возвращают
typed lifecycle result без `operationId`, потому что process lifecycle
находится вне Gateway operation journal.

**Почему:** `last`, `previous` и `-N` становятся race-prone при одновременной
работе UI/agent.

**Следствия:** global build output/test result resources удаляются; recent
operations остаются UI/query view, а не identity artifact.

## D-014. API compatibility не сохраняется

**Статус:** accepted.

**Решение:** tools/resources/IPC/config v1 удаляются без aliases.

**Почему:** проект ещё не имеет обязательства сохранять public compatibility,
а параллельные старые/новые semantics продлят хаос.

**Следствия:** migration может временно ломать build и packaging; merge gate
наступает только после полного contract cutover.

## D-015. Project variant selection отложен

**Статус:** deferred.

**Решение сейчас:** пользователь выбирает variant вручную при подготовке
`.sln`. Gateway только наблюдает active variant.

**Почему:** TwinCAT 3.1.4024.17 поддерживает variants, но текущему workflow не
нужно programmatic переключение. Оно затрагивает reload, compiler definitions,
project state и activation identity.

**Условие возврата:** появится подтверждённый сценарий, где одна agent session
обязана переключать normal/test variant автоматически.

## D-016. Debugging implementation отложена

**Статус:** deferred.

**Решение сейчас:** реализовать корректный state vocabulary и object boundaries,
но не добавлять symbol read/write, force, PLC control или XAE debugger tools.

**Почему:** XAE UI поддерживает online monitoring, writes/forces, breakpoints,
stepping и call stack, но стабильность programmatic Automation Interface на
4024.17 требует отдельного spike.

**Условие возврата:** утверждённый debug use case и real-XAE spike, который
проверяет DTE command identities, structured breakpoint/call-stack access,
dialogs и postconditions.

Сохранённые use cases и незафиксированные кандидаты API:
[`WORKFLOWS.md`](WORKFLOWS.md#3-отложенные-сценарии-отладки).

## D-017. Skills task-oriented, API object-oriented

**Статус:** accepted.

**Решение:** target skills:

```text
twincat-build
twincat-test
twincat-operate
twincat-diagnose
twincat-debug   # deferred
```

`twincat-activate` поглощается `twincat-operate`.

**Почему:** tool names должны показывать affected object, а skill — решаемую
пользовательскую задачу и правильную последовательность.
