using Npgsql;

namespace Course.Shared;

/// <summary>
/// Приведение исключения к безопасной для логов форме.
///
/// Зачем это нужно. Обычный <c>logger.LogError(exception, ...)</c> печатает исключение целиком,
/// вместе с сообщением. У <see cref="PostgresException"/> сообщение при нарушении ограничения
/// содержит ЗНАЧЕНИЯ данных — например
/// <c>Key (scope, scope_key, module, action, request_id)=(principal_action, candidate-client, ...)
/// already exists</c>. Это фактически кусок полезной нагрузки запроса и идентификатор
/// вызывающего, попавшие в журнал, то есть ровно то, что контракт запрещает туда писать.
///
/// Поэтому в обычный журнал уходит только тип исключения и SQLSTATE — этого достаточно, чтобы
/// понять природу сбоя, и недостаточно, чтобы восстановить содержимое запроса.
/// </summary>
public static class Redaction
{
    /// <summary>
    /// Возвращает короткое безопасное описание сбоя вида <c>PostgresException/23505</c>
    /// или <c>SocketException</c>, если это не ошибка PostgreSQL.
    /// </summary>
    public static string Describe(Exception exception)
    {
        // Разворачиваем цепочку InnerException: реальная причина часто завёрнута в обёртку
        // уровня Npgsql, и SQLSTATE лежит именно во вложенном PostgresException.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return $"{current.GetType().Name}/{postgres.SqlState}";
            }
        }

        return exception.GetType().Name;
    }
}
