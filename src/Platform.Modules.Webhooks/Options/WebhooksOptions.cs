namespace Platform.Modules.Webhooks.Options;

/// <summary>Webhook 投递配置。</summary>
public sealed class WebhooksOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Webhooks";

    /// <summary>首次重试延迟（秒），之后按 2 的幂递增。</summary>
    public int BaseDelaySeconds { get; set; } = 30;

    /// <summary>最大重试延迟（秒）。</summary>
    public int MaxDelaySeconds { get; set; } = 3600;

    /// <summary>最大投递次数（含首次）。</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>单次 HTTP 投递超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>后台投递扫描间隔（秒）。</summary>
    public int DispatchIntervalSeconds { get; set; } = 15;

    /// <summary>每轮最多处理的投递条数。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>是否启用后台投递（集成测试可关闭）。</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>终态（succeeded/dead）投递记录保留天数；pending/failed 属于重试队列，不在此清理。</summary>
    public int DeliveryRetentionDays { get; set; } = 30;

    /// <summary>
    /// 允许的内网目标（主机名、<c>*.example.com</c> 通配子域、IP 或 CIDR），逗号/分号分隔。
    /// 用字符串而不是数组，环境变量（<c>Webhooks__AllowedHosts</c>）才能直接整串注入。
    /// 未列入的环回/私网/链路本地地址一律拒绝。
    /// </summary>
    public string AllowedHosts { get; set; } = string.Empty;

    /// <summary>规范化后的投递次数上限。</summary>
    public int EffectiveMaxAttempts => Math.Max(1, MaxAttempts);

    /// <summary>按尝试次数计算退避秒数：<c>base * 2^(attempts-1)</c>，封顶 <see cref="MaxDelaySeconds"/>。</summary>
    public int DelaySeconds(int completedAttempts)
    {
        var attempt = Math.Max(1, completedAttempts);
        var exponent = Math.Min(attempt - 1, 16);
        var seconds = (long)Math.Max(1, BaseDelaySeconds) * (1L << exponent);
        return (int)Math.Min(Math.Max(1, MaxDelaySeconds), seconds);
    }
}
