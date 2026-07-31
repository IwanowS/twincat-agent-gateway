# Рабочие процессы агента — target architecture v2

> **Статус:** утверждённые последовательности для первой волны архитектуры v2.
> Раздел об отладке сохраняет отложенные use cases и проектные кандидаты, но
> не добавляет их в поддерживаемый MCP contract.

Точный контракт tools/resources находится в
[`MCP_REFERENCE.md`](MCP_REFERENCE.md). Здесь описано, когда и в какой
последовательности их применять.

## 1. Общие правила

1. Агент принимает или определяет имя `profile`.
2. Если связанные исходники ещё неизвестны, агент один раз читает
   `twincat-profile://{profile}/sources`.
3. Агент не запрашивает общий status перед каждой операцией. Gateway сам
   разрешает profile, проверяет XAE/Target identity, capability и operator
   locks.
4. `GATEWAY_NOT_RUNNING` разрешает один вызов `gateway_start` и один повтор
   исходной операции.
5. Mutating result сначала читается в compact form. Дополнительная диагностика
   начинается с указанного `component`, `stage` и exact `operationId`.
6. `CAPABILITY_DISABLED` является постоянным profile denial.
   `OPERATOR_LOCKED` является временной пользовательской блокировкой; агент
   сообщает её и не опрашивает Gateway в цикле.

## 2. Типовые процессы

### 2.1 Только изменить код

```text
read profile sources, если они ещё неизвестны
→ изменить связанные source files
→ показать focused diff
→ завершить
```

Gateway/XAE/Target не нужны. Агент не выполняет build «на всякий случай», если
пользователь просил только изменить код.

### 2.2 Только проверить текущий код сборкой

```text
twincat_xae_build(profile, action=rebuild, scope=plc)
→ compact result
→ exact build artifact только при необходимости
```

Target state не входит в normal result и не является precondition. Config,
activation и restart не выполняются.

### 2.3 Изменить код и собрать

```text
read profile sources
→ изменить coherent batch
→ twincat_xae_build(profile, action=build|rebuild, scope=plc)
→ собрать весь bounded diagnostic set
→ исправить batch
→ повторить build
```

`scope=solution` используется только когда нужен полный System Manager/
solution build. Для обычной PLC-итерации default остаётся `plc`.

### 2.4 Только исправить уже известные ошибки

Если исходная неуспешная operation и `operationId` известны:

```text
compact failed result
→ twincat-operation://{operationId}/build или /xae-messages
→ изменить минимальный coherent batch
→ повторить только исходный вид проверки
```

Агент не начинает с общего status и не повторяет mutating operation как
диагностический probe.

Если известен только текст ошибки:

```text
найти соответствующий source/project
→ исправить код
→ twincat_xae_build(... scope=plc), только если нужна compile verification
```

### 2.5 Изменить код и активировать

```text
read profile sources
→ изменить coherent batch
→ twincat_xae_activate(
     profile,
     finalTargetMode=run,
     verification=none)
→ проверить stage results
```

Отдельный build перед activation не является обязательным: native activation
выполняет собственную compilation. Самостоятельная сборка добавляется только
как полезный ранний compile checkpoint.

### 2.6 Изменить код, собрать и выполнить тесты

```text
read profile sources
→ изменить implementation/tests
→ twincat_xae_build(... scope=plc)       # самостоятельный compile checkpoint
→ исправить полный bounded compile batch
→ twincat_xae_activate(
     profile,
     finalTargetMode=run,
     verification=tcunit)
→ проверить compile/deploy/transition/verification stages
```

Вторая compilation внутри activation ожидаема. Первая сборка не является
Gateway precondition и может быть пропущена, если отдельная syntax feedback
итерация не нужна.

### 2.7 Изменить код и сразу выполнить тесты

```text
read profile sources
→ изменить implementation/tests
→ twincat_xae_activate(
     profile,
     finalTargetMode=run,
     verification=tcunit)
→ собрать все bounded failures этого run
```

Это основной короткий workflow для небольшого coherent изменения.

### 2.8 Повторить тесты без изменения deployed code

```text
twincat_target_start_restart(profile, verification=tcunit)
→ fresh Target Run evidence
→ fresh completion/report evidence
```

При исходном Run это настоящий restart, а не успешный no-op. Activation и
повторная загрузка configuration не выполняются.

### 2.9 Перевести Target в Config

```text
twincat_target_config(profile)
→ fresh direct System Service Config postcondition
```

Операция допустима из любого наблюдаемого Target state. Gateway по возможности
сохраняет предшествующие fault observations, но сбор crash/core-dump evidence
не блокирует Config.

### 2.10 Запустить или перезапустить Target без тестов

```text
twincat_target_start_restart(profile, verification=none)
→ fresh direct System Service Run postcondition
```

