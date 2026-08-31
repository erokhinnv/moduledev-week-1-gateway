using System.Net.Sockets;
using Npgsql;

namespace Course.Shared;

/// <summary>
/// Переводит сбой PostgreSQL или сетевого транспорта в код ошибки контракта.
///
/// Важно: исходное сообщение об ошибке намеренно ОТБРАСЫВАЕТСЯ. Текст SQL-запроса, имена схем и
/// функций, строка подключения — всё это никогда не должно попасть в ответ клиенту. Наружу уходит
/// только один из заранее известных технических кодов (например dependency.unavailable), а
/// подробности остаются только во внутренних логах сервера.
/// </summary>
public static class PostgresFailure
{
    /// <summary>
    /// Классифицирует исключение, разворачивая цепочку InnerException, пока не найдёт известный
    /// тип ошибки (PostgresException с конкретным SQL-состоянием, сетевую ошибку, таймаут и т.д.).
    /// </summary>
    public static string Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException postgres:
                    return ClassifySqlState(postgres.SqlState);
                case NpgsqlException:
                case SocketException:
                case TimeoutException:
                case IOException:
                    // Любая ошибка на уровне сети или клиентской библиотеки Npgsql трактуется как
                    // временная недоступность базы данных.
                    return ContractCodes.DependencyUnavailable;
            }
        }

        return ContractCodes.InternalError;
    }

    /// <summary>
    /// Распознаёт конфликт параллельного доступа, который имеет смысл повторить.
    ///
    /// 40001 (serialization_failure) и 40P01 (deadlock_detected) — это не поломка и не ошибка
    /// вызывающего: PostgreSQL сама отменила одну из конкурирующих транзакций, чтобы разрешить
    /// конфликт. Правильная реакция на такой отказ — повторить операцию, а не показать ошибку.
    /// Актуально для команд каталога, которые конкурируют за один и тот же маршрут.
    /// </summary>
    public static bool IsTransientConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState is "40001" or "40P01";
            }
        }

        return false;
    }

    /// <summary>
    /// Переводит SQL-состояние (SQLSTATE) PostgreSQL в код ошибки контракта. Коды SQLSTATE — это
    /// стандартизированные пятисимвольные коды, определённые стандартом SQL и документацией
    /// PostgreSQL (https://www.postgresql.org/docs/current/errcodes-appendix.html).
    /// </summary>
    private static string ClassifySqlState(string? sqlState)
    {
        if (sqlState is null)
        {
            return ContractCodes.InternalError;
        }

        // 57014 = query_canceled: запрос отменён по достижении дедлайна (statement_timeout),
        // выставленного на основе timeout_ms из манифеста действия.
        if (sqlState == "57014")
        {
            return ContractCodes.ActionTimeout;
        }

        // Класс ошибок 08 в PostgreSQL — это "connection exception" (проблемы с соединением).
        if (sqlState.StartsWith("08", StringComparison.Ordinal))
        {
            return ContractCodes.DependencyUnavailable;
        }

        // Отдельные точечные коды, которые тоже означают временную недоступность сервера:
        // 53300/53400 — нехватка ресурсов (слишком много соединений и т.п.),
        // 57P01..57P03 — административное завершение работы / перезапуск сервера,
        // 58000/58030 — ошибки системы (например файловой подсистемы).
        return sqlState switch
        {
            "53300" or "53400" or "57P01" or "57P02" or "57P03" or "58000" or "58030" =>
                ContractCodes.DependencyUnavailable,
            _ => ContractCodes.InternalError,
        };
    }
}
