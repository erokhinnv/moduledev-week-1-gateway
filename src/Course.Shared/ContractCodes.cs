namespace Course.Shared;

/// <summary>
/// Матрица кодов ошибок контракта курса.
///
/// Это единственное место в проекте, где технический код ошибки (например "auth.invalid")
/// превращается в HTTP-статус (например 401). Благодаря тому что и gateway, и api, и cli
/// используют один и тот же класс, они никогда не разойдутся в понимании того, какой статус
/// соответствует какой ошибке — не нужно поддерживать одну и ту же таблицу в нескольких местах.
/// </summary>
public static class ContractCodes
{
    /// <summary>Версия контракта курса. Публикуется в meta каждого ответа CLI и в проверочных views.</summary>
    public const string ContractVersion = "course-1";

    // --- Технические коды ошибок, которые может вернуть сам runtime (gateway/api) ---

    /// <summary>JWT отсутствует, повреждён, просрочен или содержит claim неверного типа.</summary>
    public const string AuthInvalid = "auth.invalid";

    /// <summary>Сам HTTP-запрос некорректен: неверный JSON, неверный маршрут или неверный заголовок версии.</summary>
    public const string RequestInvalid = "request.invalid";

    /// <summary>Действие требует заголовок Idempotency-Key, а он не передан.</summary>
    public const string IdempotencyRequired = "idempotency.required";

    /// <summary>У вызывающего нет прав (scopes), необходимых для этого действия.</summary>
    public const string AccessDenied = "access.denied";

    /// <summary>Запрошенное действие/версия не зарегистрированы в каталоге или выключены.</summary>
    public const string ActionNotFound = "action.not_found";

    /// <summary>Тот же ключ идемпотентности использован повторно, но с другим содержимым запроса.</summary>
    public const string IdempotencyConflict = "idempotency.conflict";

    /// <summary>Тело запроса не соответствует JSON Schema действия (request_schema).</summary>
    public const string PayloadInvalid = "payload.invalid";

    /// <summary>Авторитетное хранилище (PostgreSQL) временно недоступно.</summary>
    public const string DependencyUnavailable = "dependency.unavailable";

    /// <summary>Зарегистрированная функция нарушила контракт: неизвестный outcome или невалидный result.</summary>
    public const string ContractViolation = "action.contract_violation";

    /// <summary>Истёк дедлайн выполнения действия, вычисленный из timeout_ms манифеста.</summary>
    public const string ActionTimeout = "action.timeout";

    /// <summary>Необработанная внутренняя ошибка, о деталях которой клиенту сообщать нельзя.</summary>
    public const string InternalError = "internal.error";

    // --- Коды ошибок, которые может вернуть только CLI при работе с публикацией ---

    /// <summary>Манифест действия не проходит проверку по JSON Schema каталога.</summary>
    public const string ManifestInvalid = "manifest.invalid";

    /// <summary>Попытка опубликовать уже существующую версию действия с другим содержимым (нарушение неизменяемости).</summary>
    public const string ManifestConflict = "manifest.conflict";

    /// <summary>Файл миграции с таким именем уже применён, но с другой контрольной суммой.</summary>
    public const string MigrationConflict = "migration.conflict";

    // --- Коды ошибок движка процессов (неделя 2) ---

    /// <summary>Карта процесса не удовлетворяет семантическому контракту course-1.</summary>
    public const string WorkflowMapInvalid = "workflow.map_invalid";

    /// <summary>Источник данных, объявленный в input_mapping, отсутствует; действие не вызывается.</summary>
    public const string WorkflowMappingMissing = "workflow.mapping_missing";

    /// <summary>Аренда, дававшая право завершить задание, больше не действует.</summary>
    public const string WorkflowLeaseStale = "workflow.lease_stale";

    /// <summary>Закреплённая версия действия недоступна: не опубликована или выключена.</summary>
    public const string WorkflowActionUnavailable = "workflow.action_unavailable";

    /// <summary>
    /// Граница исполнения отказала способом, который не является предметной ошибкой действия:
    /// например несогласованность закреплённой карты, обнаруженная уже во время завершения.
    /// Повтор такой попытки бессмыслен, поэтому она неповторяема.
    /// </summary>
    public const string WorkflowBoundaryFailed = "workflow.boundary_failed";

    /// <summary>Решение по ручному шагу уже принято: конкурирующее или изменённое решение отклоняется.</summary>
    public const string WorkflowDecisionConflict = "workflow.decision_conflict";

    // --- Коды границы подписанных запросов (неделя 3) ---

    /// <summary>Заголовок X-Provider-Signature передан, но его формат, версия или HMAC неверны.</summary>
    public const string SignatureInvalid = "signature.invalid";

    /// <summary>
    /// Действие требует доверенный маркер проверенной подписи, а запрос пришёл без подписи. Код
    /// возвращает сама зарегистрированная функция: C# не знает, какому действию подпись нужна.
    /// </summary>
    public const string ReceiptSignatureRequired = "receipt.signature_required";

    // --- Коды наблюдаемости (неделя 4) ---

    /// <summary>
    /// Сквозной идентификатор не встречается ни в одной сохранённой цепочке. Код возвращает сама
    /// зарегистрированная функция, а здесь он получает статус 404: «искали и не нашли» — это
    /// отсутствующий ресурс, а не отклонённое по содержанию обращение.
    /// </summary>
    public const string DiagnosticsTraceNotFound = "diagnostics.trace_not_found";

    /// <summary>
    /// HTTP-статус для кода ошибки, который НЕ входит в техническую матрицу выше — то есть для
    /// предметного (доменного) кода ошибки, объявленного самой зарегистрированной функцией
    /// (например "operation.not_found" из payment.operation_get_v1). Раз код не технический,
    /// значит запрос был понят и корректно отклонён на уровне бизнес-логики — это соответствует
    /// статусу 422 Unprocessable Entity.
    /// </summary>
    public const int DomainErrorStatus = 422;

    /// <summary>
    /// Главная таблица соответствия "код ошибки → HTTP-статус". Любой код, которого нет в этом
    /// списке явно, считается предметной (доменной) ошибкой и получает статус 422.
    /// </summary>
    public static int HttpStatus(string? code) => code switch
    {
        AuthInvalid => 401,
        RequestInvalid => 400,
        IdempotencyRequired => 400,
        AccessDenied => 403,
        ActionNotFound => 404,
        DiagnosticsTraceNotFound => 404,
        IdempotencyConflict => 409,
        WorkflowDecisionConflict => 409,
        SignatureInvalid => 401,
        ReceiptSignatureRequired => 403,
        PayloadInvalid => 422,
        DependencyUnavailable => 503,
        ContractViolation => 500,
        ActionTimeout => 504,
        InternalError => 500,
        _ => DomainErrorStatus,
    };
}
