using System.Text.Json.Nodes;
using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Проверка контракта карты процесса на ОПУБЛИКОВАННЫХ fixtures открытой проверки.
///
/// Это не тест «на свои же данные»: карты берутся из каталога проверяющего, поэтому тест
/// доказывает ровно то, что потребуется на сдаче — валидные карты принимаются, а каждая
/// заведомо испорченная отклоняется, причём отклоняется до всякой записи.
/// </summary>
public sealed class WorkflowMapTests
{
    /// <summary>
    /// Факты каталога, соответствующие манифестам fixtures: версия 1 включена, версия 2
    /// опубликована, но выключена.
    /// </summary>
    private static readonly WorkflowPublicationFacts Facts = new(
        ["workflow:execute", "workflow:read"],
        [
            new WorkflowActionFact(
                "module_5abe942c84",
                "action_5cd90325f0",
                1,
                Found: true,
                Enabled: true,
                RequiredPolicy: ["workflow:execute"],
                Outcomes: ["ROUTE_SIGNAL_72125C4E", "ROUTE_MANUAL_503E0EA7"],
                IdempotencyMode: "required",
                IdempotencyScope: "principal_action"),
            new WorkflowActionFact(
                "module_5abe942c84",
                "action_5cd90325f0",
                2,
                Found: true,
                Enabled: false,
                RequiredPolicy: ["workflow:execute"],
                Outcomes: ["ROUTE_SIGNAL_72125C4E", "ROUTE_MANUAL_503E0EA7"],
                IdempotencyMode: "required",
                IdempotencyScope: "principal_action"),
        ]);

    private static string FixtureRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "check.sh")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "autocheck", "fixtures", "maps");
        }
    }

    [Theory]
    [InlineData("3c363d117b40-v1.flow.json")]
    [InlineData("3c363d117b40-v1.flow.yaml")]
    [InlineData("3c363d117b40-v2.flow.json")]
    [InlineData("3c363d117b40-v2.flow.yaml")]
    public void ValidMapsPassSchemaAndSemantics(string name)
    {
        var document = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, name));
        Assert.True(
            SchemaValidator.TryValidate(WorkflowContracts.WorkflowMapSchema, document, out var schemaViolations),
            string.Join("; ", schemaViolations.Select(item => item.Message)));

        var violations = WorkflowMapValidator.Validate(WorkflowMap.FromDocument(document), Facts);
        Assert.Empty(violations);
    }

    /// <summary>
    /// Одна и та же карта в JSON и в YAML обязана дать один и тот же документ — иначе повторная
    /// публикация из другого формата выглядела бы попыткой изменить опубликованную версию.
    /// </summary>
    [Theory]
    [InlineData("3c363d117b40-v1")]
    [InlineData("3c363d117b40-v2")]
    public void JsonAndYamlDescribeTheSameContract(string prefix)
    {
        var json = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, $"{prefix}.flow.json"));
        var yaml = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, $"{prefix}.flow.yaml"));

        Assert.Equal(Canonical(json), Canonical(yaml));
    }

    [Theory]
    [InlineData("invalid-step-type.flow.json")]
    [InlineData("missing-start-step.flow.json")]
    [InlineData("unknown-field.flow.json")]
    [InlineData("unknown-service.flow.json")]
    public void SchemaInvalidMapsAreRejectedByTheSchema(string name)
    {
        var document = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, "invalid", "schema", name));
        Assert.False(SchemaValidator.TryValidate(WorkflowContracts.WorkflowMapSchema, document, out _));
    }

    [Theory]
    [InlineData("cycle.flow.json")]
    [InlineData("dead-end.flow.json")]
    [InlineData("disabled-action-version.flow.json")]
    [InlineData("duplicate-outcome.flow.json")]
    [InlineData("duplicate-step-key.flow.json")]
    [InlineData("incomplete-outcomes.flow.json")]
    [InlineData("invalid-retry.flow.json")]
    [InlineData("missing-end.flow.json")]
    [InlineData("overlapping-mapping.flow.json")]
    [InlineData("policy-mismatch.flow.json")]
    [InlineData("transition-from-end.flow.json")]
    [InlineData("unknown-action.flow.json")]
    [InlineData("unknown-action-version.flow.json")]
    [InlineData("unknown-start-step.flow.json")]
    [InlineData("unreachable-step.flow.json")]
    public void SemanticInvalidMapsAreRejectedByTheValidator(string name)
    {
        var document = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, "invalid", "semantic", name));

        // Схема обязана пропустить такую карту: испорчен именно смысл, а не форма документа.
        Assert.True(SchemaValidator.TryValidate(WorkflowContracts.WorkflowMapSchema, document, out _));

        var violations = WorkflowMapValidator.Validate(WorkflowMap.FromDocument(document), Facts);
        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Действие без идемпотентности нельзя связать с automatic-шагом: доставка задания
    /// at-least-once, и «ровно один предметный эффект» держится ключом идемпотентности действия,
    /// а не движком. Отказ обязан наступать на публикации, а не при первом же перехвате аренды.
    /// </summary>
    [Fact]
    public void ActionWithoutIdempotencyCannotBackAnAutomaticStep()
    {
        var document = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, "3c363d117b40-v1.flow.json"));
        var facts = Facts with
        {
            Actions =
            [
                Facts.Actions[0] with { IdempotencyMode = "none", IdempotencyScope = "none" },
                Facts.Actions[1],
            ],
        };

        var violations = WorkflowMapValidator.Validate(WorkflowMap.FromDocument(document), facts);

        Assert.Contains(violations, item => item.Contains("idempotency_mode 'none'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Сигнал адресуется типом, а не шагом. Два ожидания одного типа сделали бы получателя
    /// раннего сигнала зависящим от порядка обхода карты.
    /// </summary>
    [Fact]
    public void TwoWaitStepsCannotShareOneSignalType()
    {
        var document = WorkflowMapReader.ReadDocument(Path.Combine(FixtureRoot, "3c363d117b40-v1.flow.json"));

        // Ручной шаг превращается во второе ожидание того же типа: граф остаётся корректным,
        // ломается только однозначность адресации сигнала.
        var steps = document["steps"]!.AsArray();
        var manual = steps.First(step => step!["type"]!.GetValue<string>() == "manual")!.AsObject();
        var manualKey = manual["key"]!.GetValue<string>();
        var manualOutcome = manual["allowed_outcomes"]!.AsArray()[0]!.GetValue<string>();
        var index = steps.IndexOf(manual);
        steps.RemoveAt(index);
        steps.Insert(index, new JsonObject
        {
            ["key"] = manualKey,
            ["type"] = "wait_signal",
            ["signal_type"] = "signal_f21581.received_f1fa89",
            ["outcome"] = manualOutcome,
        });

        var violations = WorkflowMapValidator.Validate(WorkflowMap.FromDocument(document), Facts);

        Assert.Contains(violations, item => item.Contains("more than one wait_signal step", StringComparison.Ordinal));
    }

    private static string Canonical(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                return "{" + string.Join(",", o
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"\"{pair.Key}\":{(pair.Value is null ? "null" : Canonical(pair.Value))}")) + "}";
            case JsonArray a:
                return "[" + string.Join(",", a.Select(item => item is null ? "null" : Canonical(item))) + "]";
            default:
                return node.ToJsonString();
        }
    }
}

