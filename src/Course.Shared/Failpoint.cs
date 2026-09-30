using System.Text.Json.Nodes;

namespace Course.Shared;

/// <summary>
/// Барьер подтверждения закрытого тестового профиля.
///
/// Точка остановки задаётся ТОЛЬКО окружением процесса и только при <c>COURSE_TEST_PROFILE=1</c>:
/// публичного способа включить её не существует, поэтому в обычном профиле этот класс не делает
/// ничего. Достигнув точки, компонент печатает ровно одну структурную строку и блокируется до
/// принудительной остановки — проверка ждёт именно её, а не таймаут, и граница аварии
/// воспроизводится точно, а не «примерно».
/// </summary>
public sealed class Failpoint
{
    /// <summary>Пустой барьер: точка не задана или профиль не тестовый.</summary>
    public static readonly Failpoint Disabled = new(string.Empty, string.Empty);

    private int _reached;

    private Failpoint(string name, string instanceId)
    {
        Name = name;
        InstanceId = instanceId;
    }

    /// <summary>Имя активной точки остановки или пустая строка.</summary>
    public string Name { get; }

    /// <summary>Идентификатор экземпляра в подтверждении.</summary>
    public string InstanceId { get; }

    /// <summary>
    /// Читает точку остановки из окружения. Вне тестового профиля значение игнорируется целиком:
    /// оставленная в конфигурации переменная не должна однажды остановить рабочий процесс.
    /// </summary>
    public static Failpoint FromEnvironment(string instanceIdVariable)
    {
        if (Env.Optional("COURSE_TEST_PROFILE", "0") != "1")
        {
            return Disabled;
        }

        var name = Env.Optional("COURSE_FAILPOINT", string.Empty).Trim();
        if (name.Length == 0)
        {
            return Disabled;
        }

        var instanceId = Env.Optional(instanceIdVariable, string.Empty).Trim();
        if (instanceId.Length == 0)
        {
            instanceId = Environment.MachineName;
        }

        return new Failpoint(name, instanceId);
    }

    /// <summary>true, если активна именно эта точка.</summary>
    public bool Matches(string name) => Name.Length > 0 && Name == name;

    /// <summary>
    /// Подтверждает достижение точки и блокируется навсегда. Вызов никогда не возвращает
    /// управление: граница ЗА failpoint не имеет права быть пройденной остановленным
    /// компонентом — иначе проверяемый инвариант доказывал бы не то, что заявлено.
    /// </summary>
    public async Task ReachAsync(string name)
    {
        if (Interlocked.Exchange(ref _reached, 1) == 0)
        {
            var acknowledgement = new JsonObject
            {
                ["event"] = "failpoint.reached",
                ["name"] = name,
                ["instanceId"] = InstanceId,
            };

            await Console.Out.WriteLineAsync(acknowledgement.ToJsonString());
            await Console.Out.FlushAsync();
        }

        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }
}
