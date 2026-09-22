# Недели 1–3. Action runtime, workflow-ядро и Python-периметр

Решение первых трёх недель ModuleDev в одном репозитории.

Неделя 1: C# gateway и generic C# runtime, публикующие зарегистрированные PostgreSQL-функции как
HTTP actions. Неделя 2: поверх того же runtime — общий C# worker, версионированные workflow-карты,
персистентное состояние процессов в PostgreSQL, аренда с fencing, ограниченный retry и
восстановление после гибели исполнителя. Неделя 3: два платёжных процесса (`payment-processing` и
`payment-review`) на том же ядре и интеграционный периметр из трёх Python-процессов одного образа —
durable Outbox к выданному provider v0.2.0, anti-corruption layer для его legacy callback с
подписанной квитанцией receipt v1, согласование Inbox, ручное и автоматическое решение с аудитом.

Исходные тексты заданий — [TASK.md](TASK.md) (неделя 1),
[docs/task/week-2-assignment.md](docs/task/week-2-assignment.md) (неделя 2) и
[docs/task/week-3-assignment.md](docs/task/week-3-assignment.md) (неделя 3, полный контракт —
[week-3-contract.md](docs/task/week-3-contract.md),
[week-3-external-contracts.md](docs/task/week-3-external-contracts.md) и
[week-3-configuration.md](docs/task/week-3-configuration.md); редакция `week-3.1`, сняты с пакета
задания на commit `23419a5`, изменены только относительные ссылки). Машинные схемы контракта — в
[`contracts/course-1`](contracts/course-1).

Главное свойство решения не изменилось и распространилось на процессы и периметр: **новое действие
и новая карта процесса добавляются миграцией, манифестом и картой — и исполняются через общие
маршруты без единой правки и пересборки C#-кода.** Обе платёжные карты исполняются тем же
worker'ом без веток по имени flow, шага или действия; `receipt.accept` и `workflow.manual` — обычные
действия generic-маршрута, а не отдельные controllers.

## Решение

### Архитектура

Десять сервисов Docker Compose, одна публичная точка входа.

| Сервис | Технология | Ответственность | Порт |
|---|---|---|---|
| `gateway` | C# ASP.NET Core | Публичный edge: whitelist маршрутов, прозрачная передача контрактных заголовков, health. Не содержит каталог, предметную логику и доступ к PostgreSQL | `127.0.0.1:8080` на host |
| `api` | C# ASP.NET Core | Generic action runtime: JWT, generic-граница подписи, server-side context, разрешение версии, request/response schema, одна Npgsql-транзакция, OpenAPI из манифестов | только внутренний `8080` |
| `cli` | C# console | Course CLI: checksummed migrations, публикация манифестов и карт, trusted-команды процессов. Владеет publication credentials | нет |
| `postgres` | PostgreSQL 17 | Авторитетное состояние: каталог, идемпотентность, процессы, operations, external requests, Outbox, Inbox, receipts, decisions, схема `autocheck` | нет |
| `worker-a` | C# console | Сменный исполнитель процессов, lease owner `worker-a` | нет |
| `worker-b` | C# console | Тот же образ, lease owner `worker-b` | нет |
| `outbox-dispatcher` | Python 3.12 | Outbox → provider: один claim — одна HTTP-попытка, отчёт о ней в PostgreSQL | нет |
| `receipt-adapter` | Python 3.12, тот же образ | Legacy callback provider → signed receipt v1 → gateway. Без учётных данных БД | только внутренний `8082` |
| `inbox-reconciler` | Python 3.12, тот же образ | Вызывает одну функцию согласования Inbox | нет |
| `provider-simulator` | Go, выданный образ по digest | Внешний provider v0.2.0: `POST /payments`, legacy callback, audit | только внутренний `8081` |

Направление вызовов строго одностороннее:

```text
клиент            → gateway (:8080) → api (Compose DNS http://api:8080) → PostgreSQL (api.invoke)
worker            → PostgreSQL (claim_jobs → api.invoke → finish_job / fail_job)
cli               → PostgreSQL (миграции, публикация действий и карт, trusted-команды)
outbox-dispatcher → PostgreSQL (delivery.claim/succeed/fail_outbox) и provider (POST /payments)
provider          → receipt-adapter (legacy callback) → gateway → api → PostgreSQL (receipt.accept)
inbox-reconciler  → PostgreSQL (delivery.reconcile_inbox)
```

- `gateway` обращается к внутреннему `api` по Compose DNS `http://api:8080`, а не через
  `localhost`; loopback-адрес отклоняется при старте.
- `api` не имеет ни одного предметного controller: единственный маршрут действий —
  `POST /api/{module}/{action}`, всё специфичное берётся из опубликованного манифеста.
- Единственная точка предметного выполнения — `api.invoke(...)` в PostgreSQL. Она разрешает
  target только из неизменяемого каталога, повторно проверяет policy по доверенному context,
  проверяет точную сигнатуру функции и вызывает только зарегистрированную функцию.
- `worker` не содержит ни одного HTTP-обработчика и ни одной ветки по имени карты, шага или
  действия: он арендует задание, вызывает через `api.invoke` то, что задано опубликованной картой,
  и сообщает исход. Продвижение процесса решается в PostgreSQL по данным карты.
- Идемпотентность, «ровно одна default-версия действия», «ровно одна активная версия карты» и
  «один процесс на business key» держатся уникальными индексами PostgreSQL, а не блокировками
  внутри процессов C#.
- Что считается повтором, решает маршрут, а не версия: `idempotency_mode` и `idempotency_scope`
  задаёт первая опубликованная версия действия, и каждая следующая обязана их повторить
  (`manifest.conflict` при публикации, триггер на вставку в обход неё). Иначе тот же
  `Idempotency-Key` после activate версии с другим scope исполнил бы действие второй раз.

Схема контейнеров и поток одного вызова: [C4 Container diagram](docs/c4-container.md).

Архитектурные решения:

- [ADR 0001. Граница доверия проходит по проверенному JWT и повторяется в базе](docs/adr/0001-trust-boundary.md);
- [ADR 0002. Технический результат и предметный результат разделены](docs/adr/0002-technical-and-domain-result.md);
- [ADR 0003. Бюджет времени складывается из одного дедлайна, а шлюз держит потолок](docs/adr/0003-deadline-composition.md);
- [ADR 0004. Аренда с fencing вместо блокировки на время выполнения](docs/adr/0004-lease-and-fencing.md);
- [ADR 0005. Граница доверия provider и Python anti-corruption layer](docs/adr/0005-provider-trust-boundary-and-anti-corruption-layer.md);
- [ADR 0006. Durable Outbox и Inbox за фиксированными SQL-границами](docs/adr/0006-durable-outbox-and-inbox.md).

Ключевые файлы:

| Что | Где |
|---|---|
| Generic HTTP executor | [`src/Course.Api/ActionRuntime.cs`](src/Course.Api/ActionRuntime.cs) |
| Generic-граница подписанных запросов | [`src/Course.Shared/SignatureBoundary.cs`](src/Course.Shared/SignatureBoundary.cs) |
| Таблицы периметра и их инварианты | [`db/migrations/017_perimeter_core.sql`](db/migrations/017_perimeter_core.sql) |
| Платёжные действия, `receipt.accept`, `workflow.manual`, `delivery.*` | [`db/migrations/018_perimeter_actions.sql`](db/migrations/018_perimeter_actions.sql) |
| Проекции периметра и права Python-ролей | [`db/migrations/019_perimeter_autocheck.sql`](db/migrations/019_perimeter_autocheck.sql) |
| Семантика идемпотентности, закреплённая за маршрутом | [`db/migrations/021_idempotency_semantics.sql`](db/migrations/021_idempotency_semantics.sql) |
| Python-периметр | [`integration/course_integration`](integration/course_integration) |
| Карты платёжных процессов | [`db/flows/payment-processing-v1.flow.yaml`](db/flows/payment-processing-v1.flow.yaml), [`db/flows/payment-review-v1.flow.yaml`](db/flows/payment-review-v1.flow.yaml) |
| Общий разбор границы `api.invoke` | [`src/Course.Shared/RegisteredAction.cs`](src/Course.Shared/RegisteredAction.cs) |
| Сменный исполнитель процессов | [`src/Course.Workflow.Worker/WorkflowWorker.cs`](src/Course.Workflow.Worker/WorkflowWorker.cs) |
| Семантический валидатор карт | [`src/Course.Shared/WorkflowMapValidator.cs`](src/Course.Shared/WorkflowMapValidator.cs) |
| Единственная точка предметного вызова — действующее определение `api.invoke` (идентичность повтора по точным байтам тела; замещает 009 и 004) | [`db/migrations/020_invoke_body_identity.sql`](db/migrations/020_invoke_body_identity.sql) |
| Определения и состояние процессов | [`db/migrations/011_workflow_core.sql`](db/migrations/011_workflow_core.sql) |
| Продвижение процесса и сигналы | [`db/migrations/012_workflow_engine.sql`](db/migrations/012_workflow_engine.sql) |
| Аренда, fencing и завершение попыток | [`db/migrations/013_workflow_lease.sql`](db/migrations/013_workflow_lease.sql) |
| Проверочные views и права ролей | [`db/migrations/015_workflow_autocheck.sql`](db/migrations/015_workflow_autocheck.sql) |

