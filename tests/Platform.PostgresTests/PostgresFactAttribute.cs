using System.IO.Pipes;
using Xunit;

namespace Platform.PostgresTests;

/// <summary>
/// 只在 PostgreSQL 可用的环境执行（Docker 或 <c>POSTGRES_TEST_CONNECTION</c> 指向的实例）。
/// xUnit 2 没有运行期跳过，因此在发现阶段探测一次；本机两者都没有时整组用例显式跳过而不是红。
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!DockerProbe.IsAvailable)
        {
            Skip = "需要 Docker（Testcontainers）或 POSTGRES_TEST_CONNECTION；当前环境不可用，已跳过。";
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

        // 显式给了连接串就跳过 Docker 探测：内网机器拉不到 Docker Hub 时仍可对现有 PostgreSQL 跑这组用例
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION")))
        {
            return true;
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
