# C4, уровень 2: Container diagram

Диаграмма фиксирует ответственность контейнеров и направление вызовов. Единственная публичная
точка входа — `gateway` на порту `8080`. Предметное выполнение существует только внутри
PostgreSQL, за функцией `api.invoke`, — и для HTTP-клиента, и для исполнителя процессов.

Неделя 3 добавляет интеграционный периметр: три Python-процесса из одного образа и выданный
provider v0.2.0. Python переводит форматы и носит байты через фиксированные границы, но не
выбирает карту, лимит, переход или финальный статус. См.
[ADR 0005](adr/0005-provider-trust-boundary-and-anti-corruption-layer.md) и
[ADR 0006](adr/0006-durable-outbox-and-inbox.md).

```mermaid
C4Container
    title Недели 1-3. Action runtime, workflow-ядро и Python-периметр

    Person(client, "Внешний клиент", "Создаёт и отправляет операцию, читает события")
    Person(reviewer, "Ревьюер", "Принимает ручное решение по payment-review")
    Person(operator, "Оператор курса", "Публикует миграции, манифесты и карты процессов")
    System_Ext(provider, "provider-simulator", "Go, выданный образ v0.2.0 по digest. Идемпотентный POST /payments, legacy callback без JWT и HMAC")

    System_Boundary(stack, "Docker Compose stack") {
        Container(gateway, "gateway", "C# ASP.NET Core", "Единственный публичный порт 8080. Whitelist маршрутов, прозрачная передача контрактных заголовков, health. Не содержит каталог и предметную логику")
        Container(api, "api", "C# ASP.NET Core", "Generic action runtime. JWT, server-side context, request/response schema validation, одна Npgsql-транзакция, OpenAPI из манифестов")
        Container(cli, "cli", "C# console", "Course CLI: checksummed migrations, публикация манифестов и карт, trusted-команды процессов. Тремя разными ролями: миграций, публикации и операций")
        Container(workera, "worker-a", "C# console", "Сменный исполнитель процессов. Аренда задания, вызов зарегистрированного действия, сообщение исхода. lease owner worker-a")
        Container(workerb, "worker-b", "C# console", "Тот же образ, lease owner worker-b. Отличается только идентификатором арендатора")
        Container(dispatcher, "outbox-dispatcher", "Python 3.12", "Один claim - одна HTTP-попытка к provider. Неизменные key/body/correlation, классификация ответа, отчёт в PostgreSQL")
        Container(adapter, "receipt-adapter", "Python 3.12, тот же образ", "Anti-corruption layer: legacy callback -> receipt v1, compact sorted bytes, HMAC. Без учётных данных БД")
        Container(reconciler, "inbox-reconciler", "Python 3.12, тот же образ", "Периодически вызывает одну функцию согласования Inbox")
        ContainerDb(postgres, "postgres", "PostgreSQL 17", "Авторитетное состояние: каталог, идемпотентность, карты, процессы, jobs, сигналы, operations, external requests, Outbox, Inbox, receipts, decisions, схема autocheck")
    }

    Rel(client, gateway, "POST /api/{module}/{action}, GET /openapi, GET /health", "HTTPS/HTTP :8080")
    Rel(gateway, api, "Проксирует запрос и контрактные заголовки", "HTTP по Compose DNS http://api:8080")
    Rel(api, postgres, "SELECT api.invoke(...), api.resolve_action(...), api.record_dispatch(...)", "Npgsql, роль course_runtime")
    Rel(operator, cli, "migration apply, action publish/activate, flow publish/activate/start/signal", "docker compose run cli")
    Rel(cli, postgres, "DDL, публикация каталога и карт, trusted-команды процессов", "Npgsql, роли course_migrator, course_publisher и course_operator")
    Rel(workera, postgres, "claim_jobs → api.invoke → finish_job / fail_job", "Npgsql, роль workflow_worker")
    Rel(workerb, postgres, "claim_jobs → api.invoke → finish_job / fail_job", "Npgsql, роль workflow_worker")
    Rel(reviewer, gateway, "POST /api/workflow/manual", "HTTP :8080, scope workflow:manual")
    Rel(dispatcher, postgres, "delivery.claim_outbox / succeed_outbox / fail_outbox", "psycopg, роль outbox_dispatcher")
    Rel(dispatcher, provider, "POST /payments + Idempotency-Key + X-Correlation-ID", "HTTP provider-simulator:8081")
    Rel(provider, adapter, "Legacy callback", "HTTP /callbacks/provider-v02/{capability}")
    Rel(adapter, gateway, "POST /api/receipt/accept + JWT + X-Provider-Signature", "HTTP gateway:8080")
    Rel(reconciler, postgres, "delivery.reconcile_inbox", "psycopg, роль inbox_reconciler")

    UpdateLayoutConfig($c4ShapeInRow="3", $c4BoundaryInRow="1")
```

