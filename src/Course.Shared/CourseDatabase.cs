using Npgsql;

namespace Course.Shared;

/// <summary>
/// Фабрика подключений к PostgreSQL.
///
/// Важный момент границы доверия: runtime (сервис api) и cli (публикация) используют РАЗНЫЕ
/// учётные данные и настраиваются независимо друг от друга. Сервис api никогда не получает
/// пароль от роли, которой разрешено накатывать миграции и публиковать действия — так что даже
/// если api скомпрометирован, он физически не может изменить каталог действий или структуру базы.
/// </summary>
public static class CourseDatabase
{
    /// <summary>
    /// Строит строку подключения к PostgreSQL. Если задана переменная окружения
    /// COURSE_POSTGRES_CONNECTION целиком — используется она; иначе строка собирается из
    /// отдельных переменных (хост, порт, база, логин, пароль).
    /// </summary>
    public static string ConnectionString(int connectTimeoutSeconds, int commandTimeoutSeconds, int maxPoolSize)
    {
        var explicitConnection = Environment.GetEnvironmentVariable("COURSE_POSTGRES_CONNECTION");
        var builder = string.IsNullOrWhiteSpace(explicitConnection)
            ? new NpgsqlConnectionStringBuilder
            {
                Host = Env.Optional("COURSE_POSTGRES_HOST", "postgres"),
                Port = Env.OptionalInt("COURSE_POSTGRES_PORT", 5432),
                Database = Env.Optional("COURSE_POSTGRES_DB", "course"),
                Username = Env.Require("COURSE_POSTGRES_USER"),
                Password = Env.Require("COURSE_POSTGRES_PASSWORD"),
            }
            : new NpgsqlConnectionStringBuilder(explicitConnection);

        builder.Timeout = connectTimeoutSeconds;
        builder.CommandTimeout = commandTimeoutSeconds;
        builder.MaxPoolSize = maxPoolSize;
        builder.MinPoolSize = 0;
        // Короткое время жизни соединений в пуле нужно для того, чтобы сервис пережил перезапуск
        // PostgreSQL: старые "протухшие" соединения быстро закрываются и заменяются новыми, без
        // необходимости перезапускать сам процесс api или gateway.
        builder.ConnectionIdleLifetime = 15;
        builder.ConnectionPruningInterval = 5;
        builder.KeepAlive = 30;
        builder.ApplicationName = Env.Optional("COURSE_APPLICATION_NAME", "course");
        return builder.ConnectionString;
    }

    /// <summary>Создаёт пул подключений (NpgsqlDataSource) с указанными таймаутами и размером пула.</summary>
    public static NpgsqlDataSource CreateDataSource(
        int connectTimeoutSeconds = 3,
        int commandTimeoutSeconds = 30,
        int maxPoolSize = 40)
    {
        var builder = new NpgsqlDataSourceBuilder(
            ConnectionString(connectTimeoutSeconds, commandTimeoutSeconds, maxPoolSize));
        return builder.Build();
    }
}
