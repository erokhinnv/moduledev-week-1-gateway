# Недели 1–2. Database-first action runtime и персистентное workflow-ядро

Решение первых двух недель ModuleDev в одном репозитории.

Неделя 1: C# gateway и generic C# runtime, публикующие зарегистрированные PostgreSQL-функции как
HTTP actions. Неделя 2: поверх того же runtime — общий C# worker, версионированные workflow-карты,
персистентное состояние процессов в PostgreSQL, аренда с fencing, ограниченный retry и
восстановление после гибели исполнителя.

Исходные тексты заданий — [TASK.md](TASK.md) (неделя 1) и
[docs/task/week-2-assignment.md](docs/task/week-2-assignment.md) (неделя 2). Машинные схемы
контракта — в [`contracts/course-1`](contracts/course-1).

Главное свойство решения не изменилось и распространилось на процессы: **новое действие и новая
карта процесса добавляются миграцией, манифестом и картой — и исполняются через общие маршруты без
единой правки и пересборки C#-кода.**

## Решение

### Архитектура

Шесть сервисов Docker Compose, одна публичная точка входа.

| Сервис | Технология | Ответственность | Порт |
|---|---|---|---|
| `gateway` | C# ASP.NET Core | Публичный edge: whitelist маршрутов, прозрачная передача контрактных заголовков, health. Не содержит каталог, предметную логику и доступ к PostgreSQL | `8080` на host |
| `api` | C# ASP.NET Core | Generic action runtime: JWT, server-side context, разрешение версии, request/response schema, одна Npgsql-транзакция, OpenAPI из манифестов | только внутренний `8080` |
| `cli` | C# console | Course CLI: checksummed migrations, публикация манифестов и карт, trusted-команды процессов. Владеет publication credentials | нет |
| `postgres` | PostgreSQL 17 | Авторитетное состояние: каталог, идемпотентность, dispatch, определения и состояние процессов, схема `autocheck` | нет |
| `worker-a` | C# console | Сменный исполнитель процессов, lease owner `worker-a` | нет |
| `worker-b` | C# console | Тот же образ, lease owner `worker-b` | нет |

Направление вызовов строго одностороннее:

```text
клиент  → gateway (:8080) → api (Compose DNS http://api:8080) → PostgreSQL (api.invoke)
worker  → PostgreSQL (claim_jobs → api.invoke → finish_job / fail_job)
cli     → PostgreSQL (миграции, публикация действий и карт, trusted-команды)
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

Схема контейнеров и поток одного вызова: [C4 Container diagram](docs/c4-container.md).

Архитектурные решения:

- [ADR 0001. Граница доверия проходит по проверенному JWT и повторяется в базе](docs/adr/0001-trust-boundary.md);
- [ADR 0002. Технический результат и предметный результат разделены](docs/adr/0002-technical-and-domain-result.md);
- [ADR 0003. Бюджет времени складывается из одного дедлайна, а шлюз держит потолок](docs/adr/0003-deadline-composition.md);
- [ADR 0004. Аренда с fencing вместо блокировки на время выполнения](docs/adr/0004-lease-and-fencing.md).

Ключевые файлы:

| Что | Где |
|---|---|
| Generic HTTP executor | [`src/Course.Api/ActionRuntime.cs`](src/Course.Api/ActionRuntime.cs) |
| Общий разбор границы `api.invoke` | [`src/Course.Shared/RegisteredAction.cs`](src/Course.Shared/RegisteredAction.cs) |
| Сменный исполнитель процессов | [`src/Course.Workflow.Worker/WorkflowWorker.cs`](src/Course.Workflow.Worker/WorkflowWorker.cs) |
| Семантический валидатор карт | [`src/Course.Shared/WorkflowMapValidator.cs`](src/Course.Shared/WorkflowMapValidator.cs) |
| Единственная точка предметного вызова | [`db/migrations/009_invoke_replay_version.sql`](db/migrations/009_invoke_replay_version.sql) |
| Определения и состояние процессов | [`db/migrations/011_workflow_core.sql`](db/migrations/011_workflow_core.sql) |
| Продвижение процесса и сигналы | [`db/migrations/012_workflow_engine.sql`](db/migrations/012_workflow_engine.sql) |
| Аренда, fencing и завершение попыток | [`db/migrations/013_workflow_lease.sql`](db/migrations/013_workflow_lease.sql) |
| Проверочные views и права ролей | [`db/migrations/015_workflow_autocheck.sql`](db/migrations/015_workflow_autocheck.sql) |

### Запуск

Prerequisites: Docker Engine 24+ и Docker Compose v2. Другие инструменты не нужны: .NET SDK,
`psql` и NuGet-пакеты используются только внутри образов.

Из корня чистого clone:

```bash
docker compose up -d --build
```

Команда самодостаточна. После неё не требуются ручные SQL-команды, ручная публикация встроенных
actions и карт, настройка учётных данных: сервис `cli` применяет базовые миграции, публикует
`payment.request` v1, `operation.get` v1, `training.canary` v1 и `workflow.get` v1, публикует карты
`workflow-smoke` v1 и v2 и активирует v1. Только после его успешного завершения стартует `api`.

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

Остановка и полная очистка состояния:

```bash
docker compose down -v
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
  любую таблицу и ни одной другой функции схем `api` и `workflow`.

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