### Запуск

Prerequisites: Docker Engine 24+, Docker Compose v2 и Python 3.11+ на host (только для генерации
локального окружения и e2e-сценариев). .NET SDK, `psql`, NuGet и pip-пакеты используются только
внутри образов.

У JWT, HMAC-ключа, capability callback и токенов **нет значений по умолчанию** в `compose.yaml`: они
приходят только из окружения запуска. Для локального стенда их генерирует помощник — случайные
synthetic-значения на одну сессию shell плюс JWT `receipt-provider`, подписанный тем же ключом, что
проверяет `api`. На диск ничего не пишется, `.env` не нужен:

```bash
eval "$(python3 scripts/dev_env.py)"
docker compose up -d --build
```

После этого не требуются ручные SQL-команды, ручная публикация actions и карт или настройка
учётных данных: сервис `cli` применяет миграции недель 1–3, публикует встроенные actions (включая
одиннадцать действий недели 3), публикует карты `workflow-smoke` v1/v2, `payment-processing` v1 и
`payment-review` v1 и активирует младшую версию каждой. Только после его успешного завершения
стартует `api`. Python-сервисы и provider стартуют сразу и сами дожидаются базы и gateway.

Роли `outbox_dispatcher` и `inbox_reconciler` создаются при инициализации кластера, поэтому
перед первым запуском недели 3 на томе прежних недель нужен `docker compose down -v`.

Стек самодостаточен и по устройству Compose: ни одного bind mount и ни одного внешнего ресурса —
миграции, манифесты, карты и скрипт создания ролей копируются внутрь образов на этапе сборки.

Ожидаемый результат:

```bash
curl -s http://localhost:8080/health/live    # {"status":"ok","service":"gateway",...}
curl -s http://localhost:8080/health/ready   # {"status":"ok","service":"gateway","check":"api",...}
```

Полный проход встроенной карты. Данные процесса передаются файлом или стандартным вводом:

```bash
echo '{"mode":"signal","value":"demo"}' | docker compose run --rm -T cli \
  flow start workflow-smoke --business-key demo-1 --data /dev/stdin
```

Процесс сразу доходит до устойчивого ожидания сигнала; дальше его двигает сигнал:

```bash
./course.sh flow get <process-id>
echo '{}' | docker compose run --rm -T cli flow signal <process-id> \
  --type training.completed --message-id demo-msg-1 --payload /dev/stdin
```

Платёжный процесс целиком — через provider, adapter и reconciler (токен клиента выпускается тем же
локальным ключом):

```bash
TOKEN=$(python3 -c 'import os,sys; sys.path.insert(0,"scripts"); from dev_env import issue_token; \
  print(issue_token(os.environ["COURSE_JWT_SIGNING_KEY"],"demo","web",["payment:write","payment:read"],3600))')
OP=$(curl -s http://localhost:8080/api/payment/request -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-request-1' \
  -d '{"operationKind":"PAYMENT_EXECUTION","amount":"1000.00","currency":"RUB"}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["result"]["operationId"])')
curl -s http://localhost:8080/api/payment/submit -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-submit-1' -d "{\"operationId\":\"$OP\"}"
sleep 3
curl -s http://localhost:8080/api/operation/events -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d "{\"operationId\":\"$OP\"}"   # ... RECEIPT_APPLIED, OPERATION_COMPLETED
```

Все сценарии недели 3 разом (provider path, adapter, recreate, review, конкуренция, роли, failpoint, порядок блокировок, неизменяемость хранилища):

```bash
python3 scripts/e2e_perimeter.py
```

Остановка и полная очистка состояния:

```bash
docker compose down -v
```

### Конфигурация

Реальные секреты в репозитории отсутствуют. У JWT, HMAC, capability и токенов в `compose.yaml` нет
значений по умолчанию; для паролей PostgreSQL заданы явно обозначенные local-only значения, которые
проверка всегда заменяет synthetic-значениями через собственный Compose override.

