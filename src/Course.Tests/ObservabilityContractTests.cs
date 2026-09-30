using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Course.Shared;
using Course.Workflow.Worker;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Контракт наблюдаемости недели 4: документ OpenMetrics, статус кода трассировки и барьер
/// подтверждения закрытого профиля.
/// </summary>
public sealed class OpenMetricsTests
{
    private static readonly Regex Sample = new(
        @"^(?<name>[A-Za-z_:][A-Za-z0-9_:]*)(?:\{(?<labels>[^}]*)\})?\s+(?<value>-?[0-9.]+)$",
        RegexOptions.Compiled);

    [Fact]
    public void DocumentEndsWithEofAndUsesLineFeedsOnly()
    {
        var document = new OpenMetrics().Gauge("queue_depth", 3, "Queue depth.").Build();

        Assert.EndsWith("# EOF\n", document, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', document);
    }

    [Fact]
    public void CounterFamilyIsDeclaredWithoutTheTotalSuffix()
    {
        // TYPE объявляет СЕМЕЙСТВО, а суффикс _total принадлежит значению. Объявить семейство
        // как "..._total" — самый обычный способ получить документ, который разбирается везде,
        // кроме валидатора.
        var document = new OpenMetrics().Counter("workflow_failures", 2, "Failures.").Build();

        Assert.Contains("# TYPE workflow_failures counter\n", document, StringComparison.Ordinal);
        Assert.Contains("\nworkflow_failures_total 2\n", document, StringComparison.Ordinal);
        Assert.DoesNotContain("# TYPE workflow_failures_total", document, StringComparison.Ordinal);
    }

    [Fact]
    public void GaugeSampleCarriesTheFamilyName()
    {
        var document = new OpenMetrics().Gauge("outbox_pending", 5, "Pending.").Build();

        Assert.Contains("# TYPE outbox_pending gauge\n", document, StringComparison.Ordinal);
        Assert.Contains("\noutbox_pending 5\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersUseADotSeparatorAndNoExponent()
    {
        // Сборка идёт в InvariantGlobalization, поэтому подменить культуру процесса здесь нельзя
        // и разделитель из локали в документ попасть не может. Проверяется наблюдаемое свойство,
        // которое обязано сохраниться и если инвариантный режим когда-нибудь выключат: точка как
        // разделитель и никакой экспоненциальной записи, которую валидатору читать незачем.
        var document = new OpenMetrics()
            .Gauge("outbox_oldest_age_seconds", 1.5, "Age.")
            .Gauge("workflow_job_oldest_age_seconds", 1234567, "Age.")
            .Gauge("outbox_pending", 0.0004, "Pending.")
            .Build();

        Assert.Contains("\noutbox_oldest_age_seconds 1.5\n", document, StringComparison.Ordinal);
        Assert.Contains("\nworkflow_job_oldest_age_seconds 1234567\n", document, StringComparison.Ordinal);
        Assert.Contains("\noutbox_pending 0\n", document, StringComparison.Ordinal);
        Assert.DoesNotContain("E+", document, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteValuesDoNotProduceAnInvalidSample(double value)
    {
        var document = new OpenMetrics().Gauge("workflow_jobs_ready", value, "Ready jobs.").Build();

        Assert.Contains("\nworkflow_jobs_ready 0\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerDocumentParsesAndCarriesNoLabels()
    {
        var metrics = new WorkerMetrics();
        metrics.JobClaimed();
        metrics.JobSettled(succeeded: false);

        var document = metrics.Render();
        Assert.EndsWith("# EOF\n", document, StringComparison.Ordinal);

        var labels = new List<string>();
        foreach (var line in document.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var match = Sample.Match(line);
            Assert.True(match.Success, $"invalid OpenMetrics sample: {line}");
            labels.AddRange(match.Groups["labels"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=')[0]));
        }

        // Ни одного label вообще: уникальный идентификатор не может попасть ни в имя, ни в
        // значение метрики даже по недосмотру.
        Assert.Empty(labels);
        Assert.Contains("\nworkflow_worker_attempts_failed_total 1\n", document, StringComparison.Ordinal);
        Assert.Contains("\nworkflow_worker_leased_jobs 0\n", document, StringComparison.Ordinal);
    }
}

/// <summary>
/// Журнал: одна строка — один JSON object, идентификаторы полями, исключение только классом.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class StructuredLogTests
{
    [Fact]
    public void RecordIsOneJsonObjectOnOneLine()
    {
        var captured = Capture(() => StructuredLog.Info(
            "workflow-worker",
            "attempt.succeeded",
            ("jobId", Guid.Parse("11111111-1111-4111-8111-111111111111")),
            ("attemptNumber", 2),
            ("retryable", false),
            ("outcome", null)));

        var lines = captured.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        var record = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("workflow-worker", record["service"]!.GetValue<string>());
        Assert.Equal("info", record["level"]!.GetValue<string>());
        Assert.Equal("attempt.succeeded", record["event"]!.GetValue<string>());
        // Идентификатор — отдельное поле, а не часть текста: иначе путь попытки пришлось бы
        // искать разбором строки.
        Assert.Equal("11111111-1111-4111-8111-111111111111", record["jobId"]!.GetValue<string>());
        Assert.Equal(2, record["attemptNumber"]!.GetValue<int>());
        Assert.False(record["retryable"]!.GetValue<bool>());
        Assert.Null(record["outcome"]);

        // Метка времени — RFC 3339 UTC, а не локальное время машины, где собран журнал.
        var timestamp = record["ts"]!.GetValue<string>();
        Assert.EndsWith("Z", timestamp, StringComparison.Ordinal);
        Assert.Equal(
            TimeSpan.Zero,
            DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Offset);
    }

    [Fact]
    public void LoggerPlaceholdersBecomeFieldsAndTheTemplateDoesNot()
    {
        var captured = Capture(() =>
        {
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.AddProvider(new JsonConsoleLoggerProvider("api"));
            });

            factory.CreateLogger("Course.Api.ActionRuntime").LogInformation(
                "action completed correlationId={CorrelationId} route={Module}.{Action} outcome={Outcome}",
                Guid.Empty,
                "payment",
                "request",
                "APPLIED");
        });

        var record = JsonNode.Parse(captured.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0])!.AsObject();
        Assert.Equal("api", record["service"]!.GetValue<string>());
        Assert.Equal("Course.Api.ActionRuntime", record["event"]!.GetValue<string>());
        Assert.Equal(Guid.Empty.ToString(), record["correlationId"]!.GetValue<string>());
        Assert.Equal("payment", record["module"]!.GetValue<string>());
        Assert.Equal("APPLIED", record["outcome"]!.GetValue<string>());
        // Шаблон дублировал бы уже отформатированное сообщение.
        Assert.DoesNotContain("OriginalFormat", captured, StringComparison.Ordinal);
        Assert.Contains("action completed", record["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionIsReducedToItsClassification()
    {
        var captured = Capture(() =>
        {
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.AddProvider(new JsonConsoleLoggerProvider("api"));
            });

            factory.CreateLogger("probe").LogError(
                new InvalidOperationException("duplicate key value violates unique constraint (1000.00)"),
                "action failed");
        });

        var record = JsonNode.Parse(captured.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0])!.AsObject();
        Assert.Equal("InvalidOperationException", record["failure"]!.GetValue<string>());
        // Сообщение исключения PostgreSQL умеет содержать значения данных, то есть фрагмент
        // полезной нагрузки. В журнал уходит только класс сбоя.
        Assert.DoesNotContain("1000.00", captured, StringComparison.Ordinal);
    }

    private static string Capture(Action action)
    {
        var original = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return captured.ToString();
    }
}

/// <summary>Статусы кодов, добавленных неделей 4.</summary>
public sealed class DiagnosticsCodeTests
{
    [Fact]
    public void UnknownTraceIdentifierIsNotFound()
        => Assert.Equal(404, ContractCodes.HttpStatus(ContractCodes.DiagnosticsTraceNotFound));

    [Fact]
    public void AnUnlistedDomainCodeStaysUnprocessableEntity()
        => Assert.Equal(422, ContractCodes.HttpStatus("operation.not_found"));
}

/// <summary>
/// Барьер подтверждения. Проверяется ровно то, чем он ценен: вне тестового профиля его нет, а в
/// профиле он печатает одну строку заданной формы и НЕ возвращает управление.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class FailpointTests
{
    [Fact]
    public void ProductionProfileIgnoresAConfiguredFailpoint()
    {
        using var environment = new ScopedEnvironment(
            ("COURSE_TEST_PROFILE", "0"),
            ("COURSE_FAILPOINT", "after_job_claim"),
            ("COURSE_WORKER_ID", "worker-a"));

        var failpoint = Failpoint.FromEnvironment("COURSE_WORKER_ID");

        Assert.Equal(string.Empty, failpoint.Name);
        Assert.False(failpoint.Matches("after_job_claim"));
    }

    [Fact]
    public void TestProfileWithoutAFailpointStaysDisabled()
    {
        using var environment = new ScopedEnvironment(
            ("COURSE_TEST_PROFILE", "1"),
            ("COURSE_FAILPOINT", "   "),
            ("COURSE_WORKER_ID", "worker-a"));

        Assert.Equal(string.Empty, Failpoint.FromEnvironment("COURSE_WORKER_ID").Name);
    }

    [Fact]
    public void TestProfileMatchesOnlyTheConfiguredPoint()
    {
        using var environment = new ScopedEnvironment(
            ("COURSE_TEST_PROFILE", "1"),
            ("COURSE_FAILPOINT", "after_inbox_saved"),
            ("COURSE_APPLICATION_NAME", "course-api"));

        var failpoint = Failpoint.FromEnvironment("COURSE_APPLICATION_NAME");

        Assert.True(failpoint.Matches("after_inbox_saved"));
        Assert.False(failpoint.Matches("after_manual_decision"));
        Assert.Equal("course-api", failpoint.InstanceId);
    }

    [Fact]
    public async Task ReachingAPointAcknowledgesOnceAndNeverReturns()
    {
        using var environment = new ScopedEnvironment(
            ("COURSE_TEST_PROFILE", "1"),
            ("COURSE_FAILPOINT", "after_manual_decision"),
            ("COURSE_APPLICATION_NAME", "course-api"));

        var failpoint = Failpoint.FromEnvironment("COURSE_APPLICATION_NAME");
        var original = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);

            // Управление не возвращается сознательно: граница ЗА failpoint не имеет права быть
            // пройденной остановленным компонентом. Поэтому задача только запускается.
            var blocked = failpoint.ReachAsync("after_manual_decision");
            var acknowledged = await WaitForLineAsync(captured);

            Assert.False(blocked.IsCompleted);
            Assert.Equal(
                """{"event":"failpoint.reached","name":"after_manual_decision","instanceId":"course-api"}""",
                acknowledged);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task ActionFailpointsStopOnlyTheDeclaredActionAndPhase()
    {
        using var environment = new ScopedEnvironment(
            ("COURSE_TEST_PROFILE", "1"),
            ("COURSE_FAILPOINT", "after_inbox_saved"),
            ("COURSE_APPLICATION_NAME", "course-api"));

        var failpoints = new Course.Api.ActionFailpoints(Failpoint.FromEnvironment("COURSE_APPLICATION_NAME"));

        // Посторонние действия и другая граница проходят насквозь: остановка на первой же
        // встреченной границе поймала бы чужой вызов и доказала бы не тот инвариант.
        await failpoints.StopIfReachedAsync("payment", "request", Course.Api.ActionPhase.AfterCommit);
        await failpoints.StopIfReachedAsync("receipt", "accept", Course.Api.ActionPhase.BeforeCommit);

        var original = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            var blocked = failpoints.StopIfReachedAsync("receipt", "accept", Course.Api.ActionPhase.AfterCommit);
            var acknowledged = await WaitForLineAsync(captured);

            Assert.False(blocked.IsCompleted);
            Assert.Contains("\"name\":\"after_inbox_saved\"", acknowledged, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static async Task<string> WaitForLineAsync(StringWriter captured)
    {
        // Ищется именно строка подтверждения, а не первая попавшаяся: Console.Out подменяется
        // глобально, и посторонний вывод не должен выдавать себя за подтверждение.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var line = captured.ToString()
                .Split('\n')
                .Select(value => value.TrimEnd('\r'))
                .FirstOrDefault(value => value.Contains("failpoint.reached", StringComparison.Ordinal));
            if (line is not null)
            {
                return line;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no failpoint acknowledgement was printed");
    }
}

/// <summary>
/// Общая коллекция тестов, подменяющих Console.Out.
///
/// Подмена глобальна для процесса, а xunit по умолчанию выполняет классы параллельно: без общей
/// коллекции запись одного теста попадала бы в буфер другого, и «одна запись — одна строка»
/// падало бы через раз, не имея отношения к проверяемому свойству.
/// </summary>
[CollectionDefinition(ConsoleCapture.Name, DisableParallelization = true)]
public sealed class ConsoleCapture
{
    public const string Name = "console-out";
}

/// <summary>Временная подмена переменных окружения на время одного теста.</summary>
internal sealed class ScopedEnvironment : IDisposable
{
    private readonly (string Name, string? Previous)[] _restore;

    public ScopedEnvironment(params (string Name, string Value)[] values)
    {
        _restore = values
            .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
            .ToArray();

        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, previous) in _restore)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
