namespace Platform.Kernel.Abstractions;

/// <summary>时钟抽象。业务代码禁止直接 <c>DateTimeOffset.UtcNow</c>，否则过期/宽限期逻辑无法单测。</summary>
public interface IClock
{
    /// <summary>当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>生产实现。</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
