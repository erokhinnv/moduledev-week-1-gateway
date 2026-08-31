using Course.Shared;

namespace Course.Gateway;

/// <summary>Настройки шлюза, читаемые из переменных окружения при старте сервиса.</summary>
public sealed class GatewayOptions
{
    /// <summary>Имя именованного HttpClient, используемого для проксирования обычных запросов.</summary>
    public const string UpstreamClient = "course-api";

    /// <summary>Имя именованного HttpClient с более коротким таймаутом — только для проверки готовности.</summary>
    public const string ProbeClient = "course-api-probe";

    /// <summary>Базовый адрес внутреннего сервиса api (по DNS-имени Docker Compose, не localhost).</summary>
    public required Uri UpstreamBaseAddress { get; init; }

    public required int ListenPort { get; init; }

    /// <summary>
    /// Бюджет ожидания ответа от внутреннего api.
    ///
    /// Он обязан быть ЗАВЕДОМО БОЛЬШЕ контрактного максимума timeout_ms (30 секунд) плюс запас на
    /// накладные расходы. Иначе возникает худший из возможных исходов: действие успевает
    /// зафиксировать предметный эффект, а клиент получает от шлюза таймаут и считает, что ничего
    /// не произошло. Раньше здесь стояло 12 секунд, то есть меньше контрактного максимума.
    ///
    /// Подстроить бюджет под конкретное действие шлюз не может принципиально: по контракту он не
    /// имеет права знать каталог, а значит и timeout_ms манифеста. Поэтому единственный
    /// допустимый вариант — фиксированный бюджет выше контрактного потолка.
    /// </summary>
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(35);

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    public static GatewayOptions FromEnvironment()
    {
        var upstream = Env.Optional("COURSE_API_BASE_URL", "http://api:8080");
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("COURSE_API_BASE_URL must be an absolute URL");
        }

        // Внутренний сервис api обязан адресоваться по своему имени в сети Docker Compose
        // (например "http://api:8080"), а НЕ через localhost/127.0.0.1 — иначе gateway будет
        // фактически пытаться обратиться сам к себе, а не к отдельному контейнеру api. Это прямое
        // требование контракта: "gateway обращается к api по Compose DNS, а не через localhost".
        if (uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "COURSE_API_BASE_URL must use the Compose service name of the internal api, not a loopback address");
        }

        return new GatewayOptions
        {
            UpstreamBaseAddress = uri,
            ListenPort = Env.OptionalInt("COURSE_GATEWAY_PORT", 8080),
            UpstreamTimeout = TimeSpan.FromSeconds(Env.OptionalInt("COURSE_GATEWAY_UPSTREAM_TIMEOUT", 35)),
        };
    }
}
