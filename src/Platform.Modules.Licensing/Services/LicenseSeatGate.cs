namespace Platform.Modules.Licensing.Services;

/// <summary>
/// 席位"计数 → 落库"的串行化闸门。
/// <para>
/// 平台要求单副本部署（见 <c>SingleInstanceGuard</c>），进程内闸门即可覆盖本进程所有写入路径；
/// InMemory 集成测试同样有效。关系型库不需要额外咨询锁。
/// </para>
/// </summary>
internal static class LicenseSeatGate
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
