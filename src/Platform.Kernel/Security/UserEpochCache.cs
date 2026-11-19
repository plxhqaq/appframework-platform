using System.Collections.Concurrent;
using Platform.Kernel.Abstractions;

namespace Platform.Kernel.Security;

/// <summary>
/// 用户令牌代号的进程内短缓存（D7 epoch 守卫）。请求路径不必每个请求都查库；
/// 吊销路径 bump 后本副本立即失效缓存，其它副本（理论上不存在，单副本硬约束）最迟在一个 TTL 后收敛。
/// </summary>
public sealed class UserEpochCache
{
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>构造。</summary>
    /// <param name="clock">时钟。</param>
    /// <param name="ttl">缓存时长；非正数表示完全不缓存（集成测试用）。</param>
    public UserEpochCache(IClock clock, TimeSpan ttl)
    {
        _clock = clock;
        _ttl = ttl;
    }

    /// <summary>尝试读取缓存的代号；过期项会被顺手移除。</summary>
    public bool TryGet(Guid userId, out int epoch)
    {
        epoch = 0;
        if (_ttl <= TimeSpan.Zero || !_entries.TryGetValue(userId, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= _clock.UtcNow)
        {
            _entries.TryRemove(userId, out _);
            return false;
        }

        epoch = entry.Epoch;
        return true;
    }

    /// <summary>写入缓存；<see cref="TimeSpan.Zero"/> 或负 TTL 时忽略。</summary>
    public void Set(Guid userId, int epoch)
    {
        if (_ttl <= TimeSpan.Zero)
        {
            return;
        }

        _entries[userId] = new Entry(epoch, _clock.UtcNow + _ttl);
    }

    /// <summary>失效指定用户；bump 成功后由吊销路径调用，保证本副本即时生效。</summary>
    public void Invalidate(Guid userId) => _entries.TryRemove(userId, out _);

    private readonly record struct Entry(int Epoch, DateTimeOffset ExpiresAt);
}