### Проверка

Открытая black-box проверка запускается из корня решения:

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
том виде, в каком его отдаёт PostgreSQL) и — на опубликованных fixtures проверки — приём валидных
карт и отклонение каждой заведомо испорченной.

```bash
dotnet test src/Course.sln
```

Интеграционные тесты закрывают то, что без живого контура доказать нельзя: гонку версий между
валидацией и исполнением, повтор по ключу идемпотентности через смену версии по умолчанию,
конкурентные команды каталога и границы прав ролей, а для недели 2 — неизменяемость опубликованной
версии карты, append-only историю, неизменяемость личности состояния (ключ идемпотентности,
закрепление за версией, тело принятого сигнала), отклонение испорченной карты самим хранилищем в
обход клиента, границы роли `workflow_worker`, конкурентные `flow activate` и `flow start`, полный
проход `workflow-smoke` через сигнал с ровно одним предметным эффектом и честный отчёт об отказе
границы завершения, не связанном с потерей аренды.

```bash
./scripts/integration-tests.sh
```

Скрипт поднимает отдельный Compose-проект, выполняет тесты и гасит контур за собой. Без поднятого
контура эти тесты помечаются пропущенными, поэтому `dotnet test` без Docker остаётся зелёным.

Тесты дополняют `./check.sh`, но не заменяют его.

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
| `executionId` из `autocheck.jobs` | связывает задание, все его попытки, строку `autocheck.action_dispatches` и предметный эффект |

### Ограничения

- Неделя 2 доводит `manual` только до устойчивого `WAITING_MANUAL`. Завершение ручного шага
  публичным действием, principal/reason и защита конкурентного решения относятся к неделе 3.
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
- Разделение ролей включается только на **чистом томе**: роли создаются один раз при инициализации
  кластера, а выполнить `CREATE ROLE` из миграции нельзя — у `course_owner` нет `CREATEROLE`. На
  базе, развёрнутой раньше, гранты тихо пропускаются.
- Бюджет ожидания шлюза фиксирован (35 секунд) и заведомо больше контрактного потолка `timeout_ms`
  (30 секунд): по контракту шлюз не имеет права знать каталог, а значит и `timeout_ms` манифеста.
- Пароли ролей в `compose.yaml` — локальные значения по умолчанию для разработки, а не секреты;
  в рабочем окружении они задаются переменными окружения хоста.

## Структура репозитория

```text
compose.yaml              шесть сервисов, единственный публичный порт 8080
check.sh, autocheck/      открытая black-box проверка задания
course.sh                 обёртка над сервисом cli
contracts/course-1/       машинные схемы контракта: манифест действия и карта процесса
db/init/                  создание ролей при инициализации кластера
db/migrations/            checksummed миграции: каталог, invoke, предметная схема, workflow, autocheck
db/actions/               встроенные манифесты действий
db/flows/                 встроенные карты workflow-smoke v1 и v2
docker/                   Dockerfile сервисов, включая образ postgres со скриптом ролей
docs/                     C4 Container diagram, ADR и текст задания недели 2
scripts/                  запуск интеграционных тестов на живом контуре
src/Course.Shared/        envelope, JWT, JSON Schema, OpenAPI, контракт карты, доступ к PostgreSQL
src/Course.Api/           generic action runtime
src/Course.Gateway/       публичный gateway
src/Course.Cli/           Course CLI
src/Course.Workflow.Worker/   сменный исполнитель процессов
src/Course.Tests/         unit-тесты границ C#-слоя и контракта карт
src/Course.IntegrationTests/  тесты гонок версий, конкурентной публикации и границ ролей
```