| Переменная | Сервис | Значение открытого профиля | Назначение |
|---|---|---|---|
| `COURSE_GATEWAY_PORT` | `gateway` | `8080` | Host-порт на `127.0.0.1` |
| `COURSE_JWT_ISSUER` | `api` | нет default | Ожидаемый `iss` |
| `COURSE_JWT_AUDIENCE` | `api` | нет default | Ожидаемый `aud` |
| `COURSE_JWT_SIGNING_KEY` | `api` | нет default, ≥ 32 байт | HS256-ключ проверки подписи |
| `PROVIDER_HMAC_SECRET` | `api`, `receipt-adapter` | нет default | Ключ HMAC-SHA256 receipt v1: точные UTF-8 байты, без trim и декодирования |
| `PROVIDER_CALLBACK_CAPABILITY` | `receipt-adapter`, `provider-simulator` (`CALLBACK_URL`) | нет default | Непредсказуемый сегмент пути callback |
| `PROVIDER_CALLBACK_TOKEN` | `receipt-adapter` | нет default | JWT principal `receipt-provider`, scope `receipt:write` |
| `RECEIPT_API_URL` | `receipt-adapter` | `http://gateway:8080/api/receipt/accept` | Generic-маршрут через gateway |
| `PROVIDER_URL` | `outbox-dispatcher` | `http://provider-simulator:8081` | Базовый адрес provider |
| `OUTBOX_OWNER` | `outbox-dispatcher` | `outbox-dispatcher` | Стабильный lease owner Outbox |
| `PROVIDER_AUDIT_TOKEN` | `provider-simulator` | нет default | Токен audit endpoint provider (нужен только проверке) |
| `PROVIDER_SIMULATOR_MODE` | `provider-simulator` | `success` | Режим simulator для ручных экспериментов |
| `PROVIDER_TIMEOUT_MS` | `outbox-dispatcher` | `500` в test profile, иначе `5000` | Бюджет одной HTTP-попытки |
| `OUTBOX_POLL_INTERVAL_MS` / `INBOX_POLL_INTERVAL_MS` | dispatcher / reconciler | `100` / `200` в test profile | Интервал опроса; у reconciler в test profile не более 500 мс |
| `RECEIPT_ADAPTER_HOST`, `RECEIPT_ADAPTER_PORT`, `RECEIPT_API_TIMEOUT_MS` | `receipt-adapter` | `0.0.0.0`, `8082`, `10000` | Адрес и порт внутри сети Compose, бюджет ожидания API |
| `INBOX_BATCH_SIZE` | `inbox-reconciler` | `50` | Сколько сообщений Inbox согласуется одним вызовом `delivery.reconcile_inbox` (1–100) |
| `COURSE_POSTGRES_STATEMENT_TIMEOUT_MS` | dispatcher, reconciler | `10000` | Потолок одного вызова SQL-границы: ожидание блокировки заканчивается ошибкой и переподключением, а не зависанием |
| `COURSE_OUTBOX_PASSWORD`, `COURSE_INBOX_PASSWORD` | `postgres`, dispatcher / reconciler | local-only значения | Пароли ролей `outbox_dispatcher` и `inbox_reconciler` |
| `COURSE_API_BASE_URL` | `gateway` | `http://api:8080` | Внутренний адрес по Compose DNS |
| `COURSE_POSTGRES_HOST` / `_PORT` / `_DB` | `api`, `cli`, `worker-*` | `postgres` / `5432` / `course` | Адрес авторитетного состояния |
| `COURSE_POSTGRES_USER` / `_PASSWORD` | `api` | `course_runtime` | Runtime-роль без прямого DML |
| `COURSE_POSTGRES_USER` / `_PASSWORD` | `cli` | `course_migrator` | Роль миграций (DDL), нужна только `migration apply` |
| `COURSE_PUBLISHER_USER` / `_PASSWORD` | `cli` | `course_publisher` | Роль публикации каталога и карт: только определения, без запуска процессов. Обязательна: отката к роли миграций нет — он бы сработал и молча вернул объединение прав |
| `COURSE_OPERATOR_USER` / `_PASSWORD` | `cli` | `course_operator` | Роль операций: запуск, сигналы, чтение процессов и проба fencing; публиковать не может. Тоже обязательна |
| `COURSE_POSTGRES_USER` / `_PASSWORD` | `worker-*` | `workflow_worker` | Роль исполнителя: `EXECUTE` ровно на четыре функции-границы |
| `COURSE_WORKER_ID` | `worker-*` | `worker-a` / `worker-b` | Идентификатор арендатора; он же `instanceId` в структурных событиях |
| `COURSE_TEST_PROFILE` | `api`, `cli`, `worker-*` | `0` | `1` включает короткую аренду, частый опрос, failpoints и `flow test-finish` |
| `COURSE_FAILPOINT` | `worker-*` | пусто | `after_job_claim` или `after_action_before_finish` |
| `COURSE_WORKER_LEASE_MS` / `COURSE_WORKER_POLL_MS` | `worker-*` | профиль | Явное переопределение срока аренды и интервала опроса |
| `COURSE_MIGRATOR_PASSWORD`, `COURSE_PUBLISHER_PASSWORD`, `COURSE_RUNTIME_PASSWORD`, `COURSE_OPERATOR_PASSWORD`, `COURSE_WORKER_PASSWORD`, `COURSE_POSTGRES_PASSWORD` | `postgres` | локальные значения | Пароли ролей при инициализации кластера |
| `COURSE_BOOTSTRAP_MIGRATIONS` / `_ACTIONS` / `_FLOWS` | `cli` | `/app/db/...` | Источники стартовой инициализации |
| `COURSE_GATEWAY_UPSTREAM_TIMEOUT` | `gateway` | `35` секунд | Бюджет ожидания `api`; заведомо больше контрактного потолка `timeout_ms` (30 с) |
| `COURSE_MAX_RESULT_BYTES` | `api`, `worker-*` | `524288` | Верхняя граница конверта действия до его разбора |
| `COURSE_GATEWAY_MAX_RESPONSE_BYTES` | `gateway` | `2097152` | Верхняя граница ответа, который шлюз пересылает клиенту; заведомо больше `COURSE_MAX_RESULT_BYTES`. Превышение даёт контрактный `action.contract_violation`, а если часть тела уже ушла — обрыв соединения, но никогда не молча усечённый ответ |
| `COURSE_DISPATCH_QUEUE_CAPACITY` | `api` | `2048` | Глубина очереди технических следов вызовов |

Роли PostgreSQL создаются один раз при инициализации кластера
([`db/init/00-roles.sh`](db/init/00-roles.sh)):

- `course_owner` — `NOLOGIN`, владелец всех схем, таблиц и `SECURITY DEFINER` функций;
- `course_migrator` — роль миграций сервиса `cli`, выполняет DDL под `SET ROLE course_owner`;
  используется **только** командой `migration apply`;
- `course_publisher` — роль публикации сервиса `cli`: команды `action *` и `flow validate/publish/
  list/activate`. Запускать процессы, принимать сигналы и завершать задания она **не может**;
- `course_operator` — роль операций сервиса `cli`: команды `flow start/get/signal/test-finish`.
  Публиковать или активировать карты она **не может** — разделение ровно обратное публикатору,
  поэтому ни один набор учётных данных не даёт сразу и определение, и исполнение;
- `course_runtime` — runtime-роль сервиса `api`: `EXECUTE` на `api.invoke`, `SELECT` на views
  `autocheck` и никакого прямого DML; доступа к схеме `workflow` не имеет вовсе;
- `workflow_worker` — роль исполнителя процессов: `EXECUTE` ровно на `workflow.claim_jobs`,
  `api.invoke`, `workflow.finish_job`, `workflow.fail_job`, ноль прав `INSERT`/`UPDATE`/`DELETE` на
  любую таблицу и ни одной другой функции схем `api` и `workflow`;
- `outbox_dispatcher` — роль Python-диспетчера: `EXECUTE` ровно на `delivery.claim_outbox`,
  `delivery.succeed_outbox`, `delivery.fail_outbox`; ни одного права на таблицы и views, `CREATE`
  на схемы и базу, членства в ролях, `api.invoke` или workflow-функций;
- `inbox_reconciler` — роль Python-согласователя: `EXECUTE` ровно на `delivery.reconcile_inbox`
  и те же запреты. У `receipt-adapter` учётных данных PostgreSQL нет вовсе.

### Миграции

Миграции — обычные `.sql` файлы в [`db/migrations`](db/migrations), применяются **только**
сервисом `cli` через Course CLI:

```bash
docker compose run --rm -T cli migration apply /app/db/migrations
```

Правила применения:

- файлы выполняются в лексикографическом порядке, по одной транзакции на файл;
- для каждого файла хранится SHA-256; повтор с тем же checksum безопасен и отмечается как
  `skipped`;
- изменение уже применённого файла даёт `migration.conflict` и ненулевой exit code;
- каждый файл выполняется под `SET ROLE course_owner`, поэтому владельцем объектов и
  `SECURITY DEFINER` функций всегда становится роль без права входа;
- журнал применения хранится в `course_meta.schema_migration`.

Данные PostgreSQL хранятся в named volume `postgres-data` и переживают удаление и пересоздание
контейнеров `gateway`, `api` и обоих `worker`.

### Workflow-карты

Карта процесса — это опубликованный документ `course-1` в JSON или YAML
([схема](contracts/course-1/workflow-map.schema.json)). Один и тот же контракт, записанный двумя
способами, даёт одну и ту же версию: YAML разбирается по core schema, а документ нормализуется
перед публикацией, поэтому повторная публикация из другого формата — это повтор, а не конфликт.

Четыре типа шагов и переход по конечному исходу:

| Тип | Поведение |
|---|---|
| `automatic` | Создаёт задание и вызывает зарегистрированное действие |
| `wait_signal` | Устойчиво ждёт идемпотентный сигнал |
| `manual` | Устойчиво переводит процесс в `WAITING_MANUAL` |
| `end` | Завершает технический процесс с объявленным исходом |

Публикация создаёт **неизменяемую** версию, но не активирует её. `flow activate` атомарно выбирает
ровно одну версию для новых экземпляров; уже запущенный процесс навсегда остаётся на своей.

До публикации карта проходит три независимые проверки. JSON Schema доказывает форму документа,
семантический валидатор — смысл, а само хранилище — те инварианты, которые обязаны держаться и
тогда, когда карту публикуют минуя CLI.

`workflow.publish_flow` — граница, выданная отдельной роли `course_publisher`, а JSON Schema живёт
в клиенте, поэтому функция проверяет локальные инварианты сама: `automatic`-шаг обязан нести
`task`, а никакой другой — не имеет права его нести; `start_step` и оба конца каждого перехода
обязаны быть объявлены. Строчная форма шага (`wait_signal` → `signal_type` и `outcome`, `end` →
`outcome`, `manual` → непустой `allowed_outcomes`) закреплена CHECK-ограничениями
`workflow.step_definition`. Отказ происходит до первой записи и не оставляет строк. Достижимость,
ацикличность и покрытие исходов остаются за валидатором: это свойства графа, а не документа.

