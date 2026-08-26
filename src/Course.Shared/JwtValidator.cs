using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Course.Shared;

/// <summary>
/// Личность (identity), доказанная проверенным токеном доступа (JWT).
///
/// Это единственный источник данных о том, кто обращается к системе (principal), от лица какого
/// потребителя (consumer) и какими правами (scopes) он обладает. Никакой другой код в проекте не
/// имеет права заполнить эти поля из чего-то ещё, кроме успешно проверенного токена — в частности,
/// поля тела запроса (payload) никогда не могут подменить эти значения.
/// </summary>
public sealed record TrustedIdentity(string Principal, string Consumer, IReadOnlyList<string> Scopes);

/// <summary>Настройки проверки JWT, читаемые из переменных окружения при старте сервиса.</summary>
public sealed class JwtOptions
{
    /// <summary>Ожидаемое значение claim "iss" (кто выпустил токен).</summary>
    public required string Issuer { get; init; }

    /// <summary>Ожидаемое значение claim "aud" (для кого предназначен токен).</summary>
    public required string Audience { get; init; }

    /// <summary>Секретный ключ подписи HS256 в виде байтов.</summary>
    public required byte[] SigningKey { get; init; }

    /// <summary>Допустимый разброс часов (clock skew) при проверке времени действия токена.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Собирает настройки из переменных окружения COURSE_JWT_ISSUER, COURSE_JWT_AUDIENCE и
    /// COURSE_JWT_SIGNING_KEY. Ключ подписи обязан быть не короче 32 байт — это требование
    /// контракта задания (минимальная длина ключа HS256).
    /// </summary>
    public static JwtOptions FromEnvironment()
    {
        var issuer = Env.Require("COURSE_JWT_ISSUER");
        var audience = Env.Require("COURSE_JWT_AUDIENCE");
        var key = Env.Require("COURSE_JWT_SIGNING_KEY");
        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException("COURSE_JWT_SIGNING_KEY must be at least 32 bytes long");
        }

        return new JwtOptions { Issuer = issuer, Audience = audience, SigningKey = keyBytes };
    }
}

/// <summary>
/// Проверка JWT по алгоритму HS256 с явной проверкой каждого claim.
///
/// Важно: корректной подписи самой по себе НЕДОСТАТОЧНО. Даже если подпись верна, ещё отдельно
/// проверяются наличие, JSON-тип и значение каждого обязательного claim'а. Поэтому, например,
/// токен с правильной подписью, но с claim "iss" типа число вместо строки, будет отклонён с кодом
/// auth.invalid, а не приведёт к необработанному сбою фреймворка (500 ошибке).
/// </summary>
public sealed class JwtValidator(JwtOptions options)
{
    private readonly JwtOptions _options = options;

    /// <summary>
    /// Пытается проверить заголовок Authorization и извлечь из него доверенную личность.
    /// Возвращает true и заполненный identity при успехе; при неудаче возвращает false и
    /// человекочитаемую причину (только для внутренних логов — наружу эта причина не идёт,
    /// клиент всегда видит одинаковый код auth.invalid).
    /// </summary>
    public bool TryValidate(string? authorizationHeader, out TrustedIdentity? identity, out string reason)
    {
        identity = null;

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            reason = "authorization header is missing";
            return false;
        }

