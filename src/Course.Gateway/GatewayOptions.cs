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

    /// <summary>
    /// Верхняя граница ответа, который шлюз соглашается переслать клиенту.
    ///
    /// Устроена так же, как бюджет ожидания: она обязана быть ЗАВЕДОМО БОЛЬШЕ того, что api имеет
    /// право отдать, — иначе шлюз резал бы законные ответы. Самый большой законный ответ — конверт
    /// действия с результатом до COURSE_MAX_RESULT_BYTES (512 КБ); четырёхкратный запас покрывает
    /// конверт и документ OpenAPI с большим каталогом.
    ///
    /// Своя граница у шлюза нужна потому, что ограничение api — это обещание api, а не гарантия:
    /// единственная публичная точка входа не должна переливать неограниченный объём только потому,
    /// что внутренний сервис ошибся или был скомпрометирован. Тело запроса шлюз ограничивает давно;
    /// до этой правки ответ не ограничивался ничем.
    /// </summary>
    public long MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

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

        // Граница проверяется ПРИ СТАРТЕ, как и адрес api выше. Ноль или отрицательное значение не
        // ломает запуск, но делает шлюз бесполезным: он отверг бы каждый непустой ответ или падал бы
        // на каждом запросе. Ошибка конфигурации обязана быть громкой и ранней, а не размазанной по
        // трафику.
        var maxResponseBytes = Env.OptionalInt("COURSE_GATEWAY_MAX_RESPONSE_BYTES", 2 * 1024 * 1024);
        if (maxResponseBytes <= 0)
        {
            throw new InvalidOperationException(
                "COURSE_GATEWAY_MAX_RESPONSE_BYTES must be a positive number of bytes");
        }

        return new GatewayOptions
        {
            UpstreamBaseAddress = uri,
            ListenPort = Env.OptionalInt("COURSE_GATEWAY_PORT", 8080),
            UpstreamTimeout = TimeSpan.FromSeconds(Env.OptionalInt("COURSE_GATEWAY_UPSTREAM_TIMEOUT", 35)),
            MaxResponseBytes = maxResponseBytes,
        };
    }
}
