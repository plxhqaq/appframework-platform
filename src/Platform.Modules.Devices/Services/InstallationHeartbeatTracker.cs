using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Modules.Devices.Options;

namespace Platform.Modules.Devices.Services;

/// <summary>
/// 心跳内存态登记表：安装实例最近一次心跳后的完整概要、心跳时刻与待回写标记。
/// 心跳命中本表时不再读写数据库；状态三值（含报警摘要）跃迁走单行写穿，
/// 仅心跳时间由 <see cref="HeartbeatFlushWorker"/> 按周期批量回库。
/// </summary>
/// <remarks>
/// 单实例部署（advisory lock）下本表即在线设备心跳时刻的事实来源。
/// 条目在注册、断开离线、离线扫描与管理端改名/停用处失效，下次心跳回源查库重播，
/// 另以 <see cref="DevicesOptions.HeartbeatCacheTtlSeconds"/> 兜底防止未清理条目的陈旧与滞留。
/// </remarks>
public sealed class InstallationHeartbeatTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly IClock _clock;
    private readonly DevicesOptions _options;

    /// <summary>构造。</summary>
    public InstallationHeartbeatTracker(IClock clock, IOptions<DevicesOptions> options)
    {
        _clock = clock;
        _options = options.Value;
    }

    /// <summary>取缓存条目；应用键不匹配或超过 TTL 视为未命中（并清理过期条目）。</summary>
    public bool TryGet(string appKey, Guid installationId, out Entry entry)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(installationId, out entry!))
            {
                return false;
            }

            if (!string.Equals(entry.AppKey, appKey, StringComparison.Ordinal) || Expired(entry))
            {
                RemoveCore(installationId);
                entry = null!;
                return false;
            }

            return true;
        }
    }

    /// <summary>用落库后的概要重播条目；<paramref name="timestampPersisted"/> 表示心跳时间已写库。</summary>
    public void Seed(
        string appKey,
        Guid installationId,
        Guid nodeId,
        InstallationSummaryDto summary,
        DateTimeOffset lastHeartbeatAt,
        bool timestampPersisted)
    {
        lock (_gate)
        {
            RemoveCore(installationId);
            _entries[installationId] = new Entry(
                appKey, nodeId, summary, lastHeartbeatAt,
                Dirty: !timestampPersisted,
                DirtyVersion: 0,
                SeededAt: _clock.UtcNow);
        }
    }

    /// <summary>命中后刷新条目：概要、心跳时刻与待回写标记（写穿落库则清除标记并抬版本）。</summary>
    public void Touch(
        Guid installationId,
        InstallationSummaryDto summary,
        DateTimeOffset lastHeartbeatAt,
        bool timestampPersisted)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(installationId, out var entry))
            {
                return;
            }

            _entries[installationId] = timestampPersisted
                ? entry with
                {
                    Summary = summary,
                    LastHeartbeatAt = lastHeartbeatAt,
                    Dirty = false,
                    DirtyVersion = entry.DirtyVersion + 1,
                }
                : entry with { Summary = summary, LastHeartbeatAt = lastHeartbeatAt, Dirty = true };
        }
    }

    /// <summary>失效条目（注册、断开、离线标记、管理端改名/停用）。</summary>
    public void Remove(Guid installationId)
    {
        lock (_gate)
        {
            RemoveCore(installationId);
        }
    }

    /// <summary>批量失效。</summary>
    public void RemoveRange(IReadOnlyCollection<Guid> installationIds)
    {
        lock (_gate)
        {
            foreach (var id in installationIds)
            {
                RemoveCore(id);
            }
        }
    }

    /// <summary>快照待回写条目（id、所属节点、版本、心跳时刻）。</summary>
    public IReadOnlyList<DirtyEntry> SnapshotDirty()
    {
        lock (_gate)
        {
            var list = new List<DirtyEntry>();
            foreach (var (id, entry) in _entries)
            {
                if (entry.Dirty)
                {
                    list.Add(new DirtyEntry(id, entry.NodeId, entry.DirtyVersion, entry.LastHeartbeatAt));
                }
            }

            return list;
        }
    }

    /// <summary>回写成功后清除标记；期间有新心跳（版本已变）则保留，等待下一批。</summary>
    public void MarkFlushed(IReadOnlyList<DirtyEntry> batch)
    {
        lock (_gate)
        {
            foreach (var dirty in batch)
            {
                if (_entries.TryGetValue(dirty.InstallationId, out var entry)
                    && entry.Dirty
                    && entry.DirtyVersion == dirty.DirtyVersion)
                {
                    _entries[dirty.InstallationId] = entry with { Dirty = false };
                }
            }
        }
    }

    /// <summary>清理超过 TTL 的条目；返回清理数。</summary>
    public int SweepExpired()
    {
        lock (_gate)
        {
            List<Guid>? expired = null;
            foreach (var (id, entry) in _entries)
            {
                if (Expired(entry))
                {
                    (expired ??= []).Add(id);
                }
            }

            if (expired is null)
            {
                return 0;
            }

            foreach (var id in expired)
            {
                RemoveCore(id);
            }

            return expired.Count;
        }
    }

    /// <summary>当前条目数（诊断/测试用）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private bool Expired(Entry entry) =>
        _options.HeartbeatCacheTtlSeconds > 0
        && _clock.UtcNow - entry.SeededAt > TimeSpan.FromSeconds(_options.HeartbeatCacheTtlSeconds);

    private void RemoveCore(Guid installationId) => _entries.Remove(installationId);

    /// <summary>缓存条目（不可变，替换写入）。</summary>
    public sealed record Entry(
        string AppKey,
        Guid NodeId,
        InstallationSummaryDto Summary,
        DateTimeOffset LastHeartbeatAt,
        bool Dirty,
        int DirtyVersion,
        DateTimeOffset SeededAt);

    /// <summary>待回写快照。</summary>
    public sealed record DirtyEntry(
        Guid InstallationId,
        Guid NodeId,
        int DirtyVersion,
        DateTimeOffset LastHeartbeatAt);
}
