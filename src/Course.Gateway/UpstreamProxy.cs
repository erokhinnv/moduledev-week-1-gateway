using System.Net;
using System.Text.Json.Nodes;
using Course.Shared;

namespace Course.Gateway;

/// <summary>Результат проверки готовности внутреннего api, увиденный со стороны шлюза.</summary>
public sealed record UpstreamReadiness(bool Ready, string Reason, int Status);

/// <summary>
/// Прозрачное проксирование запросов во внутренний сервис api.
///
/// Шлюз пересылает запрос и все контрактные заголовки (Authorization, Idempotency-Key,
/// X-Action-Version и т.д.) дальше, НЕ МЕНЯЯ их смысла — то есть не подменяя и не переинтерпретируя
/// содержимое. При этом шлюз никогда не рассматривает и не логирует учётные данные или тело
/// запроса: он работает как честный "почтальон", а не как участник бизнес-логики.
/// </summary>
public sealed class UpstreamProxy(
    IHttpClientFactory httpClientFactory,
    GatewayOptions options,
    ILogger<UpstreamProxy> logger)
{
    /// <summary>Заголовки, которые пересылаются от клиента к внутреннему api без изменений.</summary>
    private static readonly string[] ForwardedRequestHeaders =
    [
        "Authorization",
        "Content-Type",
        "Accept",
        "Accept-Encoding",
        "Idempotency-Key",
        "X-Action-Version",
        "X-Request-Id",
    ];

    /// <summary>Служебные заголовки транспортного уровня, которые не нужно (и не следует) пересылать буквально.</summary>
    private static readonly string[] SkippedResponseHeaders =
    [
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Upgrade",
        "Server",
        "Date",
    ];

    /// <summary>Пересылает один HTTP-запрос во внутренний api и переливает его ответ обратно клиенту.</summary>
    public async Task ForwardAsync(HttpContext context)
    {
        // Собственный идентификатор шлюза для этого конкретного запроса — используется только
        // для внутренней диагностики (лог, заголовок X-Gateway-Request-Id), не путать с
        // серверным correlationId, который генерирует сам сервис api.
        var gatewayRequestId = Guid.NewGuid();
        var target = new UriBuilder(options.UpstreamBaseAddress)
        {
            Path = context.Request.Path.Value ?? "/",
            Query = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value!.TrimStart('?')
                : string.Empty,
        }.Uri;

        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            // Тело запроса передаётся потоково (не копируется целиком в память), чтобы не
            // раздувать память шлюза на больших телах и не задерживать пересылку.
            upstreamRequest.Content = new StreamContent(context.Request.Body);
        }

        foreach (var name in ForwardedRequestHeaders)
        {
            if (!context.Request.Headers.TryGetValue(name, out var values) || values.Count == 0)
            {
                continue;
            }

            var value = values.ToArray()!;
            if (!upstreamRequest.Headers.TryAddWithoutValidation(name, value))
            {
                upstreamRequest.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        upstreamRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        upstreamRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        upstreamRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value ?? string.Empty);
        upstreamRequest.Headers.TryAddWithoutValidation("X-Gateway-Request-Id", gatewayRequestId.ToString());

        var client = httpClientFactory.CreateClient(GatewayOptions.UpstreamClient);

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Внутренний api не ответил за отведённое время (не по вине самого клиента, который
            // соединение не обрывал) — это таймаут на границе шлюза.
            logger.LogWarning(
                "upstream timeout gatewayRequestId={GatewayRequestId} method={Method} path={Path}",
                gatewayRequestId,
                context.Request.Method,
                context.Request.Path);
            await GatewayResponses.WriteAsync(
                context,
                504,
                Envelope.Error(ContractCodes.ActionTimeout, "internal api did not answer in time", null, null, retryable: true));
            return;
        }
        catch (HttpRequestException exception)
        {
            // Соединение до внутреннего api вообще не установилось (например api ещё не поднялся
            // или его контейнер пересоздаётся) — сообщаем клиенту о временной недоступности.
            logger.LogWarning(
                "upstream unavailable gatewayRequestId={GatewayRequestId} path={Path} reason={Reason}",
                gatewayRequestId,
                context.Request.Path,
                exception.HttpRequestError);
            await GatewayResponses.WriteAsync(
                context,
                503,
                Envelope.Error(ContractCodes.DependencyUnavailable, "internal api is unavailable", null, null, retryable: true));
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            foreach (var header in upstreamResponse.Headers.Concat(upstreamResponse.Content.Headers))
            {
                if (SkippedResponseHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            logger.LogInformation(
                "forwarded gatewayRequestId={GatewayRequestId} method={Method} path={Path} status={Status}",
                gatewayRequestId,
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode);

            await upstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    /// <summary>Спрашивает /health/ready у внутреннего api и переводит результат в форму, удобную для собственной проверки готовности шлюза.</summary>
    public async Task<UpstreamReadiness> ProbeReadinessAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(GatewayOptions.ProbeClient);
        try
        {
            using var response = await client.GetAsync("/health/ready", cancellationToken);
            return new UpstreamReadiness(
                response.StatusCode == HttpStatusCode.OK,
                response.StatusCode == HttpStatusCode.OK ? "ready" : "internal api is not ready",
                (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            return new UpstreamReadiness(false, "internal api is unreachable", 0);
        }
    }
}

/// <summary>Небольшой общий помощник для записи JSON-ответа шлюза.</summary>
public static class GatewayResponses
{
    public static async Task WriteAsync(HttpContext context, int status, JsonNode body)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted);
    }
}
