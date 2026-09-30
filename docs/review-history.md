# История замечаний проверок

Статус каждого замечания прежних проверок на текущем коде. Статус подтверждается указанным тестом
или файлом, а не предположением: `fixed` означает, что есть проверка, которая упадёт при возврате
дефекта. Обозначения: **fixed** — исправлено и закреплено; **not_applicable** — снято контрактом.

## Неделя 1, первая попытка (`38d6373`, отчёт 2026-08-27)

| # | Замечание | Статус | Где закреплено |
|---|---|---|---|
| 1 | TOCTOU default-версии между валидацией и исполнением (High) | fixed | `RuntimeIntegrationTests.ExecutedVersionAlwaysMatchesTheValidatedVersion` |
| 2 | Таймаут gateway меньше лимита манифеста (High) | fixed | `COURSE_GATEWAY_UPSTREAM_TIMEOUT` 35 с > 30 с; [ADR 0003](adr/0003-deadline-composition.md) |
| 3 | Повтор по ключу смешивает версии действий (Medium) | fixed | `RuntimeIntegrationTests.ReplayKeepsTheVersionThatProducedTheResult` |
| 4 | Права migrator и publisher объединены (Medium) | fixed | `CatalogIntegrationTests.PublicationRoleCannotReachDdlDomainStateOrInvoke` |
| 5 | Команды каталога не сериализованы по маршруту (Medium) | fixed | advisory lock в `007_catalog_serialization.sql`; `CatalogIntegrationTests.ConcurrentCatalogCommandsLeaveExactlyOneDefault` |
| 6 | Exact-version OpenAPI не требует версию (Medium) | fixed | `OpenApiBuilderTests` (`required: true`, `const`) |
| 7 | `consumer` вне кортежа неизменяемости операции (Low) | fixed | `008_immutability_hardening.sql` |
| 8 | Документ manifest вне триггера неизменяемости (Low) | fixed | `008_immutability_hardening.sql` |
| 9 | Исключения логируются целиком (Low) | fixed | `Redaction.Describe` во всех процессах; `WorkflowIntegrationTests.CliDiagnosticsNeverCarryTheRawFailureText` |
| 10 | Размер ответа не ограничен (Low) | fixed | `COURSE_MAX_RESULT_BYTES`, `COURSE_GATEWAY_MAX_RESPONSE_BYTES`; `GatewayIntegrationTests`, `BoundedCopyTests` |

## Неделя 1, вторая попытка (`86bf649`, отчёт 2026-09-02)

| # | Замечание | Статус | Где закреплено |
|---|---|---|---|
| 11 | Смена idempotency scope между версиями повторяет предметный эффект (Critical) | fixed | `021_idempotency_semantics.sql`; `CatalogIntegrationTests.IdempotencySemanticsOfARouteCannotChangeBetweenVersions` |
| 12 | CLI печатает `exception.Message` (Medium) | fixed | `Program.cs` CLI через `Redaction.Describe`; `CliDiagnosticsNeverCarryTheRawFailureText` |
| 13 | README и C4 расходятся с активными миграциями (Low) | fixed | README «Ключевые файлы» указывает действующее определение `api.invoke` (020) |

## Неделя 2 (`a35e320`, отчёт 2026-09-19)

| # | Замечание | Статус | Где закреплено |
|---|---|---|---|
| 14 | Безопасный envelope сопровождается исходным текстом исключения в stderr (Medium) | fixed | `CliDiagnosticsNeverCarryTheRawFailureText` |
| 15 | Нет собственных регрессий состояния и сбоев (Medium) | fixed | `AStaleLeaseCannotFinishAJobThatAnotherOwnerReclaimed`, `KillingTheWorkerBetweenActionAndFinishLeavesNoEffectAndRecovers`, `AReusedMessageIdWithADifferentBodyIsAConflict` |
| 16 | `check.sh` без бита исполнения (Recommendation) | fixed | mode `100755`; `RepositoryContractTests` |

## Неделя 3 (`8dfed15`, отчёт 2026-09-30)

| # | Замечание | Статус | Где закреплено |
|---|---|---|---|
| 17 | Доставка прекращается после третьей временной ошибки: `max_attempts = 3` при задержках 200/400/800 | fixed | `022_outbox_retry_budget.sql` (четыре попытки, задержек ровно `max_attempts - 1`); `DeliveryIntegrationTests`; e2e `scenario_outbox_retry_budget` |
| 18 | Длительное действие переживает срок аренды: потолок `× 3` (6 с в test profile) при `timeout_ms` до 30 с | fixed | `023_lease_covers_the_action_budget.sql` (аренда покрывает бюджет шага, потолок — контрактный максимум); `WorkflowIntegrationTests.ALongActionIsNotReclaimedWhileItsBudgetLasts`; [ADR 0004](adr/0004-lease-and-fencing.md) |
| 19 | Собственный набор не проверяет полное исчерпание повторов Outbox | fixed | e2e `scenario_outbox_retry_budget`: исчерпание с четырьмя попытками и задержками из журнала dispatcher'а, восстановление provider перед последней попыткой, поздняя квитанция после `DEAD` |

Замечание 18 куратор отнёс к повторяющимся с недели 2; в отчёте недели 2, полученном участником,
такого пункта нет. На статус это не влияет: дефект был реальным и закреплён тестом.
