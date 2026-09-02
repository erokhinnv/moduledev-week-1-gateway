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
        course.sh flow validate <map>
        course.sh flow publish <map>
        course.sh flow list
        course.sh flow activate <flow> --version <version>
        course.sh flow start <flow> --business-key <key> [--data <file>]
        course.sh flow get <process-id>
        course.sh flow signal <process-id> --type <type> --message-id <id> --payload <file>
        course.sh flow test-finish <job-id> --owner <owner> --lease-version <version> --outcome <outcome> --result <file>
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
            ("flow", "validate") => await FlowValidateAsync(positional, cancellationToken),
            ("flow", "publish") => await FlowPublishAsync(positional, cancellationToken),
            ("flow", "list") => await FlowListAsync(cancellationToken),
            ("flow", "activate") => await FlowActivateAsync(positional, line, cancellationToken),
            ("flow", "start") => await FlowStartAsync(positional, line, cancellationToken),
            ("flow", "get") => await FlowGetAsync(positional, cancellationToken),
            ("flow", "signal") => await FlowSignalAsync(positional, line, cancellationToken),
            ("flow", "test-finish") => await FlowTestFinishAsync(positional, line, cancellationToken),
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

        await using var dataSource = CreateMigrationDataSource();
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
        await using var dataSource = CreatePublicationDataSource();
        return await new CatalogClient(dataSource).PublishAsync(manifest, cancellationToken);
    }

    /// <summary>Команда "action list": выводит все опубликованные версии всех действий.</summary>
    private static async Task<JsonObject> ActionListAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = CreatePublicationDataSource();
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

        await using var dataSource = CreatePublicationDataSource();
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

        await using var dataSource = CreatePublicationDataSource();
        return await new CatalogClient(dataSource).DisableAsync(module, action, version, replacement, cancellationToken);
    }

    // -------------------------------------------------------------------------------------
    // Команды карт процессов.
    //
    // Ни одна из них не содержит знания о конкретной карте: validate доказывает контракт,
    // publish сохраняет неизменяемую версию, activate выбирает версию для новых экземпляров,
    // а start/get/signal работают с уже закреплённой версией конкретного процесса.
    // -------------------------------------------------------------------------------------

    /// <summary>Команда "flow validate &lt;map&gt;": доказывает контракт карты, ничего не записывая.</summary>
    private static async Task<JsonObject> FlowValidateAsync(
        IReadOnlyList<string> positional,
        CancellationToken cancellationToken)
    {
        var map = ReadMapArgument(positional, "flow validate");
        await using var dataSource = CreatePublicationDataSource();
        await ValidateSemanticsAsync(new FlowClient(dataSource), map, cancellationToken);

        return new JsonObject
        {
            ["resource"] = "flow",
            ["operation"] = "validated",
            ["flowName"] = map.FlowName,
            ["flowVersion"] = map.Version,
        };
    }

    /// <summary>Команда "flow publish &lt;map&gt;": публикует неизменяемую версию карты (без активации).</summary>
    private static async Task<JsonObject> FlowPublishAsync(
        IReadOnlyList<string> positional,
        CancellationToken cancellationToken)
    {
        var map = ReadMapArgument(positional, "flow publish");
        await using var dataSource = CreatePublicationDataSource();
        var client = new FlowClient(dataSource);

        // Публикация невалидной карты обязана не оставлять следов, поэтому проверка идёт до
        // единственного записывающего вызова, а не внутри него.
        await ValidateSemanticsAsync(client, map, cancellationToken);
        return await client.PublishAsync(map.Document, cancellationToken);
    }

    private static async Task<JsonObject> FlowListAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = CreatePublicationDataSource();
        return await new FlowClient(dataSource).ListAsync(cancellationToken);
    }

    private static async Task<JsonObject> FlowActivateAsync(
        IReadOnlyList<string> positional,
        CommandLine line,
        CancellationToken cancellationToken)
    {
        var flowName = RequirePositional(positional, "flow activate", "<flow>");
        if (!line.TryInteger("--version", out var version) || version < 1)
        {
            throw new CliException(ContractCodes.RequestInvalid, "flow activate requires --version <version>");
        }

        await using var dataSource = CreatePublicationDataSource();
        return await new FlowClient(dataSource).ActivateAsync(flowName, version, cancellationToken);
    }

    private static async Task<JsonObject> FlowStartAsync(
        IReadOnlyList<string> positional,
        CommandLine line,
        CancellationToken cancellationToken)
    {
        var flowName = RequirePositional(positional, "flow start", "<flow>");
        var businessKey = line.Value("--business-key");
        if (string.IsNullOrWhiteSpace(businessKey))
        {
            throw new CliException(ContractCodes.RequestInvalid, "flow start requires --business-key <key>");
        }

        // Без --data процесс стартует с пустым объектом данных.
        var data = line.Value("--data") is { } dataPath
            ? FlowReader.ReadJsonObject(dataPath, "process data")
            : new JsonObject();

        await using var dataSource = CreateOperationDataSource();
        var result = await new FlowClient(dataSource).StartAsync(flowName, businessKey, data, cancellationToken);
        result["operation"] = "started";
        return result;
    }

    private static async Task<JsonObject> FlowGetAsync(
        IReadOnlyList<string> positional,
        CancellationToken cancellationToken)
    {
        var processId = ParseUuid(RequirePositional(positional, "flow get", "<process-id>"), "process id");
        await using var dataSource = CreateOperationDataSource();
        return await new FlowClient(dataSource).GetAsync(processId, cancellationToken);
    }

    private static async Task<JsonObject> FlowSignalAsync(
        IReadOnlyList<string> positional,
        CommandLine line,
        CancellationToken cancellationToken)
    {
        var processId = ParseUuid(RequirePositional(positional, "flow signal", "<process-id>"), "process id");
        var signalType = line.Value("--type");
        var messageId = line.Value("--message-id");
        var payloadPath = line.Value("--payload");

        if (string.IsNullOrWhiteSpace(signalType) ||
            string.IsNullOrWhiteSpace(messageId) ||
            string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new CliException(
                ContractCodes.RequestInvalid,
                "flow signal requires --type, --message-id and --payload");
        }

        var payload = FlowReader.ReadJsonObject(payloadPath, "signal payload");
        await using var dataSource = CreateOperationDataSource();
        return await new FlowClient(dataSource)
            .SignalAsync(processId, signalType, messageId, payload, cancellationToken);
    }

    /// <summary>
    /// Команда "flow test-finish": проба fencing. Доступна только в test profile и вызывает ту же
    /// production-границу завершения, что и worker — иначе проверялась бы отдельная ветка кода,
    /// а не настоящий инвариант.
    /// </summary>
    private static async Task<JsonObject> FlowTestFinishAsync(
        IReadOnlyList<string> positional,
        CommandLine line,
        CancellationToken cancellationToken)
    {
        if (Env.Optional("COURSE_TEST_PROFILE", "0") != "1")
        {
            throw new CliException(
                ContractCodes.RequestInvalid,
                "flow test-finish is only available under COURSE_TEST_PROFILE=1");
        }

        var jobId = ParseUuid(RequirePositional(positional, "flow test-finish", "<job-id>"), "job id");
        var owner = line.Value("--owner");
        var outcome = line.Value("--outcome");
        var resultPath = line.Value("--result");

        if (string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(outcome) ||
            string.IsNullOrWhiteSpace(resultPath) ||
            !long.TryParse(line.Value("--lease-version"), out var leaseVersion))
        {
            throw new CliException(
                ContractCodes.RequestInvalid,
                "flow test-finish requires --owner, --lease-version, --outcome and --result");
        }

        var result = FlowReader.ReadJsonObject(resultPath, "task result");
        await using var dataSource = CreateOperationDataSource();
        return await new FlowClient(dataSource)
            .TestFinishAsync(jobId, owner, leaseVersion, outcome, result, cancellationToken);
    }

    /// <summary>
    /// Семантическая проверка карты по авторитетному состоянию каталога и правам исполнителя.
    /// Схема доказывает форму документа, а смысл — только эта проверка.
    /// </summary>
    private static async Task ValidateSemanticsAsync(
        FlowClient client,
        WorkflowMap map,
        CancellationToken cancellationToken)
    {
        var facts = WorkflowPublicationFacts.FromResult(
            await client.PublicationFactsAsync(map.Document, cancellationToken));

        var violations = WorkflowMapValidator.Validate(map, facts);
        if (violations.Count == 0)
        {
            return;
        }

        throw new CliException(
            ContractCodes.WorkflowMapInvalid,
            "workflow map does not satisfy the course-1 semantic contract",
            new JsonObject
            {
                ["violations"] = new JsonArray(violations.Take(20)
                    .Select(item => (JsonNode)item)
                    .ToArray()),
            });
    }

    private static WorkflowMap ReadMapArgument(IReadOnlyList<string> positional, string command)
        => FlowReader.ReadMap(RequirePositional(positional, command, "<map>"));

    private static string RequirePositional(IReadOnlyList<string> positional, string command, string what)
    {
        if (positional.Count < 3 || string.IsNullOrWhiteSpace(positional[2]))
        {
            throw new CliException(ContractCodes.RequestInvalid, $"{command} requires {what}");
        }

        return positional[2];
    }

    private static Guid ParseUuid(string value, string what)
        => Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new CliException(ContractCodes.RequestInvalid, $"{what} must be a UUID");

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

        // Bootstrap — единственное место, где нужны оба набора учётных данных сразу: структуру
        // базы накатывает роль миграций, а встроенные манифесты публикует роль публикации.
        await using var migrationSource = CreateMigrationDataSource();
        await WaitForDatabaseAsync(migrationSource, cancellationToken);

        var migration = await new MigrationRunner(migrationSource).ApplyAsync(migrations, cancellationToken);

        // Профиль исполнения записывается в саму базу: проба fencing обходит арендатора, и
        // решать, разрешена ли она, обязана база, а не переменная окружения клиента.
        await SetTestProfileAsync(migrationSource, cancellationToken);

        await using var publicationSource = CreatePublicationDataSource();
        var catalog = new CatalogClient(publicationSource);
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

        // Встроенные карты процессов публикуются после действий: карта ссылается на версию
        // действия, и валидатор обязан увидеть её уже опубликованной.
        var flows = await BootstrapFlowsAsync(catalogSource: publicationSource, cancellationToken);

        return new JsonObject
        {
            ["resource"] = "runtime",
            ["operation"] = "bootstrapped",
            ["migration"] = migration,
            ["actions"] = published,
            ["flows"] = flows,
        };
    }

    /// <summary>
    /// Публикует встроенные карты и активирует младшую версию каждой карты, у которой активной
    /// версии ещё нет. Повторный bootstrap не переключает выбор, сделанный оператором вручную.
    /// </summary>
    private static async Task<JsonArray> BootstrapFlowsAsync(
        NpgsqlDataSource catalogSource,
        CancellationToken cancellationToken)
    {
        var published = new JsonArray();
        var directory = new DirectoryInfo(Env.Optional("COURSE_BOOTSTRAP_FLOWS", "/app/db/flows"));
        if (!directory.Exists)
        {
            return published;
        }

        var client = new FlowClient(catalogSource);
        var files = directory.GetFiles("*.json")
            .Concat(directory.GetFiles("*.yaml"))
            .Concat(directory.GetFiles("*.yml"))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            var map = FlowReader.ReadMap(file.FullName);
            await ValidateSemanticsAsync(client, map, cancellationToken);
            published.Add(await client.PublishAsync(map.Document, cancellationToken));
        }

        var listed = await client.ListAsync(cancellationToken);
        var items = listed["items"] as JsonArray ?? [];
        var byFlow = items
            .OfType<JsonObject>()
            .GroupBy(item => item["flowName"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal);

        var activated = new JsonArray();
        foreach (var group in byFlow)
        {
            if (group.Any(item => item["isActive"]?.GetValue<bool>() == true))
            {
                continue;
            }

            var version = group
                .Select(item => item["flowVersion"]?.GetValue<int>() ?? int.MaxValue)
                .Min();
            activated.Add(await client.ActivateAsync(group.Key, version, cancellationToken));
        }

        foreach (var item in activated)
        {
            published.Add(item?.DeepClone());
        }

        return published;
    }

    /// <summary>
    /// Записывает признак test profile в авторитетное состояние. Выполняется ролью миграций:
    /// это установочная настройка контура того же класса, что и сама схема, а не операция
    /// публикации или исполнения.
    /// </summary>
    private static async Task SetTestProfileAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT workflow.set_test_profile($1)");
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Boolean,
            Value = Env.Optional("COURSE_TEST_PROFILE", "0") == "1",
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
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
    /// Подключение от лица роли МИГРАЦИЙ (course_migrator). Она член course_owner, потому что DDL
    /// требует прав владельца, и именно поэтому её учётные данные нужны как можно реже — только
    /// команде migration apply. Роль runtime (course_runtime) их не получает никогда.
    /// </summary>
    private static NpgsqlDataSource CreateMigrationDataSource()
        => CourseDatabase.CreateDataSource(
            connectTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_CONNECT_TIMEOUT", 5),
            commandTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_COMMAND_TIMEOUT", 120),
            maxPoolSize: 4);

    /// <summary>
    /// Подключение от лица роли ПУБЛИКАЦИИ (course_publisher). Ей выданы EXECUTE ровно на пять
    /// функций управления каталогом — ни DDL, ни доступа к предметным таблицам, ни api.invoke.
    /// Если переменные публикации не заданы, используется основной набор: контур, развёрнутый до
    /// разделения ролей, продолжает работать, просто без выигрыша от разделения.
    /// </summary>
    private static NpgsqlDataSource CreatePublicationDataSource()
        => CourseDatabase.CreateDataSource(
            connectTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_CONNECT_TIMEOUT", 5),
            commandTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_COMMAND_TIMEOUT", 120),
            maxPoolSize: 4,
            credentials: CourseDatabase.Credentials.PublicationOrDefault());

    /// <summary>
    /// Подключение от лица роли ОПЕРАЦИЙ (course_operator). Ею идут команды, двигающие процессы:
    /// start, get, signal и проба fencing. Публиковать карты или действия эта роль не может, а
    /// публикатор не может двигать процессы — ни один набор учётных данных не даёт сразу и
    /// определение, и исполнение.
    /// </summary>
    private static NpgsqlDataSource CreateOperationDataSource()
        => CourseDatabase.CreateDataSource(
            connectTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_CONNECT_TIMEOUT", 5),
            commandTimeoutSeconds: Env.OptionalInt("COURSE_POSTGRES_COMMAND_TIMEOUT", 120),
            maxPoolSize: 4,
            credentials: CourseDatabase.Credentials.Operation);

    /// <summary>Печатает итоговый JSON-конверт в реальный stdout (переданный отдельно от подменённого Console.Out).</summary>
    private static void Write(TextWriter stdout, JsonNode envelope)
    {
        stdout.Write(envelope.ToJsonString());
        stdout.Write('\n');
        stdout.Flush();
    }
}
