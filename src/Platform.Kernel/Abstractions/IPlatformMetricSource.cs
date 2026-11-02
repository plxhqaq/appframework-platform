using Platform.Kernel.Metrics;

namespace Platform.Kernel.Abstractions;

/// <summary>
/// 业务指标数据源：模块把自己关心的积压/失败量在抓取时写成仪表值。
/// </summary>
/// <remarks>
/// 只在 <c>/metrics</c> 被请求时执行，因此实现必须是一条能跑的聚合查询，
/// 不要在这里做遍历全表的应用层计算。采集异常会被宿主记入
/// <see cref="PlatformMetricNames.MetricSourceErrors"/> 并跳过该源，
/// 一个模块的库查询失败不能让整个抓取端失效。
/// </remarks>
public interface IPlatformMetricSource
{
    /// <summary>数据源名，作为错误计数的标签值（小写短横线，如 <c>webhooks</c>）。</summary>
    string Name { get; }

    /// <summary>采集并写入仪表值。</summary>
    Task CollectAsync(PlatformMetrics metrics, CancellationToken cancellationToken = default);
}
