using Platform.Kernel.Metrics;

namespace Platform.UnitTests.Metrics;

/// <summary>Prometheus 文本渲染：写错格式比丢指标更糟，因为采集端只会静默判为抓取失败。</summary>
public sealed class PlatformMetricsTests
{
    [Fact]
    public void 计数器按累加渲染并带标签()
    {
        var metrics = new PlatformMetrics();
        metrics.Increment("demo_events_total", "演示计数", new Dictionary<string, string> { ["kind"] = "a" });
        metrics.Increment("demo_events_total", "演示计数", new Dictionary<string, string> { ["kind"] = "a" }, by: 4);
        metrics.Increment("demo_events_total", "演示计数", new Dictionary<string, string> { ["kind"] = "b" });

        var text = metrics.Render();

        Assert.Contains("# TYPE demo_events_total counter", text, StringComparison.Ordinal);
        Assert.Contains("demo_events_total{kind=\"a\"} 5", text, StringComparison.Ordinal);
        Assert.Contains("demo_events_total{kind=\"b\"} 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 仪表覆盖而非累加()
    {
        var metrics = new PlatformMetrics();
        metrics.Set("demo_backlog", "积压条数", 12, new Dictionary<string, string> { ["app"] = "demo" });
        metrics.Set("demo_backlog", "积压条数", 3, new Dictionary<string, string> { ["app"] = "demo" });

        Assert.Contains("demo_backlog{app=\"demo\"} 3", metrics.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void 标签值里的引号与换行被转义()
    {
        var metrics = new PlatformMetrics();
        metrics.Increment("demo_events_total", "说\"明", new Dictionary<string, string> { ["raw"] = "a\"b\nc" });

        var line = metrics.Render()
            .Split('\n')
            .Single(l => l.StartsWith("demo_events_total{", StringComparison.Ordinal));
        Assert.Contains("raw=\"a\\\"b\\nc\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void HTTP直方图按路由与状态类别分桶并累计求和()
    {
        var metrics = new PlatformMetrics();
        metrics.ObserveHttp("get", "/api/v1/admin/apps/{id:guid}", 201, 0.03);
        metrics.ObserveHttp("GET", "/api/v1/admin/apps/{id:guid}", 500, 3.0);

        var text = metrics.Render();

        Assert.Contains($"# TYPE {PlatformMetricNames.HttpRequestDuration} histogram", text, StringComparison.Ordinal);
        Assert.Contains("platform_http_request_duration_seconds_bucket{method=\"GET\",route=\"/api/v1/admin/apps/{id:guid}\",status_class=\"2xx\",le=\"0.05\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("status_class=\"5xx\",le=\"+Inf\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("status_class=\"2xx\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("_sum{method=\"GET\",route=\"/api/v1/admin/apps/{id:guid}\",status_class=\"5xx\"} 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 并发累加不丢次数()
    {
        var metrics = new PlatformMetrics();
        Parallel.For(0, 2000, i => metrics.Increment("demo_concurrent_total", "并发计数"));

        Assert.Contains("demo_concurrent_total 2000", metrics.Render(), StringComparison.Ordinal);
    }
}
