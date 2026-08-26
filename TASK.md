# Неделя 1. Database-first action runtime

Открытое задание первой недели ModuleDev. Здесь опубликованы условие, machine-readable contracts и открытая black-box проверка. Готового решения, внутренней шкалы и закрытых fixtures в репозитории нет.

С 22 по 26 августа 2026 года постройте C# gateway и generic C# runtime, который публикует зарегистрированные PostgreSQL-функции как HTTP actions.

Задание выполнено, если проверка после сборки может добавить функцию и manifest с неизвестными вашему C#-коду именами, вызвать новый action через общий маршрут и получить контрактный результат без изменения и пересборки API.

## Главный инвариант

> Клиент выбирает опубликованный action и передаёт payload, но не выбирает базу, schema, функцию, SQL, policy или предметный результат.

```text
POST /api/{module}/{action}
  -> gateway на :8080
  -> внутренний api по Compose DNS
  -> JWT и server-side context
  -> action manifest и request schema
  -> одна Npgsql transaction
  -> api.invoke(...)
  -> зарегистрированная PostgreSQL-функция
  -> outcome и response schema
  -> commit или rollback
```

## Срок и сдача

- начало работы: 22 августа 2026 года;
- дедлайн: 26 августа 2026 года, 23:59 по московскому времени;
- до дедлайна отправьте куратору URL приватного Git-репозитория, ветку и полный SHA коммита;
- заранее предоставьте куратору доступ к репозиторию;
- проверяется указанный коммит.

Архив в чате, ссылка без доступа или сообщение без полного SHA не считаются сдачей.

## Технологии и свобода реализации

Обязательны C#, актуальный LTS .NET, ASP.NET Core, PostgreSQL и Docker Compose.

На ваше усмотрение остаются:

- структура solution и число C# projects;
- ORM, query builder или чистый Npgsql;
- физические таблицы, индексы и внутренние имена;
- кеширование manifest;
- внутренние классы и интерфейсы;
- устройство idempotency storage;
- библиотеки JSON Schema и OpenAPI;
- собственные unit и integration tests.

Автопроверка не требует Clean Architecture и не ищет конкретные классы. Небольшая реализация с доказанными инвариантами предпочтительнее большого scaffold. Собственные tests рекомендуются, но не заменяют `./check.sh`.

## Обязательный интерфейс запуска

В корне решения должны находиться `compose.yaml` или `docker-compose.yml` и четыре сервиса:

| Сервис | Обязательное свойство |
|---|---|
| `gateway` | C# ASP.NET Core gateway; единственный публикует host-порт `8080` |
| `api` | Внутренний C# action runtime без опубликованных host-портов |
| `cli` | Course CLI; entrypoint принимает contract commands |
| `postgres` | PostgreSQL с базой `course`, ролью `postgres` и утилитой `psql` |

Данные PostgreSQL хранятся в named volume и переживают удаление контейнеров `gateway` и `api`. После чистого запуска не нужны ручные SQL-команды или публикация встроенных actions:

```bash
docker compose up -d --build
```

`api` читает:

| Переменная | Значение открытого профиля |
|---|---|
| `COURSE_JWT_ISSUER` | `moduledev-course` |
| `COURSE_JWT_AUDIENCE` | `moduledev-api` |
| `COURSE_JWT_SIGNING_KEY` | HS256 key длиной не менее 32 байт |

Реальный секрет в репозиторий не помещается. Проверка подменяет signing key через Compose override.

## Обязательные контракты

Точные сигнатуры, JSON envelopes, error matrix, CLI semantics, action contracts и колонки проверочных views находятся в [нормативном справочнике](docs/contract-reference.md). Machine schemas находятся в [`contracts/course-1`](contracts/course-1).

### Gateway

- внешний клиент обращается только к `http://localhost:8080`;
- gateway принимает по whitelist только actions, OpenAPI и health routes;
- gateway передаёт запрос и контрактные заголовки во внутренний `api` без изменения смысла;
- gateway обращается к `api` по Compose DNS, а не через `localhost`;
- gateway не содержит catalog, предметную логику и доступ к PostgreSQL;
- JWT, credentials и полный payload не попадают в логи;
- `GET /health/live` проверяет процесс, `GET /health/ready` — готовность внутреннего API.

### Action runtime

- один generic route: `POST /api/{module}/{action}`;
- explicit version передаётся только в `X-Action-Version`, без заголовка выбирается default;
- target разрешается только из immutable action catalog;
- request schema проверяется до предметного вызова;
- `api.invoke` повторно проверяет policy и вызывает только зарегистрированную функцию;
- outcome и `result` проверяются до commit;
- error, неизвестный outcome и invalid result откатывают весь предметный эффект;
- runtime-роль выполняет `api.invoke`, но не получает прямой DML к предметным таблицам.

### Trust boundary

- `principal`, `consumer` и `scopes` берутся только из проверенного JWT;
- claims проверяются по обязательности, JSON-типу и значению;
- `correlationId` и `deadline` формируются runtime;
- поля payload не могут подменить context, policy, target или результат;
- после аутентификации ответ содержит server-side `correlationId`;
- ошибки не раскрывают SQL, stack trace, connection string и внутренние targets.

### Версии и идемпотентность