Семантический валидатор ([`WorkflowMapValidator`](src/Course.Shared/WorkflowMapValidator.cs))
проверяет: один существующий `start_step`, уникальные ключи шагов, достижимость всех шагов и хотя бы
одного `end`, отсутствие циклов и тупиков, существование и включённость версии действия, точное
равенство множеств `required_policy` карты и действия, достаточность server-side scopes
`workflow-worker`, ровно один переход на каждый объявленный исход, отсутствие переходов из `end`,
корректность указателей RFC 6901 и их непересечение, покрытие **обязательных полей запроса
действия** константами и `input_mapping` шага, длина `delays_ms`, равная `max_attempts - 1`,
и — сверх обязательного списка — **идемпотентность действия**:
`automatic`-шаг нельзя связать с действием, объявившим `idempotency_mode: none`, потому что
доставка задания at-least-once, и без ключа идемпотентности первый же перехват аренды создал бы
второй предметный эффект. Это единственное ужесточение сверх контракта курса, и оно сознательное:
принять такую карту значило бы принять карту, которая гарантированно нарушит главный инвариант
недели. Повторяющийся `signal_type` среди шагов ожидания, наоборот, **не** запрещается — граф
ацикличен, и накопленные сигналы применяются в порядке получения, так что двусмысленности нет.
Факты каталога — включая схему запроса действия — валидатор берёт из авторитетного состояния
(`workflow.publication_facts`), а не угадывает. Проверка покрытия обязательных полей нужна именно
до публикации: версия неизменяема, и шаг, чей payload не может пройти схему запроса, отказывал бы
на каждом запуске, тратя весь бюджет повторов.

`input_mapping` направлен «target payload pointer → source process data pointer»: ключ адресует
поле payload действия, значение — поле данных процесса. Пересечение целевых указателей считается по
сегментам, поэтому `/a` пересекается с `/a/b`, но не с `/ab`. Отсутствующий источник даёт
неповторяемый `workflow.mapping_missing`, и действие не вызывается вообще.

Команды:

```bash
./course.sh flow validate /app/db/flows/workflow-smoke-v1.flow.json
./course.sh flow publish  /app/db/flows/workflow-smoke-v1.flow.json
./course.sh flow list
./course.sh flow activate workflow-smoke --version 1
./course.sh flow start workflow-smoke --business-key demo-1 --data /dev/stdin
./course.sh flow get <process-id>
./course.sh flow signal <process-id> --type training.completed --message-id m-1 --payload /dev/stdin
```

`flow start` идемпотентен по паре `flow_name` + `business_key`: одинаковые данные возвращают
прежний процесс, изменённые дают `workflow.start_conflict` — даже после смены активной версии.
`message-id` сигнала глобально уникален: повтор с тем же процессом, типом и телом возвращает
`duplicate`, любое расхождение — `workflow.signal_conflict` без изменения сохранённой строки.
Сигнал объявленного картой типа, пришедший раньше входа в ожидание, хранится как `ACCEPTED` и
атомарно применяется при входе в соответствующий `wait_signal`.

### Worker

`worker-a` и `worker-b` — это **один и тот же образ**, запущенный дважды. Отличие ровно одно:
`COURSE_WORKER_ID`, он же lease owner. В образе нет ни одной ветки по имени карты, шага или
действия, поэтому неизвестная на момент сборки карта исполняется без пересборки.

Раздел `build` объявлен только у `worker-a`: два build-раздела с одним и тем же тегом собираются
параллельно и периодически падают с «image ... already exists». `worker-b` ссылается на тот же
образ по имени и объявляет `pull_policy: never` — тянуть его из реестра нечего, и попытка pull
должна быть явной ошибкой конфигурации, а не невнятным отказом сети.

Цикл исполнителя:

```text
claim_jobs(owner)                     короткая транзакция; lease_version++ и новая попытка
  → сборка payload из констант и mapping
  → проверка payload по request_schema действия
  → BEGIN
       api.invoke(... доверенный контекст с executionId ...)
       проверка конверта, объявленности исхода и result по response_schema
       workflow.finish_job(jobId, owner, leaseVersion, outcome, result, dispatch)
     COMMIT
```

Предметный эффект и успешное завершение задания фиксируются **одной транзакцией Npgsql**: либо оба,
либо ни одного. Ошибка действия, нарушение контракта или таймаут откатывают транзакцию целиком, и
только после отката отдельная транзакция `workflow.fail_job` сохраняет попытку и расписание повтора.

Идентификаторы исполнения:

| Поле | Семантика |
|---|---|
| `jobId` | Одна логическая работа |
| `executionId` | Стабильный ключ идемпотентности предметного эффекта для всех повторов задания |
| `attemptId` | Конкретная попытка |
| `leaseVersion` | Возрастающая версия права на завершение |

Срок аренды не берётся из конфигурации вслепую, но и не отдаётся карте целиком: `claim_jobs`
вычисляет его как `min(max(настроенный срок, timeout_ms + 500 мс), настроенный срок × 3)`. Нижняя
граница не даёт перехватить задание на середине работы; верхняя не даёт карте задавать наблюдаемое
время восстановления — иначе задача с бюджетом в полминуты отодвигала бы перехват настолько же.

При перехвате истёкшей аренды `jobId` и `executionId` сохраняются, создаётся новая попытка и
растёт `leaseVersion`. `finish_job` принимает завершение только при совпадении задания, владельца,
версии аренды и ожидаемого состояния — «опоздавший» исполнитель не меняет ни задание, ни процесс,
ни предметные данные. Подробности решения — в
[ADR 0004](docs/adr/0004-lease-and-fencing.md).

Поверх бюджета отказов действует абсолютный потолок попыток `max(max_attempts × factor, minimum)`
из `workflow.runtime_setting`: бюджет `max_attempts` тратят только отказы самого действия, поэтому
сбой, не доходящий до `fail_job` (например несогласованность закреплённой карты, обнаруженная уже
на границе завершения), иначе перезахватывался бы вечно. Причина смерти различима:
`workflow_job.dead_reason` — `failure_budget` с событием `TaskFailed` либо `attempt_ceiling` с
событием `TaskAbandoned`. Причина видна и снаружи: `autocheck.jobs.dead_reason`.

Отказ границы завершения различается по коду. `workflow.lease_stale` означает, что заданием уже
владеет другой арендатор, и исполнителю нечего добавить. Любой другой отказ означает, что аренда
ещё наша, а карта и действие не согласованы, — такая попытка закрывается через `fail_job`
неповторяемым отказом, а не оставляется молча: иначе задание доживало бы до потолка попыток и
причина подменялась бы на `attempt_ceiling`.

Ограниченный retry: `max_attempts` включает первое исполнение, `delays_ms` содержит ровно
`max_attempts - 1` значений, после retryable-отказа номер N берётся `delays_ms[N - 1]`. Повторяются
только `retryable=true` и таймаут; ошибка mapping, необъявленный исход и нарушение схемы ответа не
повторяются. Истёкшая аренда оставляет попытку `STALE`, увеличивает `attempt_count`, но не
расходует бюджет отказов.

Test profile (`COURSE_TEST_PROFILE=1`): аренда 2 секунды, опрос 50 мс, доступны два failpoint.
Признак профиля записывается командой `bootstrap` в саму базу (`workflow.runtime_setting`), и
проба `flow test-finish` проверяется по нему: обход арендатора — вопрос прав в базе, а не
переменной окружения того, кто запустил клиент.
При достижении failpoint исполнитель печатает ровно одну структурную строку и блокируется:

```json
{"event":"failpoint.reached","name":"after_job_claim","instanceId":"worker-a"}
```

| Failpoint | Точная граница |
|---|---|
| `after_job_claim` | После commit аренды и попытки, до `api.invoke` |
| `after_action_before_finish` | После эффекта и проверки контракта внутри транзакции, до `finish_job` |

### Платёжные процессы

`payment.submit` принимает только `operationId` и `Idempotency-Key`. Карту выбирает серверная
таблица `payment.flow_binding`: `PAYMENT_EXECUTION` → `payment-processing`, `PAYMENT_APPROVAL` →
`payment-review`. Одна транзакция: operation `CREATED → PROCESSING`, процесс, закреплённый за
активной версией, первый job, событие `OPERATION_SUBMITTED` и результат идемпотентности. Строка
операции блокируется, поэтому конкурентные submit одной операции дают один процесс; любой повтор
(с любым ключом, после завершения или смены default-версии карты) возвращает исходный subject
result со `status = PROCESSING`. Процесс с тем же business key, запущенный в обход submit (например
`flow start`), к операции не привязывается — `422 payment.process_conflict`. Жизненный цикл
`CREATED → PROCESSING → COMPLETED | REJECTED`, «процесс есть ровно у отправленной операции» и
однократная привязка закреплены CHECK и триггером `payment.operation`.