## Границы доверия

| Граница | Что проверяется | Где |
|---|---|---|
| Edge | Whitelist маршрутов, отсутствие предметной логики и БД | `gateway` |
| Runtime | Подпись и форма claims JWT, policy, request schema, outcome и response schema | `api` |
| Worker | Mapping по указателям RFC 6901, request schema, объявленность исхода, response schema | `worker-*` |
| Database | Разрешение target только из каталога, повторная проверка policy, точная сигнатура функции, идемпотентность | `api.invoke` |
| Publication | Форма шага (`automatic` → `task`, `wait_signal` → `signal_type` и `outcome`, `end` → `outcome`), объявленность `start_step` и обоих концов каждого перехода | `workflow.publish_flow`, CHECK `workflow.step_definition` |
| Lease | Совпадение задания, владельца, версии аренды и состояния до единой записи | `workflow.finish_job` |
| Identity | Неизменяемость `execution_id`, закрепления процесса за версией, тела принятого сигнала и невозможность отмотать `lease_version` назад | триггеры `workflow.*_identity` |
| Roles | `course_runtime` без DML и без доступа к схеме `workflow`; `workflow_worker` — четыре функции и ноль DML; `course_publisher` меняет только определения, `course_operator` — только исполнение; owner `SECURITY DEFINER` имеет `NOLOGIN` | PostgreSQL |
| Provider edge | Capability-сегмент пути (constant-time), строгая схема legacy callback, запрет CR/LF и unknown fields, лимит 64 KiB | `receipt-adapter` |
| Signature | HMAC-SHA256 над точными байтами тела до разбора JSON; в context только `transport.signatureVerified` и `signatureVersion` | `api`, `SignatureBoundary` |
| Receipt | Маркер подписи, версия, известный `externalRequestId`, глобальный `messageId` и SHA-256 тела | `payment.receipt_accept_v1` |
| Delivery | Owner и `lease_version` до записи; `CONFIRMED` не регрессирует; ключ и тело Outbox неизменяемы | `delivery.*`, триггеры `outbox_guard`, `inbox_guard` |
| Evidence | Финальный статус только по применённой квитанции или записанному решению | `payment.complete_v1`, `reject_v1`, `approve_v1` |
| Integration roles | `outbox_dispatcher` — три функции, `inbox_reconciler` — одна; ноль прав на таблицы, `CREATE` и членств; у adapter нет учётных данных БД | PostgreSQL, Compose |

## Поток одного HTTP-вызова

```mermaid
sequenceDiagram
    autonumber
    participant C as Клиент
    participant G as gateway
    participant A as api
    participant P as PostgreSQL

    C->>G: POST /api/{module}/{action} + Authorization + Idempotency-Key
    G->>A: тот же запрос по Compose DNS
    A->>A: проверка JWT, формирование context (correlationId, deadline)
    A->>P: api.resolve_action(module, action, version)
    A->>A: policy и request schema
    A->>P: BEGIN; SET LOCAL statement_timeout; SELECT api.invoke(...)
    P->>P: каталог → policy → idempotency → зарегистрированная функция
    P-->>A: единый envelope
    A->>A: outcome ∈ outcomes, result по response schema
    alt контракт соблюдён
        A->>P: COMMIT
    else нарушение контракта или ошибка target
        A->>P: ROLLBACK
    end
    A->>P: api.record_dispatch(...) на отдельном соединении
    A-->>G: envelope
    G-->>C: envelope
```

## Поток одной попытки задания

Аренда берётся короткой транзакцией и сразу фиксируется; действие выполняется вне неё. Право на
завершение предъявляется парой «владелец + версия аренды» и проверяется заново — до единой записи.

```mermaid
sequenceDiagram
    autonumber
    participant WA as worker-a
    participant WB as worker-b
    participant P as PostgreSQL

    WA->>P: SELECT workflow.claim_jobs('worker-a', 1, lease)
    P->>P: SKIP LOCKED; срок = min(max(lease, timeout_ms+запас), lease*3); lease_version++; attempt_count++
    P-->>WA: закреплённый контракт: версия действия, схемы, outcomes, policy, mapping, данные процесса
    WB->>P: SELECT workflow.claim_jobs('worker-b', 1, lease)
    P-->>WB: пусто, задание арендовано
    WA->>WA: payload из констант и mapping; проверка по request schema

    WA->>P: BEGIN; SET LOCAL statement_timeout
    WA->>P: SELECT api.invoke(..., context с executionId, payload)
    P->>P: каталог → policy → idempotency по executionId → зарегистрированная функция
    P-->>WA: единый envelope
    WA->>WA: outcome ∈ outcomes, result по response schema

    alt контракт соблюдён
        WA->>P: SELECT workflow.finish_job(jobId, owner, leaseVersion, outcome, result, dispatch)
        P->>P: fencing → попытка SUCCEEDED → шаг COMPLETED → dispatch → переход и вход в следующий шаг
        WA->>P: COMMIT
    else ошибка действия, нарушение контракта или таймаут
        WA->>P: ROLLBACK
        WA->>P: SELECT workflow.fail_job(jobId, owner, leaseVersion, code, retryable, dispatch)
        P->>P: попытка FAILED → след ERROR → RETRY_WAIT с задержкой или DEAD, событие TaskFailed
    end
```