Config/Stopped означает start; Run означает restart.

### 2.11 Диагностировать отказ

```text
failed result
→ resource затронутого объекта
→ exact twincat-operation://{operationId}
→ один exact artifact
→ current Gateway log только для gateway-wide/unknown failure
```

Маршрутизация:

| `component` | Начальный resource |
|---|---|
| `gateway` | `twincat-gateway://diagnostics` |
| `profile` | profile capabilities или source manifest |
| `xae` | `twincat-xae://profile/{profile}/diagnostics` |
| `target` | `twincat-target://profile/{profile}/diagnostics` |
| `plc` | `twincat-plc://profile/{profile}/{runtime}/diagnostics` |
| `verification` | root operation и exact xUnit artifact |

XAE-observed system state, direct System Service state и PLC runtime state не
подменяют друг друга.

## 3. Отложенные сценарии отладки

Этот раздел нужен для сохранения предметной модели и будущего API discovery.
Указанные ниже имена — кандидаты для spike, а не зарезервированный контракт.
Они намеренно отсутствуют в [`MCP_REFERENCE.md`](MCP_REFERENCE.md).

### 3.1 Наблюдение значения PLC

Предполагаемая последовательность:

```text
выбрать profile + logical PLC runtime
→ проверить XAE/Target/PLC observations отдельно
→ разрешить symbol из exact PLC project/debug metadata
→ начать bounded watch или выполнить bounded read
→ вернуть value + type + quality + timestamp + transport path
```

Возможные primitives:

```text
twincat_plc_read
twincat_plc_watch_start
twincat_plc_watch_stop
twincat-plc://profile/{profile}/{runtime}/watches/{watchId}
```

Диагностика должна явно показывать путь
`Agent → Gateway → ADS Router → Ethernet/ADS → PLC runtime`, а не относить
ошибку к XAE по умолчанию.

### 3.2 Запись, force и release force

Предполагаемая последовательность:

```text
resolve exact symbol/type/runtime
→ проверить отдельную profile capability и operator lock
→ зафиксировать pre-value
→ выполнить native write/force/release
→ проверить postcondition
→ сохранить audit artifact
```

Возможные primitives:

```text
twincat_plc_write
twincat_plc_force
twincat_plc_release_force
```

Будущий дизайн сохраняет принятый принцип: разрешённая profile capability
является standing authorization для точного test bench. Machine-level запрет
должен обеспечиваться Gateway policy, ADS route/ACL и аппаратными
блокировками, а не неоднозначным предупреждением в prompt.

### 3.3 PLC application state control

Target System state и PLC application state остаются разными объектами:

```text
twincat_target_config / twincat_target_start_restart
```

не заменяют будущие native PLC operations:

```text
PLC login/logout
PLC Run/Stop/Reset
download/online change
```

Кандидат API должен принимать `profile` и logical runtime, но не arbitrary
AMS NetId/ADS port.

### 3.4 Exception и core dump

Предполагаемая последовательность:

```text
read XAE observation
→ read direct System Service observation
→ read exact PLC runtime observation
→ collect available exception/call-stack/core-dump artifact
→ optional twincat_target_config
→ analyze offline artifact
```

Config остаётся доступной обычной операцией. Автоматический сбор dump может
предшествовать ей, но невозможность получить dump не должна превращаться в
policy-блокировку перехода.

### 3.5 Breakpoints и stepping через XAE

Предполагаемая последовательность:

```text
ensure exact XAE solution/profile
→ establish PLC online/debug session
→ set breakpoint by source identity
→ continue/step
→ read structured location/call stack/locals
→ remove breakpoint/detach
```

Возможные object-oriented groups:

```text
twincat_xae_debug_attach
twincat_xae_debug_breakpoint_set/remove
twincat_xae_debug_continue
twincat_xae_debug_step

twincat-xae://profile/{profile}/debugger/state
twincat-xae://profile/{profile}/debugger/call-stack
twincat-xae://profile/{profile}/debugger/locals
```

Перед фиксацией API нужен real-XAE spike на TwinCAT 3.1.4024.17: проверить
DTE command identities, доступность structured data, modal dialogs,
breakpoint identity, timeout/cancellation и доказуемые postconditions.

## 4. Что намеренно не решено для отладки

- окончательное число и имена debug tools;
- выбор между one-shot read и subscription/watch transport;
- symbol resolution и типобезопасная сериализация;
- write/force audit retention;
- login/download/online-change state machine;
- debugger ownership и взаимодействие с ручной XAE session;
- поведение при reconnect, breakpoint hit и stale debug data;
- hardware commissioning и machine-safety acceptance.

Эти вопросы не должны расширять первую волну реализации. Возврат к ним
начинается с отдельного architecture decision и 4024.17 spike, а не с
добавления произвольного ADS invoke.
