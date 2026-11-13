using System.Collections.Concurrent;
using Platform.Kernel.Abstractions;

namespace Platform.Kernel.Security;

/// <summary>
/// 应用启用状态的进程内短缓存。请求路径不必每个请求都查库；
/// 启用/停用由写入侧主动失效，保证本副本立即生效，其它副本最迟在一个 TTL 后收敛。
/// </summary>
public sealed class AppStateCache
{
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>构造。</summary>
    /// <param name="clock">时钟。</param>
    /// <param name="ttl">缓存时长；非正数表示完全不缓存（集成测试用）。</param>
    public AppStateCache(IClock clock, TimeSpan ttl)
    {
        _clock = clock;
        _ttl = ttl;
    }

    /// <summary>尝试读取缓存的启用状态；过期项会被顺手移除。</summary>
    public bool TryGet(string appKey, out bool enabled)
    {
        enabled = false;
        if (_ttl <= TimeSpan.Zero || !_entries.TryGetValue(appKey, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= _clock.UtcNow)
        {
            _entries.TryRemove(appKey, out _);
            return false;
        }

        enabled = entry.Enabled;
        return true;
    }

    /// <summary>写入缓存；<see cref="TimeSpan.Zero"/> 或负 TTL 时忽略。</summary>
    public void Set(string appKey, bool enabled)
    {
        if (_ttl <= TimeSpan.Zero)
        {
            return;
        }

        _entries[appKey] = new Entry(enabled, _clock.UtcNow + _ttl);
    }

    /// <summary>失效指定应用；启用/停用成功后由写入侧调用。</summary>
    public void Invalidate(string appKey) => _entries.TryRemove(appKey, out _);

    private readonly record struct Entry(bool Enabled, DateTimeOffset ExpiresAt);
}
