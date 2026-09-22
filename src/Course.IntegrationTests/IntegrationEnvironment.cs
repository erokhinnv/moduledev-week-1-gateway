using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Course.IntegrationTests;

/// <summary>
/// Общее окружение интеграционных тестов: живой контур, поднятый через Docker Compose.
///
/// Эти тесты закрывают то, что принципиально нельзя проверить юнит-тестами: гонки версий,
/// конкурентную публикацию, повтор через смену версии и границы прав ролей. Все они требуют
/// работающего стека, поэтому по умолчанию ПРОПУСКАЮТСЯ — чтобы обычный "dotnet test" без Docker
/// оставался зелёным. Запуск: см. scripts/integration-tests.sh или переменные ниже.
/// </summary>
public static class IntegrationEnvironment
{
    /// <summary>Адрес публичного шлюза. Заодно служит признаком "контур поднят, тесты выполнять".</summary>
    public static string? BaseUrl => Value("COURSE_INTEGRATION_BASE_URL");

    /// <summary>Имя проекта Compose, через который тесты обращаются к psql и к командам cli.</summary>
    public static string ComposeProject => Value("COURSE_INTEGRATION_COMPOSE_PROJECT") ?? "moduledev-week-1";

    /// <summary>Ключ подписи JWT того контура, против которого идут тесты.</summary>
    public static string SigningKey =>
        Value("COURSE_JWT_SIGNING_KEY") ?? "local-development-signing-key-change-me-please";

    public static bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Причина пропуска для атрибутов xUnit; null означает "выполнять".</summary>
    public static string? SkipReason => Enabled
        ? null
        : "integration tests need a running stack: set COURSE_INTEGRATION_BASE_URL";

