using System.Text.Json;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Api;

/// <summary>
/// Сторона ЧТЕНИЯ неизменяемого каталога действий.
///
/// Разрешение (resolve) действия всегда идёт напрямую в PostgreSQL, без какого-либо кеша в
/// памяти C#-процесса. Благодаря этому команда activate или disable, выполненная через CLI, видна
/// уже на следующем же HTTP-запросе — без протокола сброса кеша и без пересоздания сервиса api.
/// Это прямое следствие требования "новое действие работает без пересборки API".
/// </summary>
public sealed class ActionCatalog(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    /// <summary>
    /// Разрешает действие для реального вызова (используется HTTP-исполнителем ActionRuntime).
    /// version = null означает "взять текущую версию по умолчанию для этого маршрута".
    /// Возвращает null, если действие/версия не найдены или выключены.
    /// </summary>
    public async Task<ActionManifest?> ResolveAsync(
        string module,
        string action,
        int? version,
        CancellationToken cancellationToken)
    {
        var json = await ScalarAsync(
            "SELECT api.resolve_action($1, $2, $3)::text",
            cancellationToken,
            Text(module),
            Text(action),
            Integer(version));

        return ReadManifest(json);
    }

    /// <summary>
    /// Возвращает ЛЮБУЮ опубликованную версию действия — включённую или выключенную — для
    /// построения точного версионного OpenAPI-документа (/openapi/actions/.../{version}.json).
    /// В отличие от ResolveAsync, здесь допускается получить и отключённую версию: она всё ещё
    /// должна быть задокументирована (как deprecated), просто больше не вызывается.
    /// </summary>
    public async Task<ActionManifest?> DescribeAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        var json = await ScalarAsync(
            "SELECT api.describe_action($1, $2, $3)::text",
            cancellationToken,
            Text(module),
            Text(action),
            Integer(version));

        return ReadManifest(json);
    }

    /// <summary>Возвращает список всех включённых действий в их версии по умолчанию — для документа /openapi/default.json.</summary>
    public async Task<IReadOnlyList<ActionManifest>> DefaultRoutesAsync(CancellationToken cancellationToken)
    {
        var json = await ScalarAsync("SELECT api.default_actions()::text", cancellationToken);
        if (json is null)
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement.EnumerateArray().Select(ActionManifest.FromCatalog).ToArray();
    }

    /// <summary>Разбирает JSON-конверт, вернувшийся из функции каталога, и достаёт из него манифест, если статус "ok".</summary>
    private static ActionManifest? ReadManifest(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) ||
            status.GetString() != "ok")
        {
            return null;
        }

        return ActionManifest.FromCatalog(root.GetProperty("result"));
    }

    /// <summary>Выполняет SQL-запрос, возвращающий одно скалярное текстовое значение (обычно JSON в виде текста).</summary>
    private async Task<string?> ScalarAsync(
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = _dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    // Ниже — небольшие помощники для построения типизированных параметров Npgsql. Они собраны
    // здесь и переиспользуются другими классами api-слоя (ActionRuntime, DispatchRecorder),
    // чтобы не дублировать одну и ту же логику построения параметров в нескольких местах.

    internal static NpgsqlParameter Text(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    internal static NpgsqlParameter Integer(int? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)value ?? DBNull.Value };

    internal static NpgsqlParameter Jsonb(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };
}
