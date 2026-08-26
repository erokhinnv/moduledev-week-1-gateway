using System.Text.Json.Nodes;
using Course.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Course.Cli;

/// <summary>
/// Применение миграций базы данных с проверкой контрольных сумм (checksummed migrations).
///
/// Файлы .sql применяются в лексикографическом (алфавитном) порядке имён, каждый файл — в своей
/// отдельной транзакции. Повторный запуск с тем же самым файлом (та же контрольная сумма) —
/// безопасен и просто пропускает уже применённый файл. А вот изменение содержимого уже применённого
/// файла — это конфликт: он будет обнаружен по несовпадению контрольной суммы и остановит
/// выполнение с ошибкой, а не молча применит изменённый файл заново.
///
/// Все миграции выполняются от лица роли course_owner — единственного владельца всех объектов
/// схемы. Ни api, ни сам обычный пользователь этой роли напрямую не получают.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource)
{
    /// <summary>Служебная схема и таблица, в которой хранится история применённых миграций (имя файла и его контрольная сумма).</summary>
    private const string MetaSchema = """
        CREATE SCHEMA IF NOT EXISTS course_meta;
        CREATE TABLE IF NOT EXISTS course_meta.schema_migration (
            filename   text PRIMARY KEY,
            checksum   text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            applied_by text NOT NULL DEFAULT current_user
        );
        """;

    public async Task<JsonObject> ApplyAsync(string directory, CancellationToken cancellationToken)
    {
        var source = new DirectoryInfo(directory);
        if (!source.Exists)
        {
            throw new CliException(ContractCodes.RequestInvalid, $"migration directory does not exist: {directory}");
        }

        // Строгий алфавитный порядок — это то, что гарантирует контракт задания: файлы с именами
        // вида 001_..., 002_..., 003_... применяются строго по возрастанию номера.
        var files = source.GetFiles("*.sql")
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, null, "SET ROLE course_owner", cancellationToken);
        await ExecuteAsync(connection, null, MetaSchema, cancellationToken);

        var applied = new JsonArray();
        var skipped = new JsonArray();

        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
            var checksum = Hashing.Sha256Hex(bytes);
            var script = await File.ReadAllTextAsync(file.FullName, cancellationToken);

            // Каждый файл — своя отдельная транзакция: если файл №3 упадёт с ошибкой, файлы №1 и
            // №2 всё равно останутся применёнными и зафиксированными.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var recorded = await ScalarAsync(
                connection,
                transaction,
                "SELECT checksum FROM course_meta.schema_migration WHERE filename = $1",
                cancellationToken,
                Text(file.Name)) as string;

            if (recorded is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (recorded != checksum)
                {
                    // Тот же файл, но с другим содержимым — явное нарушение неизменяемости
                    // применённой миграции.
                    throw new CliException(
                        ContractCodes.MigrationConflict,
                        "migration file was already applied with a different checksum",
                        new JsonObject { ["file"] = file.Name });
                }

                skipped.Add(file.Name);
                continue;
            }

            await ExecuteAsync(connection, transaction, script, cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO course_meta.schema_migration (filename, checksum) VALUES ($1, $2)",
                cancellationToken,
                Text(file.Name),
                Text(checksum));

            await transaction.CommitAsync(cancellationToken);
            applied.Add(file.Name);
        }

        return new JsonObject
        {
            ["resource"] = "migration",
            ["operation"] = "applied",
            ["directory"] = directory,
            ["applied"] = applied,
            ["skipped"] = skipped,
        };
    }

    /// <summary>Выполняет SQL-команду без возврата результата, в рамках указанной транзакции (или вне транзакции, если transaction = null).</summary>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Выполняет SQL-запрос и возвращает одно скалярное значение.</summary>
    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static NpgsqlParameter Text(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };
}
