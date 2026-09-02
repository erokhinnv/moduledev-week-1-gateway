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
    /// <summary>Имена переменных окружения с логином и паролем одной роли.</summary>
    public sealed record Credentials(string UserVariable, string PasswordVariable)
    {
        /// <summary>Основной набор: runtime для api, роль миграций для cli.</summary>
        public static readonly Credentials Default = new("COURSE_POSTGRES_USER", "COURSE_POSTGRES_PASSWORD");

        /// <summary>Отдельный набор роли публикации, которым пользуются только команды action *.</summary>
        public static readonly Credentials Publication = new("COURSE_PUBLISHER_USER", "COURSE_PUBLISHER_PASSWORD");

        /// <summary>
        /// Набор роли ОПЕРАЦИЙ: им идут команды, двигающие процессы (start, signal, get и проба
        /// fencing). Публиковать карты или действия эта роль не может — разделение ровно обратное
        /// роли публикации, чтобы ни один набор учётных данных не давал и определения, и исполнение.
        ///
        /// Отката к набору публикации здесь СОЗНАТЕЛЬНО нет: у публикатора отозваны права на
        /// запуск процессов и приём сигналов, поэтому «тихий откат» дал бы не работу без выигрыша
        /// от разделения, а отказ по правам в середине команды. Отсутствие переменных должно быть
        /// понятной ошибкой конфигурации, а не отложенным сюрпризом.
        /// </summary>
        public static readonly Credentials Operation = new("COURSE_OPERATOR_USER", "COURSE_OPERATOR_PASSWORD");

        /// <summary>
        /// Набор публикации, если он задан, иначе основной. Возврат к основному нужен для
        /// совместимости: контур, развёрнутый до разделения ролей, продолжает работать на одних
        /// учётных данных, просто без выигрыша от разделения.
        /// </summary>
        public static Credentials PublicationOrDefault()
            => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Publication.UserVariable))
                ? Default
                : Publication;
    }

    /// <summary>
    /// Строит строку подключения к PostgreSQL. Если задана переменная окружения
    /// COURSE_POSTGRES_CONNECTION целиком — используется она; иначе строка собирается из
    /// отдельных переменных (хост, порт, база, логин, пароль), причём логин и пароль берутся из
    /// переменных запрошенного набора учётных данных.
    /// </summary>
    public static string ConnectionString(
        int connectTimeoutSeconds,
        int commandTimeoutSeconds,
        int maxPoolSize,
        Credentials? credentials = null)
    {
        credentials ??= Credentials.Default;

        var explicitConnection = Environment.GetEnvironmentVariable("COURSE_POSTGRES_CONNECTION");
        var builder = string.IsNullOrWhiteSpace(explicitConnection)
            ? new NpgsqlConnectionStringBuilder
            {
                Host = Env.Optional("COURSE_POSTGRES_HOST", "postgres"),
                Port = Env.OptionalInt("COURSE_POSTGRES_PORT", 5432),
                Database = Env.Optional("COURSE_POSTGRES_DB", "course"),
                Username = Env.Require(credentials.UserVariable),
                Password = Env.Require(credentials.PasswordVariable),
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
        int maxPoolSize = 40,
        Credentials? credentials = null)
    {
        var builder = new NpgsqlDataSourceBuilder(
            ConnectionString(connectTimeoutSeconds, commandTimeoutSeconds, maxPoolSize, credentials));
        return builder.Build();
    }
}