        // Схема авторизации обязана быть Bearer — никакая другая (например Basic) не принимается.
        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            reason = "authorization scheme must be Bearer";
            return false;
        }

        // Компактный JWS (JSON Web Signature) состоит из трёх частей, разделённых точками:
        // заголовок.полезная_нагрузка.подпись.
        var token = authorizationHeader[prefix.Length..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            reason = "token is not a compact JWS";
            return false;
        }

        if (!TryDecode(parts[0], out var headerBytes) ||
            !TryDecode(parts[1], out var payloadBytes) ||
            !TryDecode(parts[2], out var signature))
        {
            reason = "token segments are not base64url";
            return false;
        }

        JsonDocument header;
        JsonDocument payload;
        try
        {
            header = JsonDocument.Parse(headerBytes);
        }
        catch (JsonException)
        {
            reason = "token header is not JSON";
            return false;
        }

        using (header)
        {
            if (header.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "token header is not a JSON object";
                return false;
            }

            // Единственный поддерживаемый алгоритм — HS256. В частности, алгоритм "none"
            // (когда подпись фактически отсутствует) отклоняется здесь же — это классическая
            // уязвимость JWT, и её нужно исключить явной проверкой значения "alg".
            if (!TryGetString(header.RootElement, "alg", out var algorithm) || algorithm != "HS256")
            {
                reason = "unsupported token algorithm";
                return false;
            }

            if (header.RootElement.TryGetProperty("typ", out var typ) &&
                (typ.ValueKind != JsonValueKind.String ||
                 !string.Equals(typ.GetString(), "JWT", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "unsupported token type";
                return false;
            }
        }

        // Подпись считается по первым двум сегментам токена (заголовок.полезная_нагрузка) и
        // сравнивается с присланной подписью защищённым от атак по времени способом
        // (FixedTimeEquals), чтобы нельзя было подобрать подпись побайтово, замеряя задержки ответа.
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var expected = HMACSHA256.HashData(_options.SigningKey, signingInput);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            reason = "token signature does not verify";
            return false;
        }

        try
        {
            payload = JsonDocument.Parse(payloadBytes);
        }
        catch (JsonException)
        {
            reason = "token payload is not JSON";
            return false;
        }

        using (payload)
        {
            var claims = payload.RootElement;
            if (claims.ValueKind != JsonValueKind.Object)
            {
                reason = "token payload is not a JSON object";
                return false;
            }

            // Ниже — последовательная проверка каждого обязательного claim'а: он должен
            // присутствовать, иметь правильный JSON-тип и (где применимо) правильное значение.

            if (!TryGetString(claims, "iss", out var issuer) || issuer != _options.Issuer)
            {
                reason = "iss claim is missing, malformed or unexpected";
                return false;
            }

            if (!AudienceMatches(claims))
            {
                reason = "aud claim is missing, malformed or unexpected";
                return false;
            }

            if (!TryGetString(claims, "sub", out var subject) || subject.Length == 0)
            {
                reason = "sub claim is missing or malformed";
                return false;
            }

            if (!TryGetString(claims, "consumer", out var consumer) || consumer.Length == 0)
            {
                reason = "consumer claim is missing or malformed";
                return false;
            }

            if (!claims.TryGetProperty("scope", out var scopeClaim) || scopeClaim.ValueKind != JsonValueKind.String)
            {
                reason = "scope claim is missing or malformed";
                return false;
            }

            if (!TryGetUnixSeconds(claims, "iat", required: true, out _))
            {
                reason = "iat claim is missing or malformed";
                return false;
            }

            if (!TryGetUnixSeconds(claims, "exp", required: true, out var expiresAt))
            {
                reason = "exp claim is missing or malformed";
                return false;
            }

            if (!TryGetUnixSeconds(claims, "nbf", required: false, out var notBefore))
            {
                reason = "nbf claim is malformed";
                return false;
            }

            // Проверка срока действия токена с учётом допустимого разброса часов (clock skew).
            var now = DateTimeOffset.UtcNow;
            if (expiresAt is not null && now > expiresAt.Value + _options.ClockSkew)
            {
                reason = "token has expired";
                return false;
            }

            if (notBefore is not null && now + _options.ClockSkew < notBefore.Value)
            {
                reason = "token is not valid yet";
                return false;
            }

            // Claim "scope" — это строка с правами через пробел (как в OAuth 2.0),
            // например "payment:write payment:read". Разбиваем её на массив отдельных прав,
            // убираем дубликаты.
            var scopes = scopeClaim.GetString()!
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            identity = new TrustedIdentity(subject, consumer, scopes);
            reason = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Claim "aud" по стандарту JWT может быть либо одиночной строкой, либо массивом строк.
    /// Проверяем оба варианта и требуем, чтобы ожидаемая audience присутствовала.
    /// </summary>
    private bool AudienceMatches(JsonElement claims)
    {
        if (!claims.TryGetProperty("aud", out var audience))
        {
            return false;
        }

        return audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == _options.Audience,
            JsonValueKind.Array => audience.EnumerateArray().Any(
                item => item.ValueKind == JsonValueKind.String && item.GetString() == _options.Audience),
            _ => false,
        };
    }

    /// <summary>Безопасно читает строковое свойство JSON-объекта, проверяя его тип.</summary>
    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// Безопасно читает числовое свойство JSON-объекта как Unix-время (секунды с 1970 года) и
    /// превращает его в DateTimeOffset. Если свойство отсутствует и оно необязательное — это не
    /// ошибка (возвращается true с value = null).
    /// </summary>
    private static bool TryGetUnixSeconds(JsonElement element, string name, bool required, out DateTimeOffset? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return !required;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var seconds))
        {
            return false;
        }

        if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() ||
            seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    /// <summary>Декодирует один сегмент токена из base64url в байты, не бросая исключение при ошибке формата.</summary>
    private static bool TryDecode(string segment, out byte[] value)
    {
        try
        {
            value = Base64Url.DecodeFromChars(segment);
            return true;
        }
        catch (FormatException)
        {
            value = [];
            return false;
        }
    }
}
