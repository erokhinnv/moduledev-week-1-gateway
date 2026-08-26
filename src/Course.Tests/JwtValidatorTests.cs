using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Правильной подписи токена недостаточно. Эти тесты закрепляют правило: наличие, JSON-тип и
/// значение каждого контрактного claim'а проверяются отдельно, поэтому испорченный токен всегда
/// приводит к контролируемому ответу auth.invalid, а не к падению программы.
/// </summary>
public sealed class JwtValidatorTests
{
    private const string Secret = "course-test-signing-key-that-is-long-enough";

    private static readonly JwtOptions Options = new()
    {
        Issuer = "moduledev-course",
        Audience = "moduledev-api",
        SigningKey = Encoding.UTF8.GetBytes(Secret),
    };

    private static readonly JwtValidator Validator = new(Options);

    [Fact]
    public void AcceptsAContractToken()
    {
        var header = Sign(Claims());

        Assert.True(Validator.TryValidate(header, out var identity, out var reason), reason);
        Assert.NotNull(identity);
        Assert.Equal("candidate-client", identity!.Principal);
        Assert.Equal("web", identity.Consumer);
        Assert.Equal(["payment:write", "payment:read"], identity.Scopes);
    }

    [Fact]
    public void AcceptsAnAudienceArray()
    {
        // По стандарту JWT claim "aud" может быть как одиночной строкой, так и массивом строк —
        // проверяем, что валидатор корректно принимает оба варианта.
        var claims = Claims();
        claims["aud"] = new[] { "other-audience", "moduledev-api" };

        Assert.True(Validator.TryValidate(Sign(claims), out _, out var reason), reason);
    }

    [Theory]
    [InlineData("iss", 42)]                       // подпись верна, но JSON-тип claim'а неверный
    [InlineData("iss", "other-issuer")]
    [InlineData("aud", "other-audience")]
    [InlineData("sub", "")]
    [InlineData("consumer", 7)]
    [InlineData("scope", 1)]
    [InlineData("exp", "not-a-number")]
    [InlineData("iat", "not-a-number")]
    public void RejectsAMalformedClaim(string claim, object value)
    {
        var claims = Claims();
        claims[claim] = value;

        Assert.False(Validator.TryValidate(Sign(claims), out var identity, out _));
        Assert.Null(identity);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("consumer")]
    [InlineData("scope")]
    [InlineData("exp")]
    [InlineData("iat")]
    [InlineData("aud")]
    [InlineData("iss")]
    public void RejectsAMissingClaim(string claim)
    {
        var claims = Claims();
        claims.Remove(claim);

        Assert.False(Validator.TryValidate(Sign(claims), out _, out _));
    }

    [Fact]
    public void RejectsAnExpiredToken()
    {
        var claims = Claims();
        claims["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;
        claims["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;

        Assert.False(Validator.TryValidate(Sign(claims), out _, out _));
    }

    [Fact]
    public void RejectsAnotherSigningKey()
    {
        // Токен подписан ДРУГИМ секретом — подпись не совпадёт с ожидаемой, несмотря на то что
        // сама структура и содержимое claim'ов корректны.
        var foreign = Sign(Claims(), "a-completely-different-signing-key-value");

        Assert.False(Validator.TryValidate(foreign, out _, out _));
    }

    [Fact]
    public void RejectsTheNoneAlgorithm()
    {
        // Классическая уязвимость JWT: алгоритм "none" означает отсутствие подписи вообще.
        // Валидатор обязан отклонить такой токен ещё до попытки проверить подпись.
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(Claims()));

        Assert.False(Validator.TryValidate($"Bearer {header}.{payload}.", out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer not-a-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public void RejectsAMissingOrUnusableHeader(string? header)
    {
        Assert.False(Validator.TryValidate(header, out _, out _));
    }

    [Fact]
    public void RejectsAKeyShorterThanThirtyTwoBytes()
    {
        // Контракт требует ключ подписи HS256 не короче 32 байт — слишком короткий ключ должен
        // приводить к ошибке уже на этапе чтения настроек, до попытки проверить хоть один токен.
        Environment.SetEnvironmentVariable("COURSE_JWT_ISSUER", "moduledev-course");
        Environment.SetEnvironmentVariable("COURSE_JWT_AUDIENCE", "moduledev-api");
        Environment.SetEnvironmentVariable("COURSE_JWT_SIGNING_KEY", "too-short");

        Assert.Throws<InvalidOperationException>(JwtOptions.FromEnvironment);
    }

    /// <summary>Собирает набор корректных, контрактных claim'ов — отправную точку для каждого теста.</summary>
    private static Dictionary<string, object> Claims()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new Dictionary<string, object>
        {
            ["iss"] = "moduledev-course",
            ["aud"] = "moduledev-api",
            ["sub"] = "candidate-client",
            ["consumer"] = "web",
            ["scope"] = "payment:write payment:read",
            ["iat"] = now,
            ["exp"] = now + 3600,
        };
    }

    /// <summary>Собирает и подписывает тестовый JWT-токен по данным claim'ам, вручную (без сторонних библиотек), чтобы тест не зависел от той же реализации, что и код под тестом.</summary>
    private static string Sign(Dictionary<string, object> claims, string secret = Secret)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.ASCII.GetBytes($"{header}.{payload}"));
        return $"Bearer {header}.{payload}.{Encode(signature)}";
    }

    /// <summary>Кодирует байты в base64url (без символа "=" на конце, "+"→"-", "/"→"_") — формат, используемый в JWT.</summary>
    private static string Encode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
