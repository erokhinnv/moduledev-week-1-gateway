using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Course.Tests;

/// <summary>
/// Свойства самого репозитория, которые нельзя увидеть из кода.
///
/// Бит на исполнение — ровно такой случай. На Windows он не отслеживается вовсе
/// (<c>core.filemode=false</c>), поэтому потерю видно только там, где по README запускают
/// скрипт из свежего клона на Linux. Один раз это уже утекло в сдачу; здесь оно закреплено.
/// </summary>
public sealed class RepositoryContractTests
{
    /// <summary>Скрипты, которые README предлагает запускать напрямую.</summary>
    public static TheoryData<string> Scripts =>
    [
        "check.sh",
        "course.sh",
        "autocheck/check.sh",
        "scripts/integration-tests.sh",
    ];

    [Theory]
    [MemberData(nameof(Scripts))]
    public void ShellScriptsKeepTheirExecutableBitInGit(string path)
    {
        var mode = GitIndexMode(path);

        Assert.True(
            mode == "100755",
            $"{path} записан в индекс как {mode}: в свежем клоне на Linux команда из README " +
            "завершится Permission denied. Вернуть: git update-index --chmod=+x " + path);
    }

    /// <summary>
    /// Таблица конфигурации README — это обещание: «переменная существует и на что-то влияет».
    /// Обещание без проводки уже встречалось дважды (README называл переменные, которые
    /// compose.yaml не пробрасывал), поэтому оно сверяется с самим контуром: каждая переменная
    /// таблицы обязана быть либо проброшена из окружения запуска (<c>${ИМЯ}</c>), либо записана в
    /// compose.yaml литералом, либо задана в образе.
    /// </summary>
    [Fact]
    public void EveryVariableOfTheReadmeConfigurationTableIsWiredIntoTheContour()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));
        var images = string.Join(
            '\n',
            Directory.GetFiles(Path.Combine(RepositoryRoot, "docker"), "*.Dockerfile").Select(File.ReadAllText));

        var variables = ConfigurationTableVariables(readme);

        // Разбор обязан находить таблицу целиком: пустой список «прошёл бы» проверку молча.
        Assert.True(variables.Count >= 40, $"таблица конфигурации README разобрана не полностью: {variables.Count}");
        Assert.Contains("COURSE_OUTBOX_BACKOFF_MAX_MS", variables);
        Assert.Contains("COURSE_POSTGRES_DB", variables);

        var unwired = variables
            .Where(name =>
                !compose.Contains("${" + name, StringComparison.Ordinal)
                && !Regex.IsMatch(compose, $@"(?m)^\s+{Regex.Escape(name)}:")
                && !Regex.IsMatch(images, $@"(?m)\b{Regex.Escape(name)}="))
            .ToList();

        Assert.True(
            unwired.Count == 0,
            "README обещает переменные, которых нет ни в compose.yaml, ни в образах: " + string.Join(", ", unwired));
    }

    /// <summary>
    /// Переменные, которым контракт конфигурации недели 4 не разрешает значение по умолчанию в
    /// отслеживаемом compose.yaml (колонка «Default в tracked Compose» — «Нет»): конфигурация JWT,
    /// ключ подписи квитанций, capability и токены, пароль наблюдателя.
    /// </summary>
    public static TheoryData<string> VariablesWithoutDefault =>
    [
        "COURSE_JWT_ISSUER",
        "COURSE_JWT_AUDIENCE",
        "COURSE_JWT_SIGNING_KEY",
        "COURSE_AUTOCHECK_PASSWORD",
        "PROVIDER_CALLBACK_CAPABILITY",
        "PROVIDER_CALLBACK_TOKEN",
        "PROVIDER_HMAC_SECRET",
        "PROVIDER_AUDIT_TOKEN",
    ];

    [Theory]
    [MemberData(nameof(VariablesWithoutDefault))]
    public void ComposeKeepsNoDefaultWhereTheContractAllowsNone(string name)
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));

        Assert.Contains("${" + name, compose, StringComparison.Ordinal);
        // ${NAME:-value} и ${NAME-value} — значение по умолчанию; пустой хвост значением не является.
        Assert.DoesNotMatch(@"\$\{" + Regex.Escape(name) + @":?-[^}]+\}", compose);
    }

    /// <summary>
    /// Имена переменных из первой колонки таблицы раздела «Конфигурация». Сокращённая запись
    /// (<c>`COURSE_POSTGRES_HOST` / `_PORT`</c>) разворачивается заменой стольких же последних
    /// сегментов предыдущего полного имени.
    /// </summary>
    private static List<string> ConfigurationTableVariables(string readme)
    {
        var start = readme.IndexOf("### Конфигурация", StringComparison.Ordinal);
        Assert.True(start >= 0, "в README нет раздела «Конфигурация»");
        var end = readme.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? readme[start..] : readme[start..end];

        var variables = new List<string>();
        foreach (var line in section.Split('\n'))
        {
            if (!line.StartsWith("| `", StringComparison.Ordinal))
            {
                continue;
            }

            var firstCell = line.Split('|')[1];
            string? full = null;
            foreach (Match token in Regex.Matches(firstCell, "`(_?[A-Z][A-Z0-9_]+)`"))
            {
                var name = token.Groups[1].Value;
                if (name.StartsWith('_'))
                {
                    Assert.True(full is not null, $"сокращение {name} без полного имени перед ним");
                    var suffix = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    var segments = full!.Split('_');
                    name = string.Join('_', segments[..^suffix.Length].Concat(suffix));
                }
                else
                {
                    full = name;
                }

                variables.Add(name);
            }
        }

        return variables;
    }

    private static string GitIndexMode(string path)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("--stage");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git could not be started");

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        Assert.True(process.ExitCode == 0, $"git ls-files не выполнился для {path}");
        Assert.False(string.IsNullOrWhiteSpace(output), $"{path} не отслеживается Git");

        return output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>Корень решения ищется по тому же признаку, что и в остальных тестах — по check.sh.</summary>
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "check.sh")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