    private static string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static HttpClient CreateClient() => new()
    {
        BaseAddress = new Uri(BaseUrl!),
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Выпускает контрактный JWT HS256 тем же ключом, что настроен у сервиса api. Токен строится
    /// вручную, без библиотек, чтобы тест не зависел от той же реализации, что и код под тестом.
    /// </summary>
    public static string Token(string subject, string consumer, params string[] scopes)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "moduledev-course",
            ["aud"] = "moduledev-api",
            ["sub"] = subject,
            ["consumer"] = consumer,
            ["scope"] = string.Join(' ', scopes),
            ["iat"] = now,
            ["exp"] = now + 3600,
        }));

        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningKey),
            Encoding.ASCII.GetBytes($"{header}.{payload}"));

        return $"{header}.{payload}.{Encode(signature)}";
    }

    private static string Encode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Выполняет SQL в контейнере postgres. Прямое подключение невозможно принципиально: у сервиса
    /// postgres нет опубликованного host-порта, и это часть контракта, а не недосмотр. Поэтому
    /// проверки на уровне базы идут тем же путём, что и у самой автопроверки, — через psql внутри
    /// контейнера.
    /// </summary>
    public static (int ExitCode, string Output) Psql(string sql, string? role = null)
    {
        var statement = role is null ? sql : $"SET ROLE {role}; {sql}";
        return Compose("exec", "-T", "postgres", "psql", "-X", "-A", "-t",
            "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "course", "-c", statement);
    }

    /// <summary>Каталог с фикстурами тестов, скопированный рядом со сборкой.</summary>
    public static string FixturesDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>
    /// Запускает одну команду course cli в одноразовом контейнере и возвращает её JSON-конверт.
    /// При указании <paramref name="mount"/> каталог хоста монтируется внутрь как /itest:ro —
    /// тем же приёмом, каким автопроверка подкладывает cli свои доверенные фикстуры.
    /// </summary>
    public static (int ExitCode, JsonNode? Envelope) Cli(string? mount, params string[] arguments)
    {
        var call = new List<string> { "run", "--rm", "-T", "--no-deps" };
        if (mount is not null)
        {
            call.Add("-v");
            call.Add($"{mount}:/itest:ro");
        }

        call.Add("cli");
        call.AddRange(arguments);
        var (exitCode, output) = Compose([.. call]);

        JsonNode? envelope = null;
        try
        {
            envelope = string.IsNullOrWhiteSpace(output) ? null : JsonNode.Parse(output.Trim());
        }
        catch (JsonException)
        {
            // Невалидный JSON — это сам по себе результат теста, вернём его как отсутствие конверта.
        }

        return (exitCode, envelope);
    }

    /// <summary>
    /// Запускает cli с подменёнными переменными окружения и возвращает ОБА потока раздельно.
    ///
    /// Раздельно — потому что весь смысл проверки в границе между ними: в stdout уходит конверт,
    /// в stderr диагностика, и утечка возможна именно во втором, когда первый выглядит безупречно.
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) CliDiagnostics(
        IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        var call = new List<string> { "run", "--rm", "-T", "--no-deps" };
        foreach (var pair in environment)
        {
            call.Add("-e");
            call.Add($"{pair.Key}={pair.Value}");
        }

        call.Add("cli");
        call.AddRange(arguments);
        return ComposeStreams([.. call]);
    }

    /// <summary>
    /// Произвольная команда docker compose над контуром тестов: остановка и запуск сервисов.
    ///
    /// Нужна там, где проверяется поведение при ОТСУТСТВИИ исполнителя: аварию между действием и
    /// подтверждением нельзя воспроизвести, пока рядом работают штатные worker'ы — они заберут
    /// задание первыми, и тест будет доказывать не то, ради чего написан.
    /// </summary>
    public static (int ExitCode, string Output) Service(params string[] arguments)
        => Compose(arguments);

    /// <summary>
    /// Запускает одноразовый worker с включённым failpoint и возвращает идентификатор контейнера.
    ///
    /// Профиль и точка аварии задаются окружением этого экземпляра, а не контура: штатные
    /// worker'ы остаются обычными, и тест не меняет условия для соседних проверок.
    /// </summary>
    public static string StartFailpointWorker(string instanceId, string failpoint)
    {
        var (exitCode, output) = Compose(
            "run", "--detach", "--no-deps",
            "-e", "COURSE_TEST_PROFILE=1",
            "-e", $"COURSE_FAILPOINT={failpoint}",
            "-e", $"COURSE_WORKER_ID={instanceId}",
            "worker-a");

        var container = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (exitCode != 0 || string.IsNullOrWhiteSpace(container))
        {
            throw new InvalidOperationException($"failpoint worker could not be started: {output}");
        }

        return container.Trim();
    }

    /// <summary>
    /// Поднимает отдельный шлюз с заданной границей ответа и возвращает контейнер и порт хоста.
    ///
    /// Отдельный, а не штатный: граница задаётся окружением при старте, и менять её у общего шлюза
    /// значило бы менять условия для всех соседних тестов. Порт выбирает Docker — жёстко заданный
    /// порт рано или поздно столкнулся бы с чужим.
    /// </summary>
    public static (string Container, int Port) StartGatewayWithResponseLimit(long limitBytes)
    {
        var (exitCode, output) = Compose(
            "run", "--detach", "--no-deps",
            "-p", "0:8080",
            "-e", $"COURSE_GATEWAY_MAX_RESPONSE_BYTES={limitBytes}",
            "gateway");

        var container = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (exitCode != 0 || string.IsNullOrWhiteSpace(container))
        {
            throw new InvalidOperationException($"limited gateway could not be started: {output}");
        }

        // «0.0.0.0:49153» — берётся номер порта после последнего двоеточия.
        var mapping = Docker("port", container, "8080").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Contains(':'));

        if (mapping is null || !int.TryParse(mapping[(mapping.LastIndexOf(':') + 1)..], out var port))
        {
            KillContainer(container);
            throw new InvalidOperationException($"limited gateway exposes no host port: {mapping}");
        }

        return (container, port);
    }

    /// <summary>Журнал контейнера: по нему видно структурное подтверждение достигнутого failpoint.</summary>
    public static string ContainerLog(string container) => Docker("logs", container).Output;

    /// <summary>
    /// Жёсткая остановка контейнера. Именно жёсткая: штатное завершение дало бы worker'у довести
    /// попытку до конца, а воспроизводится гибель посреди неё.
    /// </summary>
    public static void KillContainer(string container)
    {
        Docker("kill", container);
        Docker("rm", "--force", container);
    }

    private static (int ExitCode, string Output) Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("docker could not be started");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, output + error);
    }

    private static (int ExitCode, string Output) Compose(params string[] arguments)
    {
        var (exitCode, stdout, _) = ComposeStreams(arguments);
        return (exitCode, stdout);
    }

    private static (int ExitCode, string Stdout, string Stderr) ComposeStreams(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("compose");
        start.ArgumentList.Add("--project-name");
        start.ArgumentList.Add(ComposeProject);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("docker compose could not be started");

        var output = process.StandardOutput.ReadToEnd();
        var diagnostics = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, output, diagnostics);
    }
}
