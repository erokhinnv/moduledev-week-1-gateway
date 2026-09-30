using System.Text.Json.Nodes;
using Course.Api;
using Course.Shared;

// Точка входа внутреннего C#-сервиса api. Он не публикует наружу ни одного host-порта в Docker
// Compose — снаружи достучаться до него можно только через сервис gateway.
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
// Одна строка журнала — один JSON object (контракт наблюдаемости недели 4). Именованные
// placeholder'ы вызовов logger.Log* становятся полями записи, поэтому путь операции находится
// фильтром по correlationId, а не разбором текста строки.
builder.Logging.AddProvider(new JsonConsoleLoggerProvider("api"));

// Учётные данные, токены и полные тела запросов НИКОГДА не попадают в логи: в лог пишутся только
// технические события жизненного цикла фреймворка и собственные структурированные события этого
// сервиса (см. вызовы logger.Log* ниже и в ActionRuntime), которые заведомо не содержат секретов.
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Logging.AddFilter("Npgsql", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1024 * 1024;
});

// Регистрация сервисов в контейнере внедрения зависимостей (Dependency Injection).
builder.Services.AddSingleton(_ => JwtOptions.FromEnvironment());
builder.Services.AddSingleton<JwtValidator>();
builder.Services.AddSingleton(_ => CourseDatabase.CreateDataSource(
    connectTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_CONNECT_TIMEOUT", 2),
    commandTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_COMMAND_TIMEOUT", 30),
    maxPoolSize: Env.OptionalInt("COURSE_POSTGRES_MAX_POOL", 60)));
builder.Services.AddSingleton<DependencyState>();
builder.Services.AddSingleton<ActionCatalog>();
builder.Services.AddSingleton<DispatchRecorder>();
// Очередь регистрируется и как обычная зависимость (её пополняет ActionRuntime), и как фоновая
// служба (она же разбирает очередь) — оба обращения обязаны попасть в ОДИН экземпляр.
builder.Services.AddSingleton<DispatchQueue>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DispatchQueue>());
builder.Services.AddSingleton<ReadinessProbe>();
builder.Services.AddSingleton<RuntimeMetrics>();
builder.Services.AddSingleton(_ => SignatureBoundary.FromEnvironment());
// Точка аварийной остановки читается один раз при старте: она принадлежит процессу целиком, а не
// отдельному запросу, и менять её на лету было бы способом остановить работающий контур.
builder.Services.AddSingleton(_ => Failpoint.FromEnvironment("COURSE_APPLICATION_NAME"));
builder.Services.AddSingleton<ActionFailpoints>();
builder.Services.AddSingleton<ActionRuntime>();

var app = builder.Build();

// Любой непредвиденный сбой обязан вернуться клиенту в виде контрактного JSON-конверта, а не в
// виде стандартной страницы ошибки ASP.NET Core (которая могла бы раскрыть внутренние детали).
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException)
    {
        // Например, слишком большое тело запроса или сломанный HTTP-протокол на уровне Kestrel.
        await WriteAsync(
            context,
            400,
            Envelope.Error(ContractCodes.RequestInvalid, "request could not be read", Guid.NewGuid(), null));
    }
    catch (Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var correlationId = Guid.NewGuid();
        // Только тип сбоя и SQLSTATE — сообщения PostgreSQL умеют содержать значения данных.
        logger.LogError(
            "unhandled failure correlationId={CorrelationId} failure={Failure}",
            correlationId,
            Redaction.Describe(exception));
        await WriteAsync(
            context,
            500,
            Envelope.Error(ContractCodes.InternalError, "request could not be completed", correlationId, null));
    }
});

// ---------------------------------------------------------------------------
// Единственный универсальный маршрут для действий. Здесь и нигде больше в проекте НЕТ отдельного
// контроллера под каждое предметное действие — весь код одинаков для payment.request,
// operation.get и для любого будущего действия, которое появится через публикацию манифеста.
// ---------------------------------------------------------------------------
app.Map("/api/{**path}", async (HttpContext context, ActionRuntime runtime) =>
{
    var path = context.Request.RouteValues["path"] as string ?? string.Empty;
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    var body = await ReadBodyAsync(context);

    var request = new ActionRequest(
        context.Request.Method,
        segments,
        context.Request.Headers.Authorization.ToString(),
        Header(context, "X-Action-Version"),
        Header(context, "Idempotency-Key"),
        body,
        context.Request.Headers.TryGetValue(SignatureBoundary.HeaderName, out var signatures)
            ? signatures.Where(value => value is not null).Select(value => value!).ToArray()
            : []);

    var response = await runtime.ExecuteAsync(request, context.RequestAborted);
    await WriteAsync(context, response.Status, response.Body);
});

