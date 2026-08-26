using System.Text.Json.Nodes;
using Course.Cli;
using Course.Shared;
using Npgsql;

// Course CLI обязан вывести в stdout РОВНО ОДИН JSON-документ. Вся диагностика уходит только в
// stderr, а при любом контрактном сбое код завершения процесса (exit code) ненулевой.
return await CourseCli.RunAsync(args);

internal static class CourseCli
{
    private const string Usage = """
        course.sh migration apply <directory>
        course.sh action validate <manifest>
        course.sh action publish <manifest>
        course.sh action list
        course.sh action activate <module.action> --version <version>
        course.sh action disable <module.action> --version <version> [--replacement-version <version>]
        course.sh bootstrap
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        // Контракт разрешает вывести в stdout ровно один JSON-документ. Но библиотеки, которые
        // использует CLI (например Npgsql), в теории могут что-то напечатать в обычный
        // Console.Out по своим внутренним причинам. Чтобы гарантированно ничего постороннего не
        // просочилось на настоящий stdout, здесь он временно "прячется" (перенаправляется на
        // stderr), а настоящий stdout сохраняется в переменную stdout и используется только в
        // самом конце — ровно один раз, для итогового конверта ответа.
        var stdout = Console.Out;
        Console.SetOut(Console.Error);

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            var result = await DispatchAsync(args, lifetime.Token);
            Write(stdout, Envelope.CliSuccess(result));
            return 0;
        }
        catch (CliException exception)
        {
            // "Ожидаемая" контрактная ошибка — код и сообщение уже понятны и безопасны для вывода.
            Write(stdout, Envelope.CliError(exception.Code, exception.Message, exception.Details));
            return 1;
        }
        catch (Exception exception)
        {
            // Непредвиденная ошибка (например обрыв соединения с PostgreSQL). Классифицируем её
            // через тот же самый механизм, что используется в HTTP runtime (PostgresFailure),
            // чтобы CLI и API согласованно понимали одни и те же сбои.
            var code = PostgresFailure.Classify(exception);
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            Write(stdout, Envelope.CliError(
                code,
                code == ContractCodes.DependencyUnavailable
                    ? "authoritative state is unavailable"
                    : "command could not be completed"));
            return 1;
        }
    }

    /// <summary>Разбирает команду и аргументы, вызывает соответствующий обработчик.</summary>
    private static async Task<JsonObject> DispatchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.Error.WriteLine(Usage);
            return new JsonObject
            {
                ["resource"] = "cli",
                ["operation"] = "usage",
                ["commands"] = new JsonArray(Usage.Split('\n').Select(line => (JsonNode)line.Trim()).ToArray()),
            };
        }

        var line = CommandLine.Parse(args);
        var positional = line.Positional;
        if (positional.Count == 0)
        {
            throw new CliException(ContractCodes.RequestInvalid, "a contract command is required");
        }

        // Диспетчеризация по первым двум позиционным аргументам (например "action" + "publish").
        return (positional[0], positional.Count > 1 ? positional[1] : null) switch
        {
            ("bootstrap", _) => await BootstrapAsync(cancellationToken),
            ("migration", "apply") => await MigrationApplyAsync(positional, cancellationToken),
            ("action", "validate") => ActionValidate(positional),
            ("action", "publish") => await ActionPublishAsync(positional, cancellationToken),
            ("action", "list") => await ActionListAsync(cancellationToken),
            ("action", "activate") => await ActionActivateAsync(positional, line, cancellationToken),
            ("action", "disable") => await ActionDisableAsync(positional, line, cancellationToken),
            _ => throw new CliException(ContractCodes.RequestInvalid, $"unknown command: {string.Join(' ', positional)}"),
        };
    }

    /// <summary>Команда "migration apply &lt;directory&gt;": накатывает все .sql-файлы из каталога.</summary>
    private static async Task<JsonObject> MigrationApplyAsync(
        IReadOnlyList<string> positional,
        CancellationToken cancellationToken)
    {
        if (positional.Count < 3)
        {
            throw new CliException(ContractCodes.RequestInvalid, "migration apply requires a directory");
        }

        await using var dataSource = CreateDataSource();
        return await new MigrationRunner(dataSource).ApplyAsync(positional[2], cancellationToken);
    }

    /// <summary>Команда "action validate &lt;manifest&gt;": только проверяет манифест по схеме, ничего не меняет в базе.</summary>
    private static JsonObject ActionValidate(IReadOnlyList<string> positional)
    {
        if (positional.Count < 3)
        {
            throw new CliException(ContractCodes.RequestInvalid, "action validate requires a manifest path");
        }

        var manifest = ManifestReader.Read(positional[2]);

        // Проверка (validate) никогда ничего не записывает: она только доказывает, что манифест
        // соответствует машиночитаемому контракту, и не публикует его в каталог.
        return new JsonObject
        {
            ["resource"] = "action",
            ["operation"] = "validated",
            ["key"] = $"{manifest["module"]!.GetValue<string>()}.{manifest["action"]!.GetValue<string>()}",
            ["version"] = manifest["version"]!.GetValue<int>(),
        };
    }

    /// <summary>Команда "action publish &lt;manifest&gt;": публикует манифест в каталог действий.</summary>
    private static async Task<JsonObject> ActionPublishAsync(
        IReadOnlyList<string> positional,
        CancellationToken cancellationToken)
    {
        if (positional.Count < 3)
        {
            throw new CliException(ContractCodes.RequestInvalid, "action publish requires a manifest path");
        }

        var manifest = ManifestReader.Read(positional[2]);
        await using var dataSource = CreateDataSource();
        return await new CatalogClient(dataSource).PublishAsync(manifest, cancellationToken);
    }

    /// <summary>Команда "action list": выводит все опубликованные версии всех действий.</summary>
    private static async Task<JsonObject> ActionListAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = CreateDataSource();
        return await new CatalogClient(dataSource).ListAsync(cancellationToken);
    }

    /// <summary>Команда "action activate &lt;module.action&gt; --version &lt;version&gt;": делает указанную версию действия версией по умолчанию.</summary>
    private static async Task<JsonObject> ActionActivateAsync(
        IReadOnlyList<string> positional,
        CommandLine line,
        CancellationToken cancellationToken)
    {
        var (module, action) = ParseRouteKey(positional, "action activate");
        if (!line.TryInteger("--version", out var version) || version < 1)
        {
            throw new CliException(ContractCodes.RequestInvalid, "action activate requires --version <version>");
        }

        await using var dataSource = CreateDataSource();
        return await new CatalogClient(dataSource).ActivateAsync(module, action, version, cancellationToken);
    }

    /// <summary>Команда "action disable &lt;module.action&gt; --version &lt;version&gt; [--replacement-version &lt;version&gt;]": выключает указанную версию действия.</summary>
    private static async Task<JsonObject> ActionDisableAsync(
        IReadOnlyList<string> positional,
        CommandLine line,
        CancellationToken cancellationToken)
    {
        var (module, action) = ParseRouteKey(positional, "action disable");
        if (!line.TryInteger("--version", out var version) || version < 1)
        {
            throw new CliException(ContractCodes.RequestInvalid, "action disable requires --version <version>");
        }

        int? replacement = null;
        if (line.Has("--replacement-version"))
        {
            if (!line.TryInteger("--replacement-version", out var parsed) || parsed < 1)
            {
                throw new CliException(ContractCodes.RequestInvalid, "--replacement-version must be a positive integer");
            }

            replacement = parsed;
        }

        await using var dataSource = CreateDataSource();
        return await new CatalogClient(dataSource).DisableAsync(module, action, version, replacement, cancellationToken);
    }

    /// <summary>
    /// Команда "bootstrap" — путь автоматического старта всего стека Docker Compose: сначала
    /// накатываются базовые миграции (db/migrations), затем публикуются встроенные манифесты
    /// действий (db/actions). Благодаря этой команде чистый запуск "docker compose up -d --build"
    /// не требует НИКАКИХ ручных SQL-команд или ручной публикации действий — сервис cli делает
    /// всё это сам при старте контейнера.
    /// </summary>
    private static async Task<JsonObject> BootstrapAsync(CancellationToken cancellationToken)
    {
        var migrations = Env.Optional("COURSE_BOOTSTRAP_MIGRATIONS", "/app/db/migrations");
        var actions = Env.Optional("COURSE_BOOTSTRAP_ACTIONS", "/app/db/actions");

        await using var dataSource = CreateDataSource();
        await WaitForDatabaseAsync(dataSource, cancellationToken);

        var migration = await new MigrationRunner(dataSource).ApplyAsync(migrations, cancellationToken);

        var catalog = new CatalogClient(dataSource);
        var published = new JsonArray();
        var directory = new DirectoryInfo(actions);
        if (directory.Exists)
        {
            // Публикация манифестов тоже идёт в алфавитном порядке файлов — для предсказуемости.
            foreach (var file in directory.GetFiles("*.json").OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var manifest = ManifestReader.Read(file.FullName);
                published.Add(await catalog.PublishAsync(manifest, cancellationToken));
            }
        }

        return new JsonObject
        {
            ["resource"] = "runtime",
            ["operation"] = "bootstrapped",
            ["migration"] = migration,
            ["actions"] = published,
        };
    }

    /// <summary>
    /// Ждёт, пока PostgreSQL станет доступен, простым опросом раз в секунду. Нужно потому что
    /// сервис cli в Docker Compose стартует сразу после того, как контейнер postgres прошёл
    /// собственный healthcheck, но самому PostgreSQL может понадобиться ещё немного времени,
    /// прежде чем он реально начнёт принимать подключения.
    /// </summary>
    private static async Task WaitForDatabaseAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Env.OptionalInt("COURSE_BOOTSTRAP_WAIT_SECONDS", 90));
        while (true)
        {
            try
            {
                await using var command = dataSource.CreateCommand("SELECT 1");
                await command.ExecuteScalarAsync(cancellationToken);
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                Console.Error.WriteLine("waiting for postgres");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>Разбирает позиционный аргумент вида "module.action" на составные части.</summary>
    private static (string Module, string Action) ParseRouteKey(IReadOnlyList<string> positional, string command)
    {
        if (positional.Count < 3)
        {
            throw new CliException(ContractCodes.RequestInvalid, $"{command} requires <module.action>");
        }

        var parts = positional[2].Split('.');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new CliException(ContractCodes.RequestInvalid, "action key must be <module>.<action>");
        }

        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Создаёт подключение к базе от лица роли публикации (course_migrator) — заведомо не той
    /// роли, под которой работает HTTP runtime (course_runtime). Именно так на уровне
    /// инфраструктуры обеспечивается требование "runtime никогда не получает migration credentials".
    /// </summary>
    private static NpgsqlDataSource CreateDataSource()
        => CourseDatabase.CreateDataSource(
            connectTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_CONNECT_TIMEOUT", 5),
            commandTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_COMMAND_TIMEOUT", 120),
            maxPoolSize: 4);

    /// <summary>Печатает итоговый JSON-конверт в реальный stdout (переданный отдельно от подменённого Console.Out).</summary>
    private static void Write(TextWriter stdout, JsonNode envelope)
    {
        stdout.Write(envelope.ToJsonString());
        stdout.Write('\n');
        stdout.Flush();
    }
}