/// <summary>Указатели RFC 6901 и сборка payload по mapping карты.</summary>
public sealed class WorkflowPayloadTests
{
    [Theory]
    [InlineData("/a", "/a/b", true)]
    [InlineData("/a", "/ab", false)]
    [InlineData("/a/b", "/a/b", true)]
    [InlineData("/a/b", "/a/c", false)]
    public void OverlapIsDecidedBySegments(string left, string right, bool expected)
    {
        Assert.True(JsonPointer.TryParse(left, out var first));
        Assert.True(JsonPointer.TryParse(right, out var second));
        Assert.Equal(expected, JsonPointer.Overlaps(first, second));
    }

    [Fact]
    public void MappingCopiesSourceValuesOverConstants()
    {
        var constants = (JsonObject)JsonNode.Parse("""{"marker":"fixed"}""")!;
        var data = JsonNode.Parse("""{"outer":{"inner":"value"},"flag":true}""");

        var built = WorkflowPayload.TryBuild(
            constants,
            new Dictionary<string, string> { ["/copied"] = "/outer/inner", ["/flag"] = "/flag" },
            data,
            out var payload,
            out _);

        Assert.True(built);
        Assert.Equal("fixed", payload["marker"]!.GetValue<string>());
        Assert.Equal("value", payload["copied"]!.GetValue<string>());
        Assert.True(payload["flag"]!.GetValue<bool>());
    }

    [Fact]
    public void MissingSourceIsReportedInsteadOfSilentlySkipped()
    {
        var built = WorkflowPayload.TryBuild(
            [],
            new Dictionary<string, string> { ["/copied"] = "/outer/absent" },
            JsonNode.Parse("""{"outer":{}}"""),
            out _,
            out var missing);

        Assert.False(built);
        Assert.Equal("/outer/absent", missing);
    }
}
