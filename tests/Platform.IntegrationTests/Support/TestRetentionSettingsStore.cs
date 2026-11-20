using Platform.Kernel.Abstractions;

namespace Platform.IntegrationTests.Support;

/// <summary>内存版宽限期配置：按模块键存分钟数，未配置返回 null。</summary>
internal sealed class TestRetentionSettingsStore : IRetentionSettingsStore
{
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    public void Set(string moduleKey, int minutes) => _values[moduleKey] = minutes;

    public Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(moduleKey, out var minutes) ? (int?)minutes : null);

    public Task SetGraceMinutesAsync(
        IReadOnlyDictionary<string, int> minutesByModule,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        foreach (var pair in minutesByModule)
        {
            _values[pair.Key] = pair.Value;
        }

        return Task.CompletedTask;
    }
}
