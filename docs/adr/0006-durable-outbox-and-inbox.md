# ADR 0006. Durable Outbox и Inbox за фиксированными SQL-границами

- Статус: принято
- Дата: 2026-09-14
- Контекст недели: 3 (Python-периметр)

## Контекст

Сеть не читала нашу диаграмму: provider может принять платёж и потерять ответ, callback может
прийти раньше, чем процесс начал его ждать, dispatcher может умереть между HTTP и отчётом, а
callback — повториться или прийти с другим телом. Порядок сообщений не гарантирован. При этом
PostgreSQL обязан остаться единственным источником истины, а Python-процессы — сменными.

## Решение

### Outbox: запрос существует до HTTP

`payment.prepare_external` одной транзакцией с `workflow.finish_job` создаёт `external_request` и
строку `delivery.outbox`. Идентификатор выводится из `executionId` задания (`ext-<executionId>`),
а `UNIQUE(operation_id)` запрещает второй внешний запрос — retries задания не порождают новый ключ.
Остановленный dispatcher оставляет запрос в `PENDING`.

Dispatcher (роль `outbox_dispatcher`) видит только три функции:

| Функция | Что делает PostgreSQL |
|---|---|
| `delivery.claim_outbox(owner, limit)` | `SKIP LOCKED`, `LEASED`, `lease_version++`, `attempt_count++`, срок аренды из политики профиля |
| `delivery.succeed_outbox(id, owner, lease_version, providerPaymentId)` | условный переход в `DELIVERED`, `external_request` → `SENT` |
| `delivery.fail_outbox(id, owner, lease_version, error_code)` | классификация по коду, `RETRY_WAIT` с `next_attempt_at` из политики или `DEAD` |

Один claim — ровно одна HTTP-попытка. Тело, `Idempotency-Key` и `X-Correlation-ID` — функции
неизменяемой строки Outbox (триггер запрещает их менять), поэтому повтор байтово идентичен, и
lost response не создаёт второй платёж: provider дедуплицирует по ключу.

Повторяемость решает PostgreSQL по самому коду (`transport.error`, HTTP 408/429/5xx), а не по
присланному суффиксу. Истёкшая аренда без отчёта тоже расходует бюджет попыток. Политика
(`max_attempts`, `delays_ms`, `lease_ms`) — строка `delivery.delivery_policy` для профиля `default`
или `test`; какой профиль действует, знает сама база (`workflow.runtime_setting.test_profile`).

`CONFIRMED` — конечное состояние. Его ставит квитанция, и ни `succeed_outbox`, ни `fail_outbox`,
ни прямой UPDATE (триггер `outbox_guard`) не возвращают доставку назад. Подтверждённая доставка
больше не захватывается.

### Inbox: сохранить до ответа, применить отдельно

`payment.receipt_accept_v1` в одной транзакции:

1. проверяет доверенный маркер подписи, версию и известный `externalRequestId`;
2. вставляет `delivery.inbox(message_id, body_hash, process_id, signal_type, RECEIVED)`;
3. вставляет `payment.receipt` со связью с operation/process;
4. переводит Outbox и `external_request` в `CONFIRMED`;
5. отвечает после commit, не дожидаясь применения сигнала.

Тот же `messageId` и тот же SHA-256 точных байт — `DUPLICATE` с сохранённым `state`; другой hash —
`idempotency.conflict`, исходная строка, сигнал и статус не меняются. Повтор с тем же
`Idempotency-Key` возвращает сохранённый результат ещё на уровне `api.invoke`.

Применение — `delivery.reconcile_inbox(limit)`, единственная функция роли `inbox_reconciler`. Для
каждого подходящего `RECEIVED` сообщения она атомарно создаёт (или дедуплицирует) workflow
signal, переводит Inbox в `APPLIED` и применяет сигнал к pinned process. Кандидаты — только
сообщения, по которым решение уже возможно:

- процесс стоит в `WAITING_SIGNAL` на шаге с этим `signal_type` → `APPLIED`;
- ожидание этого типа уже пройдено, процесс завершён или карта такого ожидания не объявляет →
  `CONFLICT`;
- иначе сообщение раннее и остаётся `RECEIVED`, не загораживая очередь.

Вход в `wait_signal` (`workflow.enter_step`) в своей транзакции проверяет Inbox процесса тем же
механизмом, поэтому раннее сообщение не теряется. Проверка generic: она не использует имя карты,
шага или действия — только `process_id` и объявленный картой `signal_type`.

### Порядок блокировок

Взаимоблокировок нет, потому что порядок один на всех путях:

- `process_instance` → строка `inbox` (worker через `finish_job`, reconciler явно; строку inbox оба
  берут `SKIP LOCKED`);
- `external_request` → строка `outbox` (`receipt.accept` и `succeed_outbox`).

## Последствия

- Recreate любого Python-процесса ничего не теряет: очередь — это строки PostgreSQL.
- Exactly-once не обещается: dispatcher и callback — at-least-once, эффект один благодаря ключам
  provider, `messageId` и `executionId`.
- `DEAD` Outbox не валит процесс: поздняя квитанция всё равно подтвердит доставку и будет
  применена. Несколько dispatcher, jitter и операционная работа с `DEAD` — неделя 4.
- Python-роли не имеют ни одного права на таблицы, `CREATE` на схемы/базу, членства в ролях,
  `api.invoke` или workflow-функций — это проверяется и миграцией 019, и e2e-сценарием.