## Что происходит при гибели арендатора

```mermaid
sequenceDiagram
    autonumber
    participant WA as worker-a (гибнет)
    participant WB as worker-b
    participant P as PostgreSQL

    WA->>P: claim_jobs → lease_version = N
    Note over WA: контейнер убит: транзакция действия откатывается целиком
    WB->>P: claim_jobs после lease_until
    P->>P: попытка N → STALE (attempt_count растёт, бюджет отказов - нет)
    P->>P: lease_version = N + 1, новая попытка RUNNING
    P-->>WB: то же jobId и тот же executionId
    WA-->>P: finish_job(jobId, 'worker-a', N)
    P-->>WA: workflow.lease_stale, ни одна строка не изменена
    WB->>P: api.invoke + finish_job(jobId, 'worker-b', N + 1) в одной транзакции
    P-->>WB: ok, ровно один предметный эффект
```

## Периметр: provider path

`payment.prepare_external` фиксирует external request и Outbox одной транзакцией с завершением
задания, поэтому к моменту HTTP запрос уже durable. Ответ provider и callback — независимые
события: callback может подтвердить доставку раньше, чем dispatcher отчитается.

```mermaid
sequenceDiagram
    autonumber
    participant W as worker
    participant P as PostgreSQL
    participant D as outbox-dispatcher
    participant S as provider v0.2.0
    participant A as receipt-adapter
    participant G as gateway и api
    participant R as inbox-reconciler

    W->>P: api.invoke(payment.prepare_external) + finish_job
    P->>P: external_request CREATED + outbox PENDING + wait_receipt WAITING_SIGNAL в одной транзакции
    D->>P: delivery.claim_outbox(owner, 1)
    P-->>D: outbox_id, lease_version, externalRequestId, correlationId, amount, currency
    D->>S: POST /payments, Idempotency-Key = externalRequestId
    alt 202 и строгое тело ACCEPTED
        D->>P: delivery.succeed_outbox(id, owner, lease_version, providerPaymentId)
    else timeout, 408, 429, 5xx
        D->>P: delivery.fail_outbox(id, owner, lease_version, code)
        P->>P: RETRY_WAIT и next_attempt_at из политики; повтор с теми же байтами и ключом
    end
    S->>A: legacy callback
    A->>A: строгая валидация, receipt v1, compact sorted bytes, HMAC
    A->>G: POST /api/receipt/accept, JWT receipt-provider, Idempotency-Key = messageId, подпись
    G->>P: подпись верна, api.invoke(receipt.accept) с transport marker
    P->>P: inbox RECEIVED + receipt + outbox и external_request CONFIRMED, commit
    G-->>A: 200 RECEIVED
    A-->>S: тот же статус и тело
    R->>P: delivery.reconcile_inbox(50)
    P->>P: процесс ждёт payment.receipt: signal APPLIED, inbox APPLIED, job apply_receipt
    W->>P: payment.apply_receipt берёт outcome из сохранённой квитанции, затем complete или reject
```

## Периметр: payment-review

```mermaid
sequenceDiagram
    autonumber
    participant C as клиент
    participant V as ревьюер
    participant G as gateway и api
    participant P as PostgreSQL
    participant W as worker

    C->>G: payment.submit {operationId}
    G->>P: binding PAYMENT_APPROVAL → payment-review, start_process, OPERATION_SUBMITTED
    W->>P: payment.validate, затем payment.check_limit
    alt amount не больше 100000.00 по course-limit-v1
        P->>P: decision LIMIT_RULE APPROVED, outcome WITHIN_LIMIT, job approve_operation
    else amount больше 100000.00
        P->>P: outcome REVIEW_REQUIRED, шаг wait_manual_decision, процесс WAITING_MANUAL
        V->>G: workflow.manual {processId, stepInstanceId, decision, reason}
        G->>P: lock process, decision MANUAL с principal из JWT и reason_hash, step COMPLETED, следующий job
    end
    W->>P: payment.approve или payment.reject только при записанном решении
```
