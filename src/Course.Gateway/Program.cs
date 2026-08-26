using System.Net;
using System.Text.Json.Nodes;
using Course.Gateway;
using Course.Shared;

// Точка входа публичного C#-шлюза (gateway). Это ЕДИНСТВЕННЫЙ сервис во всём проекте, который
// публикует наружу host-порт (8080) в Docker Compose — вся остальная система недоступна извне.
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1024 * 1024;
});

var options = GatewayOptions.FromEnvironment();
builder.Services.AddSingleton(options);

// HttpClient настроен с коротким временем жизни пула соединений (PooledConnectionLifetime):
// благодаря этому DNS-имя внутреннего сервиса api в сети Compose переразрешается регулярно, и
// если контейнер api будет пересоздан (получит новый IP-адрес), gateway обнаружит это
// самостоятельно — без необходимости перезапускать сам gateway.
builder.Services.AddHttpClient(GatewayOptions.UpstreamClient, client =>
    {
        client.BaseAddress = options.UpstreamBaseAddress;
        client.Timeout = options.UpstreamTimeout;
        client.DefaultRequestVersion = HttpVersion.Version11;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(3),
        AllowAutoRedirect = false,
        UseCookies = false,
    });

// Отдельный, более "нервный" (короткий таймаут) HTTP-клиент используется только для проверки
// готовности (/health/ready) — чтобы медленный внутренний сервис не задерживал ответ на
// собственную проверку готовности gateway дольше, чем нужно.
builder.Services.AddHttpClient(GatewayOptions.ProbeClient, client =>
    {
        client.BaseAddress = options.UpstreamBaseAddress;
        client.Timeout = options.ProbeTimeout;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        AllowAutoRedirect = false,
        UseCookies = false,
    });

builder.Services.AddSingleton<UpstreamProxy>();

var app = builder.Build();

app.Logger.LogInformation(
    "gateway started upstream={Upstream} port={Port}",
    options.UpstreamBaseAddress,
    options.ListenPort);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException)
    {
        await GatewayResponses.WriteAsync(
            context,
            400,
            Envelope.Error(ContractCodes.RequestInvalid, "request could not be read", null, null));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "gateway failure path={Path}", context.Request.Path);
        await GatewayResponses.WriteAsync(
            context,
            500,
            Envelope.Error(ContractCodes.InternalError, "request could not be completed", null, null));
    }
});

// ---------------------------------------------------------------------------
// Белый список (whitelist) маршрутов. Принимаются ТОЛЬКО действия, документы OpenAPI и health.
// Всё остальное отклоняется прямо на границе. Сам gateway не содержит ни каталога действий,
// ни предметной логики — он лишь прозрачно передаёт разрешённые запросы дальше во внутренний api.
// ---------------------------------------------------------------------------
app.Map("/api/{**path}", (HttpContext context, UpstreamProxy proxy) => proxy.ForwardAsync(context));

app.MapGet("/openapi/default.json", (HttpContext context, UpstreamProxy proxy) => proxy.ForwardAsync(context));

app.MapGet("/openapi/actions/{module}/{action}/{document}",
    (HttpContext context, UpstreamProxy proxy) => proxy.ForwardAsync(context));

// Liveness отвечает только за сам процесс gateway и остаётся зелёным, даже если внутренний api недоступен.
app.MapGet("/health/live", (HttpContext context) => GatewayResponses.WriteAsync(context, 200, new JsonObject
{
    ["status"] = "ok",
    ["service"] = "gateway",
    ["check"] = "process",
}));

// Готовность gateway — это готовность внутреннего api, что в свою очередь доказывает готовность
// PostgreSQL: gateway просто спрашивает /health/ready у api и пересылает его вывод.
app.MapGet("/health/ready", async (HttpContext context, UpstreamProxy proxy) =>
{
    var upstream = await proxy.ProbeReadinessAsync(context.RequestAborted);
    await GatewayResponses.WriteAsync(context, upstream.Ready ? 200 : 503, new JsonObject
    {
        ["status"] = upstream.Ready ? "ok" : "error",
        ["service"] = "gateway",
        ["check"] = "api",
        ["reason"] = upstream.Reason,
        ["upstreamStatus"] = upstream.Status,
    });
});

// Всё, что не попало в белый список выше — вне контракта курса.
app.MapFallback((HttpContext context) => GatewayResponses.WriteAsync(
    context,
    404,
    Envelope.Error(ContractCodes.ActionNotFound, "route is not part of the course contract", null, null)));

app.Run();
