using System.Diagnostics;

namespace Platform.Api.Diagnostics;

/// <summary>
/// 进程级指标采样：后台按固定间隔更新一次，仪表盘只读最近一次快照。
/// </summary>
/// <remarks>
/// CPU 占用率必须用两次采样的增量计算；放在请求里做会让每个轮询都阻塞一个采样窗口。
/// 内存比例以 <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> 为分母，容器限额下同样成立。
/// </remarks>
public sealed class SystemMetricsSampler : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly long _totalMemoryBytes = Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    private SystemMetricsSnapshot _current;

    /// <summary>构造：先给出一个可用的初始快照，避免首个请求拿到全零。</summary>
    public SystemMetricsSampler()
    {
        using var process = Process.GetCurrentProcess();
        _current = new SystemMetricsSnapshot(
            DateTimeOffset.UtcNow,
            GetStartTime(process),
            CpuUsagePercent: 0,
            MemoryUsagePercent: Percent(process.WorkingSet64, _totalMemoryBytes),
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            _totalMemoryBytes);
    }

    /// <summary>最近一次采样。</summary>
    public SystemMetricsSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var process = Process.GetCurrentProcess();
        var previousCpu = process.TotalProcessorTime;
        var previousAt = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                process.Refresh();
                var now = DateTimeOffset.UtcNow;
                var cpu = process.TotalProcessorTime;
                var elapsed = now - previousAt;

                var cpuPercent = elapsed.TotalMilliseconds <= 0
                    ? 0
                    : Math.Clamp(
                        (cpu - previousCpu).TotalMilliseconds
                        / (elapsed.TotalMilliseconds * Math.Max(1, Environment.ProcessorCount))
                        * 100,
                        0,
                        100);
                previousCpu = cpu;
                previousAt = now;

                var workingSet = process.WorkingSet64;
                var snapshot = new SystemMetricsSnapshot(
                    now,
                    GetStartTime(process),
                    Math.Round(cpuPercent, 1),
                    Percent(workingSet, _totalMemoryBytes),
                    workingSet,
                    GC.GetTotalMemory(forceFullCollection: false),
                    _totalMemoryBytes);

                lock (_gate)
                {
                    _current = snapshot;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主停止：正常退出循环。
        }
    }

    private static DateTimeOffset GetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // 个别受限环境不暴露进程启动时间；退回当前时间，仪表盘仍可用。
            return DateTimeOffset.UtcNow;
        }
    }

    private static double Percent(long used, long total) =>
        Math.Round(Math.Clamp(used * 100d / total, 0, 100), 1);
}

/// <summary>一次进程指标采样。</summary>
public sealed record SystemMetricsSnapshot(
    DateTimeOffset SampledAt,
    DateTimeOffset StartedAt,
    double CpuUsagePercent,
    double MemoryUsagePercent,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long TotalMemoryBytes);