```text
payment-processing v1 (выданная карта)
  validate_operation → prepare_external_request → wait_receipt (payment.receipt) → apply_receipt
    → [COMPLETED] complete_operation → done
    → [REJECTED]  reject_operation   → done

payment-review v1
  validate_operation → check_limit
    → [WITHIN_LIMIT]    approve_operation → done
    → [REVIEW_REQUIRED] wait_manual_decision
                          → [APPROVED] approve_operation → done
                          → [REJECTED] reject_operation  → done
```

| Действие | Policy | Что делает в PostgreSQL |
|---|---|---|
| `payment.submit` | `payment:write` | binding, `workflow.start_process`, `OPERATION_SUBMITTED` |
| `operation.events` | `payment:read` | история операции без payload |
| `payment.validate` | `payment:internal` | operation принадлежит процессу, `PROCESSING`, карта совпадает с binding |
| `payment.prepare_external` | `payment:internal` | `external_request` (`ext-<executionId>`) + Outbox одной транзакцией с `finish_job` |
| `payment.apply_receipt` | `payment:internal` | outcome только из квитанции, применённой к процессу через Inbox |
| `payment.complete` / `payment.reject` | `payment:internal` | финальный статус только при применённой квитанции или записанном решении |
| `payment.check_limit` | `payment:internal` | правило `course-limit-v1`: до `100000.00 RUB` включительно — решение `LIMIT_RULE APPROVED` |
| `payment.approve` | `payment:internal` | финальный статус только при решении `APPROVED` |
| `receipt.accept` | `receipt:write` | маркер подписи, Inbox + receipt, подтверждение Outbox |
| `workflow.manual` | `workflow:manual` | решение, событие и следующий job одной транзакцией |

`payment:internal` есть только у server-side principal `workflow-worker`, поэтому шаги процесса
нельзя вызвать клиентским токеном через публичный маршрут. Все automatic-действия идемпотентны по
`executionId`.

`workflow.manual` — generic-действие ядра, а не платёжное: решение допустимо только для текущего
`manual`-шага закреплённой версии, `decision` обязан входить в его `allowed_outcomes`, principal
берётся из JWT, в аудит (`workflow.decision`) пишется SHA-256 reason, а не сам текст. Процесс
блокируется, `UNIQUE(step_instance_id)` не даёт зафиксировать второе решение: конкурирующее
решение получает `409 workflow.decision_conflict`, изменённый payload с тем же ключом —
`409 idempotency.conflict`, идентичный повтор — те же `status`, `outcome` и `result`.

### Python-периметр

Один пакет [`integration/course_integration`](integration/course_integration), один локально
собранный образ ([`docker/integration.Dockerfile`](docker/integration.Dockerfile), Python 3.12) и три
entrypoint: главный процесс контейнера — сам интерпретатор (`python -m course_integration
<entrypoint>`), без shell-обёртки, поэтому он получает SIGTERM и останавливается сразу.

| Процесс | Вход | Выход | Граница | Не делает |
|---|---|---|---|---|
| `outbox-dispatcher` | `delivery.claim_outbox` | `POST /payments` | роль `outbox_dispatcher`, три функции | не читает таблицы, не вызывает `api.invoke`, не решает о повторе |
| `receipt-adapter` | legacy callback | signed receipt v1 → gateway | JWT `receipt-provider`, HMAC | не подключается к PostgreSQL, не хранит состояние, не повторяет вызов API |
| `inbox-reconciler` | `delivery.reconcile_inbox` | persisted workflow signal | роль `inbox_reconciler`, одна функция | не исполняет flow, не выбирает переход |

Сквозные идентификаторы:

```text
requestId → operationId → processId → jobId → executionId
                                              → externalRequestId (= ext-<executionId>) → providerPaymentId = messageId
```

**Dispatcher.** Один claim — ровно одна HTTP-попытка: `POST {PROVIDER_URL}/payments`,
`Idempotency-Key = externalRequestId`, `X-Correlation-ID` из строки Outbox и тело
`{"operationId":"<externalRequestId>","amount":"...","currency":"RUB"}` (legacy-поле `operationId`
намеренно несёт `externalRequestId`). Всё это — функции неизменяемой строки Outbox, поэтому каждый
повтор байтово идентичен. Redirect не выполняется. Классификация:

| Результат попытки | Отчёт |
|---|---|
| `202` и строгое тело `{"providerPaymentId":"...","status":"ACCEPTED"}` | `succeed_outbox` |
| transport error, timeout | `fail_outbox(..., 'transport.error.retryable')` |
| `408`, `429`, `5xx` | `fail_outbox(..., 'http.<status>.retryable')` |
| прочий статус, включая `200` и `3xx` | `fail_outbox(..., 'http.<status>.terminal')` |
| `202` с некорректным UTF-8/JSON/телом | `fail_outbox(..., 'response.invalid.terminal')` |

Состояние, `next_attempt_at` и предел попыток определяет PostgreSQL (`delivery.delivery_policy`: в
test profile 3 попытки, задержки 200/400/800 мс, аренда 3 с). Если база пропала между HTTP и
отчётом, аренда истекает и следующий claim повторяет те же ключ и тело — provider дедуплицирует.

**Adapter.** `POST /callbacks/provider-v02/{capability}` на внутреннем порту `8082`:

| Ситуация | Ответ |
|---|---|
| неверный путь, метод или capability (сравнение за постоянное время) | `404`, пустое тело |
| `Content-Length` отсутствует, повторён, больше 64 KiB; `Transfer-Encoding` | `400`, пустое тело |
| не UTF-8, не JSON, дубли ключей, `NaN`, unknown/missing fields, неверные типы и длины, CR/LF, `occurredAt` не RFC 3339 UTC | `400`, пустое тело |
| API ответил | тот же статус, тело и `Content-Type` |
| timeout, connection error, недоступный API | `503 {"status":"error","code":"dependency.unavailable"}` |

Отображение: `providerPaymentId → messageId` и `providerPaymentId`, `operationId → externalRequestId`,
`result → outcome`, `occurredAt` — исходная строка с любой точностью, `message` проверяется и
отбрасывается. Квитанция сериализуется так же, как
`json.dumps(receipt, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")`,
HMAC-SHA256 считается над этими байтами, и ровно эти байты уходят в тело запроса. Журнал доступа
`http.server` отключён: строка запроса содержит capability.

**Reconciler.** Раз в интервал (≤ 500 мс в test profile) вызывает `delivery.reconcile_inbox(50)`;
если что-то применено — сразу следующий вызов. Очередь — только строки PostgreSQL, поэтому
recreate ничего не теряет.

**Сигнатура и Inbox в C#/PostgreSQL.** `api` проверяет `X-Provider-Signature` над точными байтами
до разбора JSON: неверный формат/версия/HMAC — `401 signature.invalid`, target не вызывается;
верная подпись добавляет в context только `transport.signatureVerified = true` и
`transport.signatureVersion = 1`. `receipt.accept` без маркера — `403 receipt.signature_required`.
Далее одной транзакцией: версия и известный `externalRequestId` (иначе `422
receipt.external_request_not_found`), Inbox с глобальным `messageId` и SHA-256 точных байт,
receipt, Outbox и `external_request` → `CONFIRMED`. Тот же `messageId` и hash — `DUPLICATE` с
сохранённым `state` Inbox (`RECEIVED`, `APPLIED` или `CONFLICT` у сообщения, опоздавшего к уже
применённой квитанции), другой hash — `409 idempotency.conflict`. Повтор с тем же `Idempotency-Key` сравнивается по тем же точным
байтам ([020](db/migrations/020_invoke_body_identity.sql)): байтово другое, даже корректно
подписанное тело — тоже конфликт, а не replay. Заголовок подписи описан в OpenAPI каждого действия
как необязательный с форматом `^v1=[0-9a-f]{64}$`. Раннее сообщение ждёт в `RECEIVED`: его применит reconciler или
сам вход процесса в `wait_signal`. Подробности и порядок блокировок — в
[ADR 0006](docs/adr/0006-durable-outbox-and-inbox.md).

