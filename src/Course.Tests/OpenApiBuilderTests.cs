using System.Text.Json.Nodes;
using Course.Shared;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Документация строится из опубликованного манифеста, а не из C#-типов: новое действие обязано
/// документироваться без правки самого runtime.
/// </summary>
public sealed class OpenApiBuilderTests
{
    /// <summary>Строит тестовый манифест действия с разумными значениями по умолчанию, которые можно переопределить точечно.</summary>
    private static ActionManifest Manifest(string module, string action, int version, bool enabled = true)
        => new()
        {
            Module = module,
            Action = action,
            Version = version,
            HttpMethod = "POST",
            TargetSchema = "domain",
            TargetFunction = $"{action}_v{version}",
            RequestSchema = JsonNode.Parse("""
                {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
                 "additionalProperties":false,"required":["mode"],"properties":{"mode":{"type":"string"}}}
                """)!,
            ResponseSchema = JsonNode.Parse("""
                {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
                 "required":["stored"],"properties":{"stored":{"type":"boolean"}}}
                """)!,
            Outcomes = ["APPLIED"],
            RequiredPolicy = ["workflow:execute"],
            IdempotencyMode = "required",
            IdempotencyScope = "principal_action",
            TimeoutMs = 2000,
            Enabled = enabled,
            IsDefault = true,
        };

    [Fact]
    public void VersionDocumentDescribesExactlyOneOperation()
    {
        var document = OpenApiBuilder.VersionDocument(Manifest("opencheck", "probe", 2));
        var paths = document["paths"]!.AsObject();

        Assert.Single(paths);
        Assert.True(paths.ContainsKey("/api/opencheck/probe"));
        Assert.NotNull(paths["/api/opencheck/probe"]!["post"]);
        // Поля из request_schema и response_schema манифеста реально попадают в итоговый документ.
        Assert.Contains("mode", document.ToJsonString());
        Assert.Contains("stored", document.ToJsonString());
    }

    [Fact]
    public void VersionsAreNotMergedIntoOneDocument()
    {
        // Контракт запрещает объединять версии одного действия через "oneOf": у каждой версии
        // должен быть свой, полностью независимый документ.
        var first = OpenApiBuilder.VersionDocument(Manifest("opencheck", "probe", 1)).ToJsonString();
        var second = OpenApiBuilder.VersionDocument(Manifest("opencheck", "probe", 2)).ToJsonString();

        Assert.Contains("opencheck_probe_v1", first);
        Assert.Contains("opencheck_probe_v2", second);
        Assert.DoesNotContain("oneOf", first);
        Assert.DoesNotContain("_v2", first);
    }

    [Fact]
    public void DefaultDocumentContainsEveryEnabledDefaultRoute()
    {
        var document = OpenApiBuilder.DefaultDocument(
        [
            Manifest("payment", "request", 1),
            Manifest("operation", "get", 1),
        ]);

        var paths = document["paths"]!.AsObject();

        Assert.Equal(2, paths.Count);
        Assert.True(paths.ContainsKey("/api/payment/request"));
        Assert.True(paths.ContainsKey("/api/operation/get"));
    }

    [Fact]
    public void IdempotencyKeyIsRequiredExactlyWhenTheManifestRequiresIt()
    {
        // Параметр Idempotency-Key в OpenAPI-документе обязан быть помечен как required=true
        // ровно тогда, когда idempotency_mode манифеста равен "required" — и как необязательный
        // в остальных случаях.
        var required = OpenApiBuilder.VersionDocument(Manifest("payment", "request", 1));
        var manifest = Manifest("operation", "get", 1);
        var optional = OpenApiBuilder.VersionDocument(new ActionManifest
        {
            Module = manifest.Module,
            Action = manifest.Action,
            Version = manifest.Version,
            HttpMethod = manifest.HttpMethod,
            TargetSchema = manifest.TargetSchema,
            TargetFunction = manifest.TargetFunction,
            RequestSchema = manifest.RequestSchema,
            ResponseSchema = manifest.ResponseSchema,
            Outcomes = manifest.Outcomes,
            RequiredPolicy = manifest.RequiredPolicy,
            IdempotencyMode = "none",
            IdempotencyScope = "none",
            TimeoutMs = manifest.TimeoutMs,
            Enabled = true,
            IsDefault = true,
        });

        Assert.True(KeyIsRequired(required, "/api/payment/request"));
        Assert.False(KeyIsRequired(optional, "/api/operation/get"));
    }

    /// <summary>Ищет в OpenAPI-документе параметр заголовка Idempotency-Key и возвращает, помечен ли он как обязательный.</summary>
    private static bool KeyIsRequired(JsonObject document, string path)
        => document["paths"]![path]!["post"]!["parameters"]!.AsArray()
            .Any(parameter => parameter!["name"]!.GetValue<string>() == "Idempotency-Key"
                              && parameter["required"]!.GetValue<bool>());
}
