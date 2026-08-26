# C4, уровень 2: Container diagram

Диаграмма фиксирует ответственность контейнеров и направление вызовов. Единственная публичная
точка входа — `gateway` на порту `8080`. Предметное выполнение существует только внутри
PostgreSQL, за функцией `api.invoke`.

```mermaid
C4Container
    title Неделя 1. Database-first action runtime

    Person(client, "Внешний клиент", "Выбирает опубликованный action и передаёт payload")
    Person(operator, "Оператор курса", "Публикует миграции и манифесты")

    System_Boundary(stack, "Docker Compose stack") {
        Container(gateway, "gateway", "C# ASP.NET Core", "Единственный публичный порт 8080. Whitelist маршрутов, прозрачная передача контрактных заголовков, health. Не содержит каталог и предметную логику")
        Container(api, "api", "C# ASP.NET Core", "Generic action runtime. JWT, server-side context, request/response schema validation, одна Npgsql-транзакция, OpenAPI из манифестов")
        Container(cli, "cli", "C# console", "Course CLI: checksummed migrations и публикация манифестов. Владеет publication credentials")
        ContainerDb(postgres, "postgres", "PostgreSQL 17", "Авторитетное состояние: каталог действий, идемпотентность, dispatch, operations и history, схема autocheck")
    }

    Rel(client, gateway, "POST /api/{module}/{action}, GET /openapi, GET /health", "HTTPS/HTTP :8080")
    Rel(gateway, api, "Проксирует запрос и контрактные заголовки", "HTTP по Compose DNS http://api:8080")
    Rel(api, postgres, "SELECT api.invoke(...), api.resolve_action(...), api.record_dispatch(...)", "Npgsql, роль course_runtime")
    Rel(operator, cli, "migration apply, action publish/activate/disable", "docker compose run cli")
    Rel(cli, postgres, "DDL и публикация каталога", "Npgsql, роль course_migrator → SET ROLE course_owner")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="1")
```

## Границы доверия

| Граница | Что проверяется | Где |
|---|---|---|
| Edge | Whitelist маршрутов, отсутствие предметной логики и БД | `gateway` |
| Runtime | Подпись и форма claims JWT, policy, request schema, outcome и response schema | `api` |
| Database | Разрешение target только из каталога, повторная проверка policy, точная сигнатура функции, идемпотентность | `api.invoke` |
| Roles | `course_runtime` не имеет DML к предметным таблицам, owner `SECURITY DEFINER` имеет `NOLOGIN` | PostgreSQL |

## Поток одного вызова

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
