# Неделя 1. Database-first action runtime

Решение задания первой недели ModuleDev: C# gateway и generic C# runtime, публикующие
зарегистрированные PostgreSQL-функции как HTTP actions.

Исходный текст задания — в [TASK.md](TASK.md). Нормативные контракты — в
[docs/contract-reference.md задания](https://github.com/fintech-dev-lab/moduledev-week-1-gateway-task/blob/main/docs/contract-reference.md),
машинные схемы — в [`contracts/course-1`](contracts/course-1).

Новый зарегистрированный action добавляется миграцией и манифестом и становится доступен через
общий маршрут без изменения и пересборки C#-кода.

## Решение

### Архитектура

Четыре сервиса Docker Compose, одна публичная точка входа.

| Сервис | Технология | Ответственность | Порт |
|---|---|---|---|
| `gateway` | C# ASP.NET Core | Публичный edge: whitelist маршрутов, прозрачная передача контрактных заголовков, health. Не содержит каталог, предметную логику и доступ к PostgreSQL | `8080` на host |
| `api` | C# ASP.NET Core | Generic action runtime: JWT, server-side context, разрешение версии, request/response schema, одна Npgsql-транзакция, OpenAPI из манифестов | только внутренний `8080` |
| `cli` | C# console | Course CLI: checksummed migrations и публикация манифестов. Владеет publication credentials | нет |
| `postgres` | PostgreSQL 17 | Авторитетное состояние: каталог, идемпотентность, dispatch, operations и history, схема `autocheck` | нет |

Направление вызовов строго одностороннее:

```text
клиент → gateway (:8080) → api (Compose DNS http://api:8080) → PostgreSQL (api.invoke)
                                     cli → PostgreSQL (миграции и публикация)
```

- `gateway` обращается к внутреннему `api` по Compose DNS `http://api:8080`, а не через
  `localhost`; loopback-адрес отклоняется при старте.
- `api` не имеет ни одного предметного controller: единственный маршрут действий —
  `POST /api/{module}/{action}`, всё специфичное берётся из опубликованного манифеста.
- Единственная точка предметного выполнения — `api.invoke(...)` в PostgreSQL. Она разрешает
  target только из неизменяемого каталога, повторно проверяет policy по доверенному context,
  проверяет точную сигнатуру функции и вызывает только зарегистрированную функцию.
- Идемпотентность и «ровно одна default-версия маршрута» держатся уникальными индексами
  PostgreSQL, а не process-local блокировками.

Схема контейнеров и поток одного вызова: [C4 Container diagram](docs/c4-container.md).

Архитектурные решения:

- [ADR 0001. Граница доверия проходит по проверенному JWT и повторяется в базе](docs/adr/0001-trust-boundary.md);
- [ADR 0002. Технический результат и предметный результат разделены](docs/adr/0002-technical-and-domain-result.md).

Ключевые файлы:

| Что | Где |
|---|---|
| Generic HTTP executor | [`src/Course.Api/ActionRuntime.cs`](src/Course.Api/ActionRuntime.cs) |
| Проверка JWT и формы claims | [`src/Course.Shared/JwtValidator.cs`](src/Course.Shared/JwtValidator.cs) |
| Единственная точка предметного вызова | [`db/migrations/004_invoke.sql`](db/migrations/004_invoke.sql) |
| Каталог действий и публикация | [`db/migrations/003_catalog_api.sql`](db/migrations/003_catalog_api.sql) |
| Проверочные views `autocheck` и права ролей | [`db/migrations/006_autocheck.sql`](db/migrations/006_autocheck.sql) |
| OpenAPI из манифестов | [`src/Course.Shared/OpenApiBuilder.cs`](src/Course.Shared/OpenApiBuilder.cs) |

### Запуск

Prerequisites: Docker Engine 24+ и Docker Compose v2. Другие инструменты не нужны: .NET SDK,
`psql` и NuGet-пакеты используются только внутри образов.

Из корня чистого clone:

```bash
docker compose up -d --build
```

Команда самодостаточна. После неё не требуются ручные SQL-команды и ручная публикация встроенных
actions: сервис `cli` применяет базовые миграции и публикует `payment.request` v1 и
`operation.get` v1, и только после его успешного завершения стартует `api`.

Ожидаемый результат:

```bash
curl -s http://localhost:8080/health/live    # {"status":"ok","service":"gateway",...}
curl -s http://localhost:8080/health/ready   # {"status":"ok","service":"gateway","check":"api",...}
curl -s http://localhost:8080/openapi/default.json | head -c 200
```

Пример вызова action (токен подписывается тем же HS256-ключом, что задан в `COURSE_JWT_SIGNING_KEY`):

```bash
curl -s http://localhost:8080/api/payment/request \
  -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: request-123" \
  -H "Content-Type: application/json" \
  -d '{"operationKind":"PAYMENT_EXECUTION","amount":"1000.00","currency":"RUB"}'
```

Успешный ответ — `200` с envelope `{"status":"ok","outcome":"CREATED","result":{...},"meta":{...}}`.

Остановка и полная очистка состояния:

```bash
docker compose down -v
```

Команды Course CLI выполняются через сервис `cli`:

```bash
./course.sh action list
./course.sh action activate payment.request --version 1
docker compose run --rm -T cli action publish /autocheck/input/manifests/example.action.json
```

### Конфигурация

Реальные секреты в репозитории отсутствуют. Значения ниже — локальные значения по умолчанию для
разработки; они переопределяются переменными окружения хоста, а проверка подменяет signing key
через собственный Compose override.

| Переменная | Сервис | Значение открытого профиля | Назначение |
|---|---|---|---|
| `COURSE_JWT_ISSUER` | `api` | `moduledev-course` | Ожидаемый `iss` |
| `COURSE_JWT_AUDIENCE` | `api` | `moduledev-api` | Ожидаемый `aud` |
| `COURSE_JWT_SIGNING_KEY` | `api` | локальный ключ ≥ 32 байт | HS256-ключ проверки подписи |
| `COURSE_API_BASE_URL` | `gateway` | `http://api:8080` | Внутренний адрес по Compose DNS |
| `COURSE_POSTGRES_HOST` / `_PORT` / `_DB` | `api`, `cli` | `postgres` / `5432` / `course` | Адрес авторитетного состояния |
| `COURSE_POSTGRES_USER` / `_PASSWORD` | `api` | `course_runtime` | Runtime-роль без прямого DML |
| `COURSE_POSTGRES_USER` / `_PASSWORD` | `cli` | `course_migrator` | Publication-роль с правом DDL |
| `COURSE_MIGRATOR_PASSWORD`, `COURSE_RUNTIME_PASSWORD`, `COURSE_POSTGRES_PASSWORD` | `postgres` | локальные значения | Пароли ролей при инициализации кластера |
| `COURSE_BOOTSTRAP_MIGRATIONS` / `_ACTIONS` | `cli` | `/app/db/migrations` / `/app/db/actions` | Источники стартовой инициализации |

Роли PostgreSQL создаются один раз при инициализации кластера
([`db/init/00-roles.sh`](db/init/00-roles.sh)):

- `course_owner` — `NOLOGIN`, владелец всех схем, таблиц и `SECURITY DEFINER` функций;
- `course_migrator` — publication-роль сервиса `cli`, выполняет DDL под `SET ROLE course_owner`;
- `course_runtime` — runtime-роль сервиса `api`: `EXECUTE` на `api.invoke`, `SELECT` на views
  `autocheck` и никакого прямого DML к каталогу, предметным таблицам и dispatch.

`api` никогда не получает migration credentials.

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

Когда: при каждом старте стека сервис `cli` выполняет `bootstrap` — применяет базовые миграции и
публикует встроенные манифесты из [`db/actions`](db/actions). Обе операции идемпотентны, поэтому
повторный `docker compose up -d --build` не создаёт расхождений. `api` стартует только после
успешного завершения `cli` (`service_completed_successfully`).

Данные PostgreSQL хранятся в named volume `postgres-data` и переживают удаление контейнеров
`gateway` и `api`.

### Проверка

Открытая black-box проверка запускается из корня решения:

```bash
./check.sh
```

Скрипт поднимает стек в отдельном Compose-проекте, подменяет JWT signing key через собственный
override, монтирует доверенные fixtures и записывает отчёт `week-1-public-report.json`
(файл не хранится в Git). Ненулевой exit code означает провал хотя бы одной обязательной проверки.

Отдельные контрактные команды CLI, которые использует проверка:

```bash
docker compose run --rm -T --no-deps cli migration apply /autocheck/input/migrations
docker compose run --rm -T --no-deps cli action validate /autocheck/input/manifests/example.action.json
docker compose run --rm -T --no-deps cli action publish  /autocheck/input/manifests/example.action.json
docker compose run --rm -T --no-deps cli action list
docker compose run --rm -T --no-deps cli action activate example.probe --version 2
docker compose run --rm -T --no-deps cli action disable  example.probe --version 2 --replacement-version 1
```

Собственные unit-тесты закрывают границы, которые проверяются без Docker: форма claims JWT,
JSON Schema с обязательной проверкой `format`, матрица кодов ошибок и построение OpenAPI из
манифеста.

```bash
dotnet test src/Course.sln
```

Тесты дополняют `./check.sh`, но не заменяют его: инварианты идемпотентности, отката и прав
ролей доказываются только на живом контуре.

### Диагностика

```bash
# состояние сервисов
docker compose ps

# логи (JWT, ключи и полный payload в них не попадают)
docker compose logs -f gateway
docker compose logs -f api
docker compose logs cli

# health
curl -i http://localhost:8080/health/live     # процесс gateway
curl -i http://localhost:8080/health/ready    # готовность api и PostgreSQL

# OpenAPI, построенный из опубликованных манифестов
curl -s http://localhost:8080/openapi/default.json
curl -s http://localhost:8080/openapi/actions/payment/request/1.json

# авторитетное состояние и проверочные проекции
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.contract_info;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.action_definitions;"
docker compose exec -T postgres psql -U postgres -d course -c "SELECT correlation_id, module, action, version, status, outcome FROM autocheck.action_dispatches ORDER BY occurred_at DESC LIMIT 10;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.operations;"
docker compose exec -T postgres psql -U postgres -d course -c "TABLE autocheck.operation_events;"
```

Как читать симптомы:

| Симптом | Где смотреть |
|---|---|
| `/health/live` 200, `/health/ready` 503 | `api` не отвечает или PostgreSQL недоступен: `docker compose logs api`, `docker compose ps postgres` |
| Действие отвечает `503 dependency.unavailable` | PostgreSQL недоступен; после восстановления readiness возвращается без пересоздания `api` |
| Действие отвечает `404 action.not_found` | версия не опубликована или выключена: `./course.sh action list` |
| Действие отвечает `500 action.contract_violation` | зарегистрированная функция вернула необъявленный `outcome` или `result`, не проходящий `response_schema` |
| `409 idempotency.conflict` | тот же `Idempotency-Key` использован с другим payload |
| `correlationId` из ответа | связывает HTTP-ответ, логи `api` и строку в `autocheck.action_dispatches` |

### Ограничения

- Манифест читается из каталога PostgreSQL на каждый запрос, кеш в C# не используется. Это даёт
  немедленный эффект `activate` и `disable` без протокола инвалидации ценой одного
  дополнительного обращения к базе на вызов.
- Предметный отказ зарегистрированной функции отображается на `422` с кодом самой функции
  (например, `operation.not_found`). Нормативная матрица не закрепляет отдельный HTTP-статус для
  предметных отказов; выбор обоснован в [ADR 0002](docs/adr/0002-technical-and-domain-result.md).
- `amount` нормализуется до двух знаков после точки (`to_char`), поэтому `payment.request` и
  `operation.get` всегда возвращают одинаковое представление суммы.
- Неизменяемость проверяется относительно прикладных ролей и дополнительно закреплена триггерами
  (каталог, identity operation, append-only history). Административный superuser PostgreSQL в
  модель угроз задания не входит.
- `process_id` всегда `null`: process на первой неделе не создаётся.
- Поддерживается только `POST` и только `Content-Type: application/json`; размер тела запроса
  ограничен 1 МиБ.
- Dispatch-журнал пишется на отдельном соединении и является best effort: сбой записи журнала не
  меняет ответ действия.
- Собственные тесты покрывают только детерминированные границы C#-слоя. Инварианты, живущие в
  PostgreSQL (уникальность идемпотентности, ровно одна default-версия, append-only history,
  права ролей), проверяются на живом контуре: они доказываются схемой и `./check.sh`, а не
  моками. Отдельного integration-набора поверх Docker не добавлялось.
- Пароли ролей в `compose.yaml` — локальные значения по умолчанию для разработки, а не секреты;
  в рабочем окружении они задаются переменными окружения хоста.

## Структура репозитория

```text
compose.yaml              четыре сервиса, единственный публичный порт 8080
check.sh, autocheck/      открытая black-box проверка задания
course.sh                 обёртка над сервисом cli
contracts/course-1/       машинные схемы контракта
db/init/                  создание ролей при инициализации кластера
db/migrations/            checksummed миграции: каталог, invoke, предметная схема, autocheck
db/actions/               встроенные манифесты payment.request v1 и operation.get v1
docker/                   Dockerfile сервисов
docs/                     C4 Container diagram и ADR
src/Course.Shared/        envelope, JWT, JSON Schema, OpenAPI, доступ к PostgreSQL
src/Course.Api/           generic action runtime
src/Course.Gateway/       публичный gateway
src/Course.Cli/           Course CLI
src/Course.Tests/         unit-тесты границ C#-слоя
```