Логи всех трёх процессов — структурные JSON-строки: идентификаторы, статусы, коды, классы ошибок.
Callback `message`, тела, токены, подписи, capability и тексты ошибок драйвера туда не попадают.

### Provider

Используется выданный образ, закреплённый по digest:

```text
ghcr.io/fintech-dev-lab/internship-provider-simulator:v0.2.0@sha256:70e5e0dd9ab8425be84de431ec74516f9bedf5d5529077358e2e2b2037fe0c74
```

Provider v0.2.0 — внешний Go-компонент со своим legacy wire contract; он не знает про receipt v1 и
ничего не подписывает. Доверие к его callback ограничено сетью Compose и capability-сегментом
пути, HMAC защищает только участок adapter → platform
([ADR 0005](docs/adr/0005-provider-trust-boundary-and-anti-corruption-layer.md)).

| Параметр в `compose.yaml` | Значение |
|---|---|
| `CALLBACK_URL` | `http://receipt-adapter:8082/callbacks/provider-v02/${PROVIDER_CALLBACK_CAPABILITY}` |
| `SIMULATOR_MODE` | `${PROVIDER_SIMULATOR_MODE:-success}`: `success`, `reject`, `delay`, `duplicate`, `lost-response`, `early-callback`, `conflicting-callback`, `transient-error` |
| `CALLBACK_DELAY` / `CALLBACK_TIMEOUT` / `CALLBACK_MAX_ATTEMPTS` / `CALLBACK_RETRY_DELAY` | `200ms` / `1s` / `5` / `200ms` |
| `AUDIT_TOKEN` | `${PROVIDER_AUDIT_TOKEN}` — `GET /internal/audit/{externalRequestId}` c `X-Audit-Token` |

Главный инвариант при `lost-response` и `transient-error`: число HTTP-попыток может быть больше
одной, но audit provider показывает `paymentCount = 1`. Память simulator не переживает его
recreate — это свойство внешнего компонента, а не решения. Пересоздать provider в другом режиме:

```bash
PROVIDER_SIMULATOR_MODE=lost-response docker compose up -d --no-deps provider-simulator
```

### Проверка

Открытая black-box проверка недели 3 живёт в отдельном репозитории задания и получает решение
явно; её `check.sh` не копируется поверх скриптов прежних недель:

```bash
git clone https://github.com/fintech-dev-lab/moduledev-week-3-python-perimeter-task.git
./moduledev-week-3-python-perimeter-task/check.sh --repo /path/to/this/repository
```

Checker выполняет Compose admission и cold build в изолированном проекте с synthetic secrets,
проверяет один локально собранный Python-образ и Python-runtime трёх процессов, останавливает и
запускает dispatcher, проводит provider path, missing/invalid signature, duplicate/conflict
callback, обе ветви review, пересоздаёт Python-сервисы, проверяет роли и отсутствие секретов в
логах и пишет `week-3-public-report.json` (не хранится в Git).

Проверка недель 1–2 по-прежнему запускается из корня решения:

```bash
./check.sh
```

Скрипт поднимает стек в отдельном Compose-проекте, выполняет cold build, подменяет JWT signing key
через собственный override, монтирует доверенные fixtures и записывает отчёт
`week-2-public-report.json` (файл не хранится в Git). Ненулевой exit code означает провал хотя бы
одной обязательной проверки.

Контрактные команды CLI, которые использует проверка:

```bash
docker compose run --rm -T --no-deps cli migration apply /autocheck/input/migrations
docker compose run --rm -T --no-deps cli action publish  /autocheck/input/manifests/<file>.action.json
docker compose run --rm -T --no-deps cli flow validate   /autocheck/input/maps/<file>.flow.json
docker compose run --rm -T --no-deps cli flow publish    /autocheck/input/maps/<file>.flow.yaml
docker compose run --rm -T --no-deps cli flow activate   <flow> --version 2
docker compose run --rm -T --no-deps cli flow start      <flow> --business-key <key> --data /dev/stdin
docker compose run --rm -T --no-deps cli flow signal     <process-id> --type <type> --message-id <id> --payload /dev/stdin
docker compose run --rm -T --no-deps cli flow test-finish <job-id> --owner worker-a --lease-version 1 --outcome <outcome> --result /dev/stdin
```

Собственные unit-тесты закрывают границы, которые проверяются без Docker: форма claims JWT, JSON
Schema с обязательной проверкой `format`, матрица кодов ошибок, построение OpenAPI, указатели
RFC 6901, согласование срока аренды с бюджетом границы завершения (включая разбор `leaseUntil` в
том виде, в каком его отдаёт PostgreSQL), на опубликованных fixtures проверки — приём валидных
карт и отклонение каждой заведомо испорченной, копирование с жёсткой границей, которым шлюз
пересылает ответ, и бит исполнения скриптов, которые README предлагает запускать напрямую.

```bash
dotnet test src/Course.sln
```

Бит исполнения читается из индекса Git (`git ls-files --stage`), потому что на Windows файловая
система его не хранит; поэтому unit-набору нужен git-checkout и `git` в `PATH` — в распакованном
архиве без `.git` четыре случая этой проверки (по одному на скрипт) падают.

Неделя 3 добавляет unit-тесты generic-границы подписи (точные байты, сырой UTF-8 ключ, строгий
формат `v1`, коды `401/403/409`), request schema `receipt.accept` (любая точность `occurredAt`,
запрет unknown fields, версий и CR/LF), описания подписи в OpenAPI и классификации отказов
PostgreSQL (deadlock и serialization failure — повторяемый `503`).

Python unit/contract-тесты периметра — только стандартная библиотека, без Docker и без драйвера
PostgreSQL:

```bash
cd integration && python3 -m unittest discover -s tests -t .
```

Они закрывают строгую валидацию legacy callback, отображение всех полей и сохранение строки
`occurredAt`, compact sorted bytes, HMAC с сырым секретом, классификацию ответов provider (на
живом локальном HTTP-сервере: redirect не выполняется, timeout и refused — retryable), неизменность
ключа и тела между повторами, ответы adapter `404/400/passthrough/503`, отсутствие секретов и
текста callback в журнале и отказ конфигурации без секретов и URL.

End-to-end сценарии недели 3 на поднятом стеке —
[`scripts/e2e_perimeter.py`](scripts/e2e_perimeter.py): durable Outbox при остановленном dispatcher,
отказ missing/invalid/replayed signature, wrong version и unknown `externalRequestId` без мутаций,
`paymentCount = 1`, duplicate/conflict callback, recreate Python-сервисов с непримененным Inbox,
отсутствие повторной доставки подтверждённого Outbox (барьер — доставленный sentinel-платёж, а не
пауза), конфликт байтово другого подписанного тела под тем же ключом, auto/manual review с
аудитом, гонка из восьми ручных решений, 20 конкурентных submit одной операции, запреты
Python-ролей, failpoint `after_outbox_claim` (остановленный dispatcher не доходит до provider, а
новый перехватывает аренду со следом `lease.expired.retryable` и одним платежом), квитанция во время
шага процесса на барьерах PostgreSQL (встаёт в очередь за шагом, а не получает deadlock), отказ
submit привязать операцию к процессу, запущенному в обход, и триггеры неизменяемости Outbox, Inbox,
receipts, decisions, flow binding и жизненного цикла операции.

Интеграционные тесты закрывают то, что без живого контура доказать нельзя: гонку версий между
валидацией и исполнением, повтор по ключу идемпотентности через смену версии по умолчанию,
отказ публикации версии с другой семантикой идемпотентности (и вставки её в обход публикации),
конкурентные команды каталога и границы прав ролей, границу ответа шлюза на настоящем ответе api
(контрактный отказ вместо усечённого тела), а для недели 2 — неизменяемость опубликованной
версии карты, append-only историю, неизменяемость личности состояния (ключ идемпотентности,
закрепление за версией, тело принятого сигнала), отклонение испорченной карты самим хранилищем в
обход клиента, границы роли `workflow_worker`, конкурентные `flow activate` и `flow start`, полный
проход `workflow-smoke` через сигнал с ровно одним предметным эффектом и честный отчёт об отказе
границы завершения, не связанном с потерей аренды.

