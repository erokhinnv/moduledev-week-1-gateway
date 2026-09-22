using System.Diagnostics;
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
