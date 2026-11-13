using System.IO.Pipes;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>
/// 只在 Docker 可用的环境执行（CI 的 ubuntu-latest 自带 Docker）。
/// xUnit 2 没有运行期跳过，因此在发现阶段探测一次；本机没开 Docker Desktop 时整组用例显式跳过而不是红。
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!DockerProbe.IsAvailable)
        {
            Skip = "需要 Docker（Testcontainers PostgreSQL）；当前环境不可用，已跳过。";
        }
    }
}

internal static class DockerProbe
{
    private static readonly Lazy<bool> Available = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Available.Value;

    private static bool Compute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("POSTGRES_TESTS"), "0", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            {
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                using var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut);
                pipe.Connect(200);
                return pipe.IsConnected;
            }

            return File.Exists("/var/run/docker.sock")
                   || File.Exists(Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       ".docker",
                       "run",
                       "docker.sock"));
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