Аварийные границы недели 2 закреплены собственными регрессиями на настоящих `claim_jobs` и
`finish_job`, а не только проверкой курса: перехват истёкшей аренды вторым арендатором и отказ
`workflow.lease_stale` опоздавшему `finish` без сдвига задания и процесса; гибель настоящего
worker на failpoint `after_action_before_finish` — ни одного эффекта до восстановления, затем ровно
один эффект при тех же `jobId`/`executionId`, новой попытке и истории `STALE,SUCCEEDED`; повтор
сигнала с тем же телом — дубликат, с другим — `workflow.signal_conflict` без перезаписи
сохранённого; и отсутствие исходного текста ошибки PostgreSQL (маркер в имени роли) в stdout и
stderr CLI. На время первых двух штатные worker'ы останавливаются.

```bash
./scripts/integration-tests.sh
```

Скрипт генерирует одноразовое synthetic-окружение (если оно не задано), поднимает отдельный
Compose-проект, выполняет C# интеграционные тесты и e2e-сценарии периметра и гасит контур за
собой. Без поднятого контура C#-тесты помечаются пропущенными, поэтому `dotnet test` без Docker
остаётся зелёным.

Тесты дополняют checker, но не заменяют его.

### Диагностика

```bash
# состояние сервисов
docker compose ps

# логи (JWT, ключи и полный payload в них не попадают)
docker compose logs -f gateway
docker compose logs -f api
docker compose logs cli
docker compose logs worker-a worker-b

# health
curl -i http://localhost:8080/health/live     # процесс gateway
curl -i http://localhost:8080/health/ready    # готовность api и PostgreSQL

# компактный снимок процесса
./course.sh flow get <process-id>
curl -s http://localhost:8080/api/workflow/get -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"processId":"<process-id>"}'

# авторитетное состояние и проверочные проекции
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.flow_versions;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.processes;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.steps;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.jobs;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.attempts;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.signals;"
docker compose exec -T postgres psql -U postgres -d course -c "SELECT event_type, occurred_at FROM autocheck.workflow_events ORDER BY occurred_at;"
docker compose exec -T postgres psql -U postgres -d course -c "SELECT request_id, module, action, version, principal, status, outcome FROM autocheck.action_dispatches ORDER BY occurred_at DESC LIMIT 10;"

# Python-периметр: структурные JSON-логи
docker compose logs -f outbox-dispatcher receipt-adapter inbox-reconciler provider-simulator

# путь одного платежа по operationId
docker compose exec -T postgres psql -U postgres -d course -c "
  SELECT o.status, e.external_request_id, e.state AS external_state, x.state AS outbox_state,
         x.attempt_count, x.last_error_code, x.next_attempt_at, r.message_id, r.outcome, i.state AS inbox_state
  FROM autocheck.operations o
  LEFT JOIN autocheck.external_requests e USING (operation_id)
  LEFT JOIN autocheck.outbox x USING (external_request_id)
  LEFT JOIN autocheck.receipts r USING (external_request_id)
  LEFT JOIN autocheck.inbox i USING (message_id)
  WHERE o.operation_id = '<operation-id>';"

# аудит решений процесса review
docker compose exec -T postgres psql -U postgres -d course -c "SELECT source, principal, outcome, rule_version, created_at FROM autocheck.decisions WHERE process_id = '<process-id>';"

# audit provider изнутри сети Compose
docker compose exec -T -e AUDIT="$PROVIDER_AUDIT_TOKEN" receipt-adapter python -c "import os,urllib.request as u; \
  print(u.urlopen(u.Request('http://provider-simulator:8081/internal/audit/<external-request-id>', \
  headers={'X-Audit-Token': os.environ['AUDIT']})).read().decode())"
```

Как читать симптомы:

| Симптом | Где смотреть |
|---|---|
| `/health/live` 200, `/health/ready` 503 | `api` не отвечает или PostgreSQL недоступен: `docker compose logs api`, `docker compose ps postgres` |
| Действие отвечает `404 action.not_found` | версия не опубликована или выключена: `./course.sh action list` |
| Действие отвечает `500 action.contract_violation` | функция вернула необъявленный `outcome` или `result`, не проходящий `response_schema` |
| Процесс стоит в `RUNNING`, задание `READY` | оба worker остановлены или не могут подключиться: `docker compose logs worker-a` |
| Задание в `RETRY_WAIT` | попытка отклонена с `retryable=true`; время следующей — в `autocheck.jobs.next_attempt_at` |
| Задание `DEAD`, процесс `FAILED` | причина — в `autocheck.jobs.dead_reason`, код отказа — в `autocheck.attempts.error_code`, событие `TaskFailed` или `TaskAbandoned` — в истории |
| Попытка `STALE` | аренда истекла до завершения; `attempt_count` вырос, бюджет отказов — нет |
| `workflow.lease_stale` | завершение пришло от прежнего арендатора после перехвата — так и должно быть |
| Действие отвечает `503 dependency.unavailable` c `retryable: true` и `details.sqlstate` `40P01`/`40001` | конфликт блокировок или сериализации в PostgreSQL; транзакция откатана целиком, ту же команду с тем же ключом можно повторить. Worker в этом случае планирует повтор, а не переводит процесс в `FAILED` |
| `executionId` из `autocheck.jobs` | связывает задание, все его попытки, строку `autocheck.action_dispatches` и предметный эффект |
| Operation `PROCESSING`, Outbox `PENDING` | dispatcher остановлен или не может подключиться: `docker compose logs outbox-dispatcher`, событие `outbox.cycle_failed` |
| Outbox `RETRY_WAIT`, `last_error_code` = `transport.error.retryable` или `http.5xx.retryable` | provider недоступен или медленнее `PROVIDER_TIMEOUT_MS`; следующая попытка — `next_attempt_at` |
| Outbox `DEAD` | исчерпаны попытки или terminal-ответ (`http.4xx.terminal`, `response.invalid.terminal`); процесс продолжает ждать квитанцию |
| Outbox `CONFIRMED`, Inbox `RECEIVED` дольше секунды | reconciler остановлен или процесс ещё не дошёл до `wait_receipt`: `docker compose logs inbox-reconciler` |
| Inbox `CONFLICT` | сообщение пришло после того, как процесс уже применил другую квитанцию или завершился |
| Outbox `last_error_code = lease.expired.retryable` | прежний dispatcher захватил доставку и не отчитался (остановлен или потерял базу); попытка учтена в `attempt_count` |
| Callback provider получает `404` | capability в `CALLBACK_URL` не совпадает с `PROVIDER_CALLBACK_CAPABILITY` adapter'а |
| `receipt.accept` отвечает `401 signature.invalid` | `PROVIDER_HMAC_SECRET` у `api` и adapter различаются или тело изменено по пути |
| `receipt.accept` отвечает `401 auth.invalid` | `PROVIDER_CALLBACK_TOKEN` подписан не тем `COURSE_JWT_SIGNING_KEY` или просрочен |
| Процесс review стоит в `WAITING_MANUAL` | ожидает `workflow.manual`; `stepInstanceId` — в `autocheck.steps` (`step_type = 'MANUAL'`, `state = 'WAITING'`) |

### Ограничения

- Exactly-once не обещается. Доставка в provider и callback — at-least-once; один платёж и одно
  применение обеспечивают ключи: `Idempotency-Key = externalRequestId` у provider, глобальный
  `messageId` с hash тела в Inbox, `executionId` у шагов процесса.
- Один dispatcher и один reconciler. Функции `delivery.*` уже безопасны при нескольких экземплярах
  (`SKIP LOCKED`, owner + `lease_version`), но jitter, операционная работа с `DEAD`, health и
  metrics периметра — неделя 4. `DEAD` Outbox не валит процесс: поздняя квитанция всё равно будет
  принята и применена.
- Failpoints `after_outbox_claim` и `after_provider_response` у dispatcher работают только в test
  profile; `after_inbox_saved` и `after_manual_decision` не реализованы — это приёмка недели 4.
