using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>Итог проверки подписи: запрос не подписан, подписан верно или подпись отвергнута.</summary>
public enum SignatureVerdict
{
    Absent,
    Verified,
    Invalid,
}

/// <summary>
/// Общая граница подписанных запросов: X-Provider-Signature: v1=&lt;lowercase hex HMAC-SHA256&gt;.
///
/// Подпись проверяется над ТОЧНЫМИ байтами HTTP-тела до разбора JSON и до любого предметного
/// вызова. Граница ничего не знает о действиях: она не решает, кому подпись обязательна, а лишь
/// сообщает целевой функции доверенный маркер transport.signatureVerified. Требовать маркер —
/// дело самой зарегистрированной функции (receipt.accept), поэтому новое подписанное действие
/// не требует правки C#.
///
/// Ключ — точные UTF-8 байты PROVIDER_HMAC_SECRET, без trim и без Base64/hex-декодирования.
/// Ни ключ, ни полученная подпись не попадают ни в контекст, ни в журнал.
/// </summary>
public sealed class SignatureBoundary
{
    public const string HeaderName = "X-Provider-Signature";
    public const int SupportedVersion = 1;

    private const string VersionPrefix = "v1=";
    private const int DigestHexLength = 64;

    private readonly byte[]? _key;

    public SignatureBoundary(string? secret)
    {
        // Пустой секрет не является ключом: с ним любая «подпись» была бы проверяемой, поэтому
        // без настроенного секрета все подписанные запросы отвергаются.
        _key = string.IsNullOrEmpty(secret) ? null : Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Секрет читается из окружения как есть: Environment не обрезает значение.</summary>
    public static SignatureBoundary FromEnvironment()
        => new(Environment.GetEnvironmentVariable("PROVIDER_HMAC_SECRET"));

    /// <param name="headerValues">Все значения заголовка; несколько подписей сразу — неверный формат.</param>
    /// <param name="body">Точные байты тела запроса.</param>
    public SignatureVerdict Verify(IReadOnlyList<string> headerValues, ReadOnlySpan<byte> body)
    {
        if (headerValues.Count == 0)
        {
            return SignatureVerdict.Absent;
        }

        if (headerValues.Count > 1 || _key is null)
        {
            return SignatureVerdict.Invalid;
        }

        var value = headerValues[0];
        if (value.Length != VersionPrefix.Length + DigestHexLength ||
            !value.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return SignatureVerdict.Invalid;
        }

        Span<byte> presented = stackalloc byte[DigestHexLength / 2];
        if (!TryDecodeLowerHex(value.AsSpan(VersionPrefix.Length), presented))
        {
            return SignatureVerdict.Invalid;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, body, expected);

        // Сравниваются декодированные байты за постоянное время: время ответа не подсказывает,
        // сколько первых байт подписи угадано.
        return CryptographicOperations.FixedTimeEquals(presented, expected)
            ? SignatureVerdict.Verified
            : SignatureVerdict.Invalid;
    }

    /// <summary>Доверенный маркер для server-side контекста. Секрета и подписи в нём нет.</summary>
    public static JsonObject TransportMarker() => new()
    {
        ["signatureVerified"] = true,
        ["signatureVersion"] = SupportedVersion,
    };

    /// <summary>Подпись для тестов и диагностики: тот же алгоритм, что у adapter.</summary>
    public static string Sign(string secret, ReadOnlySpan<byte> body)
        => VersionPrefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static bool TryDecodeLowerHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var high = LowerHexValue(hex[index * 2]);
            var low = LowerHexValue(hex[index * 2 + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int LowerHexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        _ => -1,
    };
}