- опубликованная версия manifest неизменяема;
- `enabled` и `is_default` меняются только атомарными CLI-командами;
- у включённого route ровно одна default-версия;
- одинаковые key и payload возвращают исходный результат без второго эффекта;
- тот же key с другим payload возвращает `409 idempotency.conflict`;
- конкурентная уникальность защищается PostgreSQL, а не process-local lock.

### Обязательные actions

- `payment.request` version 1 создаёт operation и одно событие `OPERATION_CREATED`;
- `operation.get` version 1 читает operation по `operationId`;
- клиент не передаёт workflow name/version и финальный status;
- точные payload/result schemas находятся в `contracts/course-1`.

### OpenAPI, health и evidence

- `/openapi/default.json` содержит только включённые default routes;
- `/openapi/actions/{module}/{action}/{version}.json` описывает одну точную версию;
- readiness успешен только при доступном PostgreSQL и завершённой инициализации;
- после восстановления PostgreSQL readiness возвращается без пересоздания API;
- schema `autocheck` публикует пять read-only views из нормативного справочника;
- `course_runtime` не может изменять operations или удалять events;
- object owner для `SECURITY DEFINER` имеет `NOLOGIN`.

## Что реализовать

- C# services `gateway`, `api`, `cli` и PostgreSQL в Compose;
- checksummed migrations и отдельные publication/runtime roles;
- immutable action catalog и `api.invoke(...)`;
- generic HTTP executor с request/response schema validation;
- JWT, server-side context и policy на двух границах;
- transactional idempotency и rollback contract;
- `payment.request` и `operation.get` как PostgreSQL-функции;
- единый HTTP error envelope;
- manifest-driven OpenAPI и health endpoints;
- пять read-only views schema `autocheck`;
- C4 Container diagram;
- ADR о trust boundary;
- ADR о техническом и предметном результате;
- README с запуском, диагностикой и проверкой.

## Открытая проверка

Запуск из корня решения:

```bash
./check.sh
```

| Область | Что проверяется |
|---|---|
| Compose | Чистая сборка, четыре сервиса, единственный внешний порт gateway |
| Publication | Migration, две версии нового action, default/explicit version, activate/disable |
| Security | JWT, claims, policy в HTTP и `api.invoke`, отсутствие секретов в логах |
| Contracts | Schemas, outcomes, error envelopes, rollback до commit |
| PostgreSQL | Конкурентная идемпотентность, operation, event, dispatch, права ролей |
| Recovery | Recreate gateway/API, остановка и восстановление PostgreSQL, readiness |
| Documentation | Manifest-driven OpenAPI и доступность точных version documents |

Проверка после дедлайна использует те же опубликованные контракты на новых данных, именах actions и конкурентных interleavings. Внутренние fixtures, сценарии и система оценки не публикуются.

## Оформление решения

В корневом `README.md` сдачи создайте секцию `## Решение` с подразделами:

- `### Архитектура` — ответственность сервисов и направление вызовов;
- `### Запуск` — prerequisites, команда запуска, адрес и ожидаемый результат;
- `### Конфигурация` — используемые переменные без реальных секретов;
- `### Миграции` — когда и каким сервисом они применяются;
- `### Проверка` — `./check.sh` и, при наличии, собственные tests;
- `### Диагностика` — gateway, api, PostgreSQL, health и OpenAPI;
- `### Ограничения` — известные ограничения и технические решения.

Исходный текст задания можно оставить ниже или перенести в `TASK.md`.

## Checklist перед сдачей

- все команды выполняются из корня чистого clone;
- `docker compose up -d --build` не требует ручной настройки после запуска;
- `./check.sh` завершается успешно;
- gateway использует Compose DNS, а не `localhost`;
- readiness действительно проверяет зависимости, а не только process liveness;
- API публикует JSON OpenAPI documents, Swagger UI их не заменяет;
- конкурентные повторы создают одну operation и одно начальное событие;
- ссылки на C4 и ADR открываются из секции `Решение`;
- в Git нет `.env`, реальных секретов, `bin`, `obj`, IDE files, логов и `week-1-public-report.json`;
- куратору доступны репозиторий, ветка и полный SHA проверяемого commit.

Admission checks выполняются до функционального прогона. Они проверяют Git commit, безопасный состав репозитория, Compose interface, секцию `Решение` и воспроизводимые команды.

## Условия незачёта

- контур не запускается по README;
- основной API реализован не на C#;
- авторитетное состояние находится не в PostgreSQL;
- предметные endpoints реализованы отдельными C# controllers без action catalog;
- клиент может выбрать БД, schema, функцию или SQL;
- повтор создаёт новый предметный эффект;
- operation и event расходятся;
- новый зарегистрированный action требует пересборки API;
- в репозитории или журналах находятся реальные секреты.

## Не входит в неделю

- workflow maps, process instances и worker;
- `payment.submit` и provider-simulator;
- Outbox/Inbox, receipts и manual actions;
- lease, retries и failpoints;
- отдельные C# handlers/controllers для предметных actions;
- Consul, CORS transformations, micro-cache, rate limiting и защитные очереди gateway.

Не проектируйте эти компоненты заранее ценой усложнения action runtime.

## Нормативные источники

1. Этот README определяет границы задания, запуск и сдачу.
2. [`docs/contract-reference.md`](docs/contract-reference.md) фиксирует точные runtime contracts.
3. JSON Schema в [`contracts/course-1`](contracts/course-1) определяет точную форму machine payloads и manifest.

Если документы противоречат друг другу, сообщите куратору: это дефект задания, а не повод угадывать поведение проверки.