- Доверие к callback provider v0.2.0 держится на изолированной сети Compose и capability URL: сам
  provider не подписывает сообщения. HMAC доказывает только участок adapter → platform
  ([ADR 0005](docs/adr/0005-provider-trust-boundary-and-anti-corruption-layer.md)).
- Adapter строже receipt-schema в одном месте: `providerPaymentId` обязан быть header-safe ASCII,
  потому что он же — `Idempotency-Key`. Иной callback получает `400` до API.
- Отпечаток точных байт тела передаётся в server-side context всех HTTP-вызовов как `payloadHash`:
  jsonb-представление payload байты не сохраняет, а Inbox хранит hash именно подписанного тела.
- Правило лимита — одна неизменяемая строка `payment.limit_rule` (`course-limit-v1`, RUB). Другой
  порог — новая версия правила миграцией, а не правка существующей.
- Автоматическое решение записывается только для одобрения (`LIMIT_RULE APPROVED`): `REVIEW_REQUIRED`
  — это маршрут к ручному шагу, а не решение, и отдельной строки аудита не порождает.
- `workflow.manual` не проверяет, что ревьюер не является автором операции: в учебном контуре это
  не требуется, в реальном — нужна проверка «четырёх глаз».
- `payment.submit` и `operation.events` не проверяют, что операция принадлежит вызывающему
  principal: любой держатель `payment:write` / `payment:read` действует над любой операцией по её
  идентификатору — так же, как `workflow.get` недели 2. В реальном контуре нужна проверка
  принадлежности.
- JWT adapter'а (`PROVIDER_CALLBACK_TOKEN`) не обновляется сам. `scripts/dev_env.py` выпускает его на
  7 дней; после истечения API отвечает `401 auth.invalid`, adapter честно передаёт этот статус
  provider (событие `callback.forwarded` с `apiStatus=401`), и provider исчерпывает свои повторы.
- Привязка `payment.flow_binding` неизменяема: другая карта для вида операции вводится новой версией
  той же карты, а не переименованием в привязке, иначе уже запущенные процессы не прошли бы
  `payment.validate`.
- Идентичность тела идемпотентной HTTP-команды — точные байты запроса. Записи идемпотентности,
  сделанные до миграции 020, хранят jsonb-отпечаток; стенд недели 3 и так требует чистого тома.
- У JWT/HMAC/capability/токенов нет default в `compose.yaml`, поэтому голый
  `docker compose up` без окружения не поднимет `api` и adapter; для локального запуска есть
  `scripts/dev_env.py`.
- Реализовано ограниченное исполняемое подмножество `course-1`, а не BPMN-движок: нет импорта BPMN
  XML, параллельных и inclusive-шлюзов, таймеров, boundary events, компенсаций, подпроцессов и
  произвольных выражений. Карта может адресовать значения указателями, но не вычислять их.
- Карта ациклична по контракту, поэтому один шаг посещается не более одного раза за процесс; это
  свойство закреплено уникальностью `(process_id, step_key)` в хранилище.
- Миграция уже запущенного процесса между версиями карты не поддерживается сознательно: процесс
  обязан оставаться воспроизводимым на закреплённой версии.
- Аренда ограничена сроком без heartbeat. Гибель исполнителя стоит одного срока аренды: перехват
  происходит не раньше `lease_until`. Это осознанный размен простоты на задержку восстановления,
  см. [ADR 0004](docs/adr/0004-lease-and-fencing.md).
- Доставка задания — at-least-once. Ровно один предметный эффект обеспечивает не движок, а
  стабильный `executionId` в роли ключа идемпотентности: зарегистрированное действие обязано быть
  идемпотентным по нему. Действие, которое это требование нарушает, движок не спасёт.
- Server-side scopes principal `workflow-worker` хранятся в `workflow.worker_principal` и меняются
  миграцией. Карта, требующая политики вне этого множества, отклоняется при публикации, а не
  падает во время исполнения.
- Успешный технический след workflow-вызова пишется внутри общей транзакции действия и
  завершения, поэтому откат не оставляет строки `dispatch`. След неуспешной попытки пишет
  отдельная транзакция `fail_job` — он переживает откат предметного эффекта. Строка появляется
  только если действие действительно вызывалось: отказ маппинга и недоступность версии вызовом
  не являются и следа не оставляют. Ключи следов разные по смыслу: успех адресуется
  `executionId` (ключ идемпотентности зафиксированного эффекта), неуспех — `attemptId`, потому
  что откаченный вызов эффекта не создал и связывать его с ключом эффекта было бы неправдой.
- Роль операций обязательна: при отсутствии `COURSE_OPERATOR_USER` команды `flow start/get/
  signal/test-finish` завершаются понятной ошибкой конфигурации. Тихого отката к роли публикации
  нет намеренно — у неё отозваны нужные права, и откат дал бы отказ в середине команды.
- Действие `workflow.get` возвращает не более 200 шагов, заданий и попыток; признак усечения —
  `process.truncated`. Ограничения на то, чей процесс можно прочитать, **нет**: любой держатель
  политики `workflow:read` читает любой процесс по его идентификатору. Для учебного контура это
  осознанный выбор, а не недосмотр; в реальном контуре здесь нужна проверка принадлежности.
- Worker не перезапускается автоматически (`restart: "no"`). Это требование воспроизводимости
  аварийных сценариев: убитый исполнитель обязан оставаться убитым, иначе доказать перехват аренды
  вторым исполнителем невозможно. В рабочем окружении политику перезапуска задают иначе.
- Манифест и определение карты читаются из PostgreSQL, кеш в C# не используется. Это даёт
  немедленный эффект `activate` без протокола инвалидации ценой одного обращения к базе.
- Предметный отказ зарегистрированной функции отображается на `422` с кодом самой функции.
  Обоснование — [ADR 0002](docs/adr/0002-technical-and-domain-result.md).
- Неизменяемость проверяется относительно прикладных ролей и дополнительно закреплена триггерами
  (каталог, версии карт, история). Административный superuser PostgreSQL в модель угроз задания
  не входит.
- Разделение ролей включается только на **чистом томе**: роли, включая `outbox_dispatcher` и
  `inbox_reconciler`, создаются один раз при инициализации кластера, а выполнить `CREATE ROLE` из
  миграции нельзя — у `course_owner` нет `CREATEROLE`. На базе, развёрнутой раньше, гранты тихо
  пропускаются, а Python-процессы не смогут подключиться.
- Бюджет ожидания шлюза фиксирован (35 секунд) и заведомо больше контрактного потолка `timeout_ms`
  (30 секунд): по контракту шлюз не имеет права знать каталог, а значит и `timeout_ms` манифеста.
- Пароли ролей в `compose.yaml` — явно обозначенные local-only значения для разработки, а не
  секреты; в рабочем окружении они задаются переменными окружения хоста.

## Структура репозитория

```text
compose.yaml              десять сервисов, единственный публичный порт 127.0.0.1:8080
check.sh, autocheck/      открытая black-box проверка недель 1–2 (проверка недели 3 — отдельный репозиторий)
course.sh                 обёртка над сервисом cli
contracts/course-1/       машинные схемы: манифест, карта, provider v0.2.0, receipt v1, submit, manual
db/init/                  создание ролей при инициализации кластера
db/migrations/            checksummed миграции: каталог, invoke, предметная схема, workflow, периметр, autocheck
db/actions/               встроенные манифесты действий
db/flows/                 встроенные карты workflow-smoke v1/v2, payment-processing v1, payment-review v1
docker/                   Dockerfile сервисов, включая образ postgres со скриптом ролей и Python-образ периметра
docs/                     C4 Container diagram, ADR и тексты заданий недель 2–3
integration/              Python-пакет периметра и его unit/contract-тесты
scripts/                  локальное окружение, интеграционные тесты и e2e-сценарии на живом контуре
src/Course.Shared/        envelope, JWT, JSON Schema, OpenAPI, контракт карты, доступ к PostgreSQL
src/Course.Api/           generic action runtime
src/Course.Gateway/       публичный gateway
src/Course.Cli/           Course CLI
src/Course.Workflow.Worker/   сменный исполнитель процессов
src/Course.Tests/         unit-тесты границ C#-слоя и контракта карт
src/Course.IntegrationTests/  тесты гонок версий, конкурентной публикации и границ ролей
```
