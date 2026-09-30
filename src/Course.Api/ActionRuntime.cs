using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;

namespace Course.Api;

/// <summary>Разобранный входящий HTTP-запрос к generic-маршруту действия.</summary>
public sealed record ActionRequest(
    string Method,
    IReadOnlyList<string> Segments,
    string? AuthorizationHeader,
    string? VersionHeader,
    string? IdempotencyKey,
    byte[] Body,
    IReadOnlyList<string>? SignatureHeaders = null);

/// <summary>Готовый HTTP-ответ: статус-код и тело в виде JSON-конверта.</summary>
public sealed record ActionResponse(int Status, JsonObject Body);

/// <summary>
/// Универсальный (generic) исполнитель HTTP-действий.
///
/// Это ЕДИНСТВЕННЫЙ компонент на C#, который вообще запускает предметные (доменные) действия —
/// и при этом он сам не содержит НИ ОДНОЙ строчки предметной логики. Всё специфичное для
/// конкретного действия (какая функция вызывается, какая у неё схема запроса/ответа, какие права
/// требуются) приходит из опубликованного манифеста, а сам вызов предметной логики происходит
/// внутри зарегистрированной PostgreSQL-функции за границей api.invoke. Благодаря этому разделению
/// добавление нового действия никогда не требует правки или пересборки этого класса.
/// </summary>
public sealed class ActionRuntime(
    ActionCatalog catalog,
    JwtValidator jwtValidator,
    DispatchQueue dispatchQueue,
    DependencyState dependencyState,
    NpgsqlDataSource dataSource,
    SignatureBoundary signatureBoundary,
    ActionFailpoints failpoints,
    ILogger<ActionRuntime> logger)
{
    private const int MaxIdempotencyKeyLength = 128;

    /// <summary>
    /// Верхняя граница размера конверта, возвращённого api.invoke.
    ///
    /// Тело запроса ограничено Kestrel, а вот результат действия до сих пор не был ограничен
    /// ничем: манифест может объявить схему ответа без верхних границ длины, и тогда одна
    /// зарегистрированная функция способна вернуть сколь угодно большой JSON. Строку из
    /// PostgreSQL к моменту проверки уже материализовал драйвер, но отсечка ДО разбора избавляет
    /// от заведомо более дорогих стадий: построения дерева JsonNode, обхода валидатором схемы и
    /// повторной сериализации в ответ.
    /// </summary>
    private static readonly int MaxResultBytes = Env.OptionalInt("COURSE_MAX_RESULT_BYTES", 512 * 1024);

    /// <summary>
    /// Главная точка входа: обрабатывает один HTTP-запрос к маршруту POST /api/{module}/{action}
    /// от начала (проверка JWT) до конца (ответ клиенту), последовательно проходя все ступени
    /// проверки, описанные ниже пронумерованными шагами.
    /// </summary>
    public async Task<ActionResponse> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        // correlationId генерируется сервером для КАЖДОГО запроса — это уникальный идентификатор,
        // по которому можно найти этот запрос в логах и в проверочных данных (autocheck).
        // Клиент никогда не передаёт correlationId сам, только получает его в ответе.
        var correlationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();

        // Шаг 1. Аутентификация идёт раньше всего остального: доверенный контекст (кто вызывает,
        // от чьего имени, какими правами обладает) имеет ровно один источник — проверенный JWT.
        // Если токен не проходит проверку, дальнейшая обработка вообще не начинается.
        if (!jwtValidator.TryValidate(request.AuthorizationHeader, out var identity, out var reason))
        {
            logger.LogInformation(
                "action rejected correlationId={CorrelationId} code={Code} reason={Reason}",
                correlationId,
                ContractCodes.AuthInvalid,
                reason);
            return Failure(ContractCodes.AuthInvalid, "request is not authenticated", correlationId, null);
        }

        // Шаг 1а. Общая граница подписанных запросов. Подпись проверяется над точными байтами тела
        // до разбора JSON и до разрешения действия: неверная подпись не доходит ни до каталога, ни
        // до целевой функции. Отсутствие подписи не является ошибкой на этой границе — какое
        // действие без неё не работает, решает само действие по доверенному маркеру.
        var signature = signatureBoundary.Verify(request.SignatureHeaders ?? [], request.Body);
        if (signature == SignatureVerdict.Invalid)
        {
            logger.LogInformation(
                "action rejected correlationId={CorrelationId} code={Code}",
                correlationId,
                ContractCodes.SignatureInvalid);
            return Failure(ContractCodes.SignatureInvalid, "request signature is invalid", correlationId, null);
        }

        // Шаг 2. Форма маршрута. После префикса /api в пути обязано быть ровно два сегмента:
        // модуль и действие (например /api/payment/request). Оба сегмента должны быть похожи на
        // корректные SQL-идентификаторы — это первая защита от попытки протащить что-то лишнее
        // прямо в имени маршрута.
        if (request.Segments.Count != 2 ||
            !IsSqlIdentifier(request.Segments[0]) ||
            !IsSqlIdentifier(request.Segments[1]))
        {
            return Failure(ContractCodes.RequestInvalid, "action route must be /api/{module}/{action}", correlationId, null);
        }

        var module = request.Segments[0];
        var action = request.Segments[1];

        if (!string.Equals(request.Method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ContractCodes.RequestInvalid, "actions are invoked with POST", correlationId, null);
        }

        // Шаг 3. Явная версия действия передаётся ТОЛЬКО через заголовок X-Action-Version.
        // Если заголовка нет — будет выбрана версия по умолчанию (default) для этого маршрута.
        // Версия никогда не читается из тела запроса или из URL.
        int? requestedVersion = null;
        if (request.VersionHeader is not null)
        {
            if (!int.TryParse(request.VersionHeader.Trim(), out var parsed) || parsed < 1)
            {
                return Failure(ContractCodes.RequestInvalid, "X-Action-Version must be a positive integer", correlationId, null);
            }

            requestedVersion = parsed;
        }

        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (idempotencyKey is { Length: > MaxIdempotencyKeyLength })
        {
            return Failure(ContractCodes.RequestInvalid, "Idempotency-Key is too long", correlationId, null);
        }

        // Шаг 4. Разбор тела запроса. Оно обязано быть корректным JSON-объектом.
        JsonDocument payloadDocument;
        try
        {
            payloadDocument = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            return Failure(ContractCodes.RequestInvalid, "request body is not valid JSON", correlationId, null);
        }

        using (payloadDocument)
        {
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(ContractCodes.RequestInvalid, "request body must be a JSON object", correlationId, null);
            }

            // Хеш тела запроса — единственное, что попадает в технический след (evidence) вызова.
            // Само содержимое запроса (например реальная сумма платежа) туда не пишется.
            var payloadHash = Hashing.Sha256Hex(request.Body);
            var evidence = new DispatchEvidence
            {
                CorrelationId = correlationId,
                Module = module,
                Action = action,
                Principal = identity!.Principal,
                Consumer = identity.Consumer,
                PayloadHash = payloadHash,
                RequestId = idempotencyKey,
            };

            try
            {
                var response = await RunAsync(
                    module,
                    action,
                    requestedVersion,
                    identity,
                    idempotencyKey,
                    payloadDocument.RootElement,
                    payloadHash,
                    signature == SignatureVerdict.Verified,
                    correlationId,
                    evidence,
                    started,
                    cancellationToken);

                evidence.DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // След ставится в очередь, а не пишется здесь: коммит уже состоялся, и заставлять
                // клиента ждать вспомогательную запись нечестно — см. DispatchQueue.
                await dispatchQueue.EnqueueAsync(evidence);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Истёк наш собственный бюджет времени, а не оборвалось соединение с клиентом:
                // клиент всё ещё ждёт ответа и по контракту обязан получить 504, а не 500.
                logger.LogWarning(
                    "action deadline reached correlationId={CorrelationId} route={Module}.{Action}",
                    correlationId,
                    module,
                    action);

                evidence.Status = "ERROR";
                evidence.ErrorCode = ContractCodes.ActionTimeout;
                evidence.DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await dispatchQueue.EnqueueAsync(evidence);

                return Failure(ContractCodes.ActionTimeout, "action deadline was reached", correlationId, evidence.Version);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Сюда попадают непредвиденные технические сбои (например обрыв соединения с
                // PostgreSQL посреди выполнения). Классифицируем причину и, если это похоже на
                // недоступность базы, "запоминаем" это на короткое время через DependencyState —
                // чтобы следующие запросы не ждали повторного долгого таймаута соединения.
                var code = PostgresFailure.Classify(exception);
                if (PostgresFailure.IsDependencyOutage(exception))
                {
                    dependencyState.MarkUnavailable();
                }

                // В журнал уходит только тип сбоя и SQLSTATE: сообщение PostgreSQL при нарушении
                // ограничения содержит значения ключа, то есть фрагмент полезной нагрузки.
                logger.LogError(
                    "action failed correlationId={CorrelationId} route={Module}.{Action} code={Code} failure={Failure}",
                    correlationId,
                    module,
                    action,
                    code,
                    Redaction.Describe(exception));

                evidence.Status = "ERROR";
                evidence.ErrorCode = code;
                evidence.DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await dispatchQueue.EnqueueAsync(evidence);

                return Failure(code, DescribeTechnicalFailure(code), correlationId, evidence.Version);
            }
        }
    }

    /// <summary>
    /// Основная последовательность разрешения действия, проверки прав, идемпотентности и
    /// собственно вызова через api.invoke внутри одной транзакции.
    /// </summary>
    private async Task<ActionResponse> RunAsync(
        string module,
        string action,
        int? requestedVersion,
        TrustedIdentity identity,
        string? idempotencyKey,
        JsonElement payload,
        string payloadHash,
        bool signatureVerified,
        Guid correlationId,
        DispatchEvidence evidence,
        long startedAt,
        CancellationToken cancellationToken)
    {
        // Шаг 5. Target (целевая функция) разрешается ТОЛЬКО из неизменяемого каталога действий.
        // Если зависимость (PostgreSQL) только что доказала свою недоступность — отвечаем сразу
        // же кодом 503, не пытаясь снова ждать соединения: см. подробный комментарий в
        // DependencyState.cs про то, зачем это нужно (резолв Compose DNS может занимать секунды).
        if (dependencyState.IsKnownUnavailable)
        {
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.DependencyUnavailable;
            return Failure(
                ContractCodes.DependencyUnavailable,
                "authoritative state is temporarily unavailable",
                correlationId,
                requestedVersion);
        }

        var manifest = await catalog.ResolveAsync(module, action, requestedVersion, cancellationToken);
        if (manifest is null)
        {
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.ActionNotFound;
            evidence.Version = requestedVersion;
            return Failure(ContractCodes.ActionNotFound, "action version is not published or is disabled", correlationId, requestedVersion);
        }

        evidence.Version = manifest.Version;

        // Шаг 6. Проверка прав (policy) на границе HTTP. Это ПЕРВАЯ из двух проверок прав —
        // вторая, полностью независимая проверка, будет выполнена ещё раз внутри api.invoke на
        // стороне PostgreSQL. Два независимых места проверки — сознательное решение: даже если
        // кто-то вызовет api.invoke напрямую в обход HTTP-слоя, права всё равно будут проверены.
        var missingScopes = manifest.RequiredPolicy
            .Where(scope => !identity.Scopes.Contains(scope, StringComparer.Ordinal))
            .ToArray();
        if (missingScopes.Length > 0)
        {
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.AccessDenied;
            return Failure(ContractCodes.AccessDenied, "token does not satisfy the required policy", correlationId, manifest.Version);
        }

        // Шаг 7. Требование идемпотентности берётся из манифеста конкретного действия.
        if (manifest.RequiresIdempotencyKey && idempotencyKey is null)
        {
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.IdempotencyRequired;
            return Failure(ContractCodes.IdempotencyRequired, "action requires an Idempotency-Key header", correlationId, manifest.Version);
        }

        // Если ключ идемпотентности не передан клиентом (для действий, где он не обязателен),
        // в качестве requestId используется сгенерированный correlationId — так у каждого вызова
        // всегда есть какой-то идентификатор запроса для технического следа.
        var requestId = idempotencyKey ?? correlationId.ToString();
        evidence.RequestId = requestId;

        // Шаг 8. Схема запроса проверяется ДО любого предметного вызова. Если payload не проходит
        // JSON Schema действия — до PostgreSQL дело вообще не доходит.
        if (!SchemaValidator.TryValidate(manifest.RequestSchema, payload, out var violations))
        {
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.PayloadInvalid;
            return Failure(
                ContractCodes.PayloadInvalid,
                "payload does not match the request schema of the action",
                correlationId,
                manifest.Version,
                details: SchemaValidator.Details(violations));
        }

        // Шаг 9. Бюджет времени на весь запрос.
        //
        // timeout_ms манифеста — это бюджет ВСЕГО запроса, а не одного лишь вызова функции.
        // Поэтому из него вычитается уже потраченное: разрешение манифеста, проверка политики и
        // валидация схемы. Остаток и есть то, что реально осталось на работу с базой, включая
        // ожидание свободного соединения в пуле. Раньше эти стадии в бюджет не входили, из-за
        // чего фактическая длительность запроса могла заметно превышать объявленную манифестом.
        var remaining = TimeSpan.FromMilliseconds(manifest.TimeoutMs) - Stopwatch.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.ActionTimeout;
            return Failure(ContractCodes.ActionTimeout, "action deadline was reached", correlationId, manifest.Version);
        }

        // Шаг 10. Формирование серверного (доверенного) контекста. Поля тела запроса НИКОГДА не
        // могут повлиять на его содержимое — даже если payload случайно (или намеренно) содержит
        // поле "principal" или "scopes", оно будет просто проигнорировано: контекст строится
        // исключительно из проверенного identity и данных манифеста.
        var deadline = DateTimeOffset.UtcNow.Add(remaining);
        var context = new JsonObject
        {
            ["principal"] = identity.Principal,
            ["consumer"] = identity.Consumer,
            ["scopes"] = new JsonArray(identity.Scopes.Select(scope => (JsonNode)JsonValue.Create(scope)!).ToArray()),
            ["correlationId"] = correlationId.ToString(),
            ["requestId"] = requestId,
            ["deadline"] = deadline.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            // Отпечаток точных байт тела. Функции, которым нужна идентичность сообщения «как
            // пришло» (Inbox), берут его отсюда: jsonb-представление payload байты не сохраняет.
            ["payloadHash"] = payloadHash,
        };

        // Только два доверенных маркера и только после успешной проверки HMAC. Ни секрет, ни
        // сама подпись в контекст не попадают.
        if (signatureVerified)
        {
            context["transport"] = SignatureBoundary.TransportMarker();
        }

        // Шаг 11. Один и только один вызов к базе на весь предметный эффект: открываем
        // транзакцию, вызываем api.invoke, проверяем контракт результата и только после этого
        // делаем commit. Если что-то пойдёт не так на любом из следующих под-шагов — вся
        // транзакция откатывается целиком (rollback), и никакого частичного эффекта не остаётся.
        //
        // Единый бюджет на всю работу с базой: он связан с токеном запроса (отключился клиент —
        // прекращаем) и вдобавок сам истекает по остатку от timeout_ms. Под него попадает и
        // ожидание свободного соединения в пуле, которое раньше не ограничивалось ничем.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(remaining);

        await using var connection = await dataSource.OpenConnectionAsync(budget.Token);
        await using var transaction = await connection.BeginTransactionAsync(budget.Token);

        // SET LOCAL statement_timeout действует только внутри этой транзакции: если PostgreSQL не
        // успевает выполнить запрос за отведённое время, он сам прервёт выполнение с SQL-состоянием
        // 57014 (query_canceled), которое затем превращается в 504 action.timeout. Значение — тот
        // же остаток бюджета, поэтому отсечка со стороны базы и со стороны C# согласованы.
        await using (var deadlineCommand = new NpgsqlCommand(
            $"SET LOCAL statement_timeout = {(int)remaining.TotalMilliseconds}", connection, transaction))
        {
            await deadlineCommand.ExecuteNonQueryAsync(budget.Token);
        }

        string? envelopeText;
        await using (var invoke = new NpgsqlCommand(
            "SELECT api.invoke($1, $2, $3, $4, $5)::text", connection, transaction))
        {
            invoke.Parameters.Add(ActionCatalog.Text(module));
            invoke.Parameters.Add(ActionCatalog.Text(action));
            // Передаётся ИМЕННО разрешённая версия, а не то, что прислал клиент.
            //
            // Раньше сюда уходил requestedVersion, то есть null при отсутствии заголовка
            // X-Action-Version, и PostgreSQL разрешала default-версию заново. Между разрешением
            // манифеста выше (строка resolve) и этим вызовом конкурентная команда activate могла
            // переключить default — и тогда payload, проверенный схемой версии A, доставался
            // функции версии B, а ответ проверялся снова схемой A. Классическая гонка
            // "проверили одно, выполнили другое" (TOCTOU).
            //
            // Явная версия закрывает окно полностью: выполняется ровно та версия, по схеме и
            // политике которой запрос был проверен. Если версию успели выключить, invoke ответит
            // action.not_found — это корректный отказ, а не исполнение чужой версии.
            invoke.Parameters.Add(ActionCatalog.Integer(manifest.Version));
            invoke.Parameters.Add(ActionCatalog.Jsonb(context.ToJsonString()));
            invoke.Parameters.Add(ActionCatalog.Jsonb(payload.GetRawText()));
            // Грубая страховка на уровне драйвера поверх точного бюджета budget.Token.
            invoke.CommandTimeout = (int)Math.Ceiling(remaining.TotalSeconds) + 5;
            envelopeText = await invoke.ExecuteScalarAsync(budget.Token) as string;
        }

        // Разбор конверта общий с исполнителем процессов: правила «что считать успехом,
        // предметной ошибкой и нарушением контракта» обязаны совпадать дословно на обоих путях
        // исполнения одного и того же действия. Держать их в двух местах означало бы рано или
        // поздно разойтись в трактовке одного конверта.
        // Ожидаемая версия передаётся внутрь: свежее исполнение обязано подтвердить, что
        // отработала именно та версия, по схеме и политике которой запрос был проверен. Для
        // повтора требование неприменимо — там версия заведомо своя, историческая.
        var interpreted = RegisteredAction.Interpret(
            envelopeText, manifest.Outcomes, manifest.ResponseSchema, MaxResultBytes, manifest.Version);

        if (interpreted.Status == RegisteredActionStatus.ContractViolation)
        {
            // Нарушение контракта зарегистрированной функции: неизвестный исход, результат мимо
            // схемы ответа или конверт не той формы. Любой случай — полный откат.
            await transaction.RollbackAsync(CancellationToken.None);
            evidence.Status = "ERROR";
            evidence.ErrorCode = ContractCodes.ContractViolation;
            return Failure(
                ContractCodes.ContractViolation,
                interpreted.Message,
                correlationId,
                interpreted.ActionVersion ?? manifest.Version);
        }

        if (interpreted.Status == RegisteredActionStatus.DomainError)
        {
            // Любой объявленный сбой (status=error) полностью откатывает весь предметный эффект
            // ЕЩЁ ДО того, как клиенту будет отправлен ответ. Например, если платёжная функция
            // вернула ошибку уже после того, как успела что-то вставить в таблицу — эта вставка
            // будет отменена транзакцией целиком.
            await transaction.RollbackAsync(CancellationToken.None);
            evidence.Status = "ERROR";
            evidence.ErrorCode = interpreted.Code;

            return new ActionResponse(
                ContractCodes.HttpStatus(interpreted.Code),
                Envelope.Error(
                    interpreted.Code,
                    interpreted.Message,
                    correlationId,
                    manifest.Version,
                    interpreted.Retryable));
        }

        // Наружу и в технический след идёт версия, которая результат действительно произвела.
        var reportedVersion = interpreted.ActionVersion ?? manifest.Version;
        var replayed = interpreted.Replayed;
        var outcome = interpreted.Outcome!;
        var result = interpreted.Result!;
        evidence.Version = reportedVersion;

        // Граница закрытого профиля «эффект есть, commit ещё нет». Принудительная остановка здесь
        // обязана откатить всё содержимое транзакции целиком, включая результат идемпотентности.
        await failpoints.StopIfReachedAsync(module, action, ActionPhase.BeforeCommit);

        // Все проверки пройдены — теперь и только теперь фиксируем транзакцию.
        await transaction.CommitAsync(budget.Token);

        // Граница закрытого профиля «commit есть, ответа ещё нет». Сохранённое сообщение обязано
        // пережить смерть процесса и быть применённым обычным согласователем, а не api.
        await failpoints.StopIfReachedAsync(module, action, ActionPhase.AfterCommit);

        dependencyState.MarkAvailable();
        evidence.Status = "OK";
        evidence.Outcome = outcome;

        logger.LogInformation(
            "action completed correlationId={CorrelationId} route={Module}.{Action} version={Version} outcome={Outcome} replayed={Replayed}",
            correlationId,
            module,
            action,
            reportedVersion,
            outcome,
            replayed);

        return new ActionResponse(
            StatusCodes.Status200OK,
            Envelope.Success(outcome, result, correlationId, reportedVersion));
    }

    /// <summary>Строит ответ об ошибке в едином формате контракта, автоматически проставляя признак "можно повторить" для временных сбоев.</summary>
    private static ActionResponse Failure(
        string code,
        string message,
        Guid correlationId,
        int? actionVersion,
        JsonNode? details = null)
        => new(
            ContractCodes.HttpStatus(code),
            Envelope.Error(
                code,
                message,
                correlationId,
                actionVersion,
                retryable: code is ContractCodes.DependencyUnavailable or ContractCodes.ActionTimeout,
                details: details));

    /// <summary>Человекочитаемое, но не раскрывающее внутренние детали, описание технического сбоя.</summary>
    private static string DescribeTechnicalFailure(string code) => code switch
    {
        ContractCodes.DependencyUnavailable => "authoritative state is temporarily unavailable",
        ContractCodes.ActionTimeout => "action deadline was reached",
        _ => "request could not be completed",
    };

    /// <summary>
    /// Проверяет, что строка похожа на корректный SQL-идентификатор PostgreSQL: начинается со
    /// строчной латинской буквы, дальше — строчные буквы, цифры или подчёркивание, не длиннее 63
    /// символов. Используется для проверки сегментов маршрута (module/action) до того, как они
    /// попадут в запрос к базе данных.
    /// </summary>
    internal static bool IsSqlIdentifier(string value)
    {
        if (value.Length is 0 or > 63 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in value)
        {
            var valid = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