// ---------------------------------------------------------------------------
// Документы OpenAPI, построенные из опубликованных манифестов "на лету" при каждом запросе.
// ---------------------------------------------------------------------------
app.MapGet("/openapi/default.json", async (HttpContext context, ActionCatalog catalog) =>
{
    var manifests = await catalog.DefaultRoutesAsync(context.RequestAborted);
    await WriteAsync(context, 200, OpenApiBuilder.DefaultDocument(manifests));
});

app.MapGet("/openapi/actions/{module}/{action}/{document}", async (
    HttpContext context,
    ActionCatalog catalog,
    string module,
    string action,
    string document) =>
{
    // document ожидается в виде "<версия>.json", например "1.json".
    if (!document.EndsWith(".json", StringComparison.Ordinal) ||
        !int.TryParse(document[..^5], out var version) ||
        version < 1 ||
        !ActionRuntime.IsSqlIdentifier(module) ||
        !ActionRuntime.IsSqlIdentifier(action))
    {
        await WriteAsync(
            context,
            400,
            Envelope.Error(ContractCodes.RequestInvalid, "openapi document path is invalid", Guid.NewGuid(), null));
        return;
    }

    var manifest = await catalog.DescribeAsync(module, action, version, context.RequestAborted);
    if (manifest is null)
    {
        await WriteAsync(
            context,
            404,
            Envelope.Error(ContractCodes.ActionNotFound, "action version is not published", Guid.NewGuid(), version));
        return;
    }

    await WriteAsync(context, 200, OpenApiBuilder.VersionDocument(manifest));
});

// ---------------------------------------------------------------------------
// Проверки состояния (health checks) и метрики.
//
// Форма ответов закреплена контрактом наблюдаемости недели 4 и намеренно минимальна: причина
// отказа уходит в журнал, а не в тело probe. Живость и готовность имеют РАЗНУЮ семантику —
// liveness не выполняет глубокую проверку зависимостей и остаётся зелёной, пока процесс способен
// ответить, иначе перезапускали бы живой сервис из-за недоступной базы.
// ---------------------------------------------------------------------------
app.MapGet("/health/live", async (HttpContext context) =>
    await WriteAsync(context, 200, new JsonObject { ["status"] = "live" }));

// Readiness ("готов принимать реальную нагрузку") реально проверяет доступность PostgreSQL и
// завершённость инициализации — см. подробности в ReadinessProbe.cs.
app.MapGet("/health/ready", async (HttpContext context, ReadinessProbe probe) =>
{
    var state = await probe.CheckAsync(context.RequestAborted);
    if (state.Ready)
    {
        await WriteAsync(context, 200, new JsonObject { ["status"] = "ready" });
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogWarning("readiness reported not ready reason={Reason}", state.Reason);
    await WriteAsync(context, 503, new JsonObject
    {
        ["status"] = "not_ready",
        ["code"] = ContractCodes.DependencyUnavailable,
    });
});

// Метрики — проекция авторитетного состояния, а не собственный счётчик процесса: значение
// очереди обязано совпадать с тем, что показывают проверочные представления PostgreSQL.
app.MapGet("/metrics", async (HttpContext context, RuntimeMetrics metrics) =>
{
    var document = await metrics.RenderAsync(context.RequestAborted);
    if (document is null)
    {
        await WriteAsync(context, 503, new JsonObject
        {
            ["status"] = "not_ready",
            ["code"] = ContractCodes.DependencyUnavailable,
        });
        return;
    }

    if (context.Response.HasStarted)
    {
        return;
    }

    context.Response.StatusCode = 200;
    context.Response.ContentType = OpenMetrics.MediaType;
    await context.Response.WriteAsync(document, context.RequestAborted);
});

// Любой маршрут, не подпадающий под перечисленные выше — это маршрут вне контракта курса.
app.MapFallback(async (HttpContext context) =>
    await WriteAsync(
        context,
        404,
        Envelope.Error(ContractCodes.ActionNotFound, "route is not part of the course contract", Guid.NewGuid(), null)));

app.Run();

/// <summary>Достаёт значение заголовка запроса по имени (последнее значение, если их несколько).</summary>
static string? Header(HttpContext context, string name)
    => context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0
        ? values[^1]
        : null;

/// <summary>Полностью читает тело HTTP-запроса в массив байт.</summary>
static async Task<byte[]> ReadBodyAsync(HttpContext context)
{
    using var buffer = new MemoryStream();
    await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
    return buffer.ToArray();
}

/// <summary>Записывает JSON-ответ с указанным статус-кодом, если ответ ещё не начал отправляться.</summary>
static async Task WriteAsync(HttpContext context, int status, JsonNode body)
{
    if (context.Response.HasStarted)
    {
        return;
    }

    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted);
}
