using System.Diagnostics;
using System.Globalization;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;

namespace Platform.Kernel.Diagnostics;

/// <summary>
/// 进程指标：内存、GC、线程池与启动时刻。
/// </summary>
/// <remarks>
/// <see cref="PlatformMetricNames.ProcessStartTime"/> 让采集端能用
/// <c>changes(...)</c> 发现副本重启——多副本部署时这是判断「谁在反复崩」的最快线索。
/// </remarks>
public sealed class ProcessMetricSource : IPlatformMetricSource
{
    private static readonly DateTime StartTimeUtc = DateTime.UtcNow;

    /// <summary>单调时钟起点：DateTime.UtcNow 精度约 1ms 且会回拨，直接相减在刚启动时会算出负数。</summary>
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public string Name => "process";

    /// <inheritdoc />
    public Task CollectAsync(PlatformMetrics metrics, CancellationToken cancellationToken = default)
    {
        using var process = Process.GetCurrentProcess();
        var uptimeSeconds = (Stopwatch.GetTimestamp() - StartTimestamp) / (double)Stopwatch.Frequency;

        metrics.Set(
            PlatformMetricNames.ProcessStartTime,
            PlatformMetricNames.ProcessStartTimeHelp,
            new DateTimeOffset(StartTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        metrics.Set(PlatformMetricNames.ProcessUptime, PlatformMetricNames.ProcessUptimeHelp, uptimeSeconds);
        metrics.Set(
            PlatformMetricNames.ProcessMemory,
            PlatformMetricNames.ProcessMemoryHelp,
            GC.GetTotalMemory(forceFullCollection: false));

        for (var gen = 0; gen <= GC.MaxGeneration; gen++)
        {
            metrics.Set(
                PlatformMetricNames.ProcessGcCollections,
                PlatformMetricNames.ProcessGcCollectionsHelp,
                GC.CollectionCount(gen),
                new Dictionary<string, string> { ["generation"] = gen.ToString(CultureInfo.InvariantCulture) });
        }

        ThreadPool.GetAvailableThreads(out var workerAvailable, out var ioAvailable);
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
        metrics.Set(
            PlatformMetricNames.ProcessThreads,
            PlatformMetricNames.ProcessThreadsHelp,
            workerMax - workerAvailable,
            new Dictionary<string, string> { ["kind"] = "worker_busy" });
        metrics.Set(
            PlatformMetricNames.ProcessThreads,
            PlatformMetricNames.ProcessThreadsHelp,
            ioMax - ioAvailable,
            new Dictionary<string, string> { ["kind"] = "io_busy" });
        metrics.Set(
            PlatformMetricNames.ProcessThreads,
            PlatformMetricNames.ProcessThreadsHelp,
            process.HandleCount,
            new Dictionary<string, string> { ["kind"] = "handles" });

        return Task.CompletedTask;
    }
}
