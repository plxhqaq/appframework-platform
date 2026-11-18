namespace Platform.Modules.Backups.Services;

/// <summary>
/// 版本配额"计数 → 插入"的串行化闸门。
/// <para>
/// 配额没有可落库的计数列，只能先数后插；平台要求单副本部署（见 <c>SingleInstanceGuard</c>），
/// 进程内闸门即可避免并发上传同时越过 <c>MaxVersions</c>。InMemory 测试同样有效。
/// </para>
/// </summary>
internal static class BackupQuotaGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>进入闸门；释放返回值即离开闸门。</summary>
    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }
}
