namespace AppFramework.Platform.Client;

/// <summary>
/// <see cref="PlatformClient"/> 的配置。
/// </summary>
public sealed class PlatformClientOptions
{
    /// <summary>平台地址，例如 <c>http://localhost:5080</c>（不含 <c>/api/v1</c>）。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>客户端版本号，写入 <c>X-Client-Version</c>，供服务端最低版本门禁使用。</summary>
    public string? ClientVersion { get; init; }

    /// <summary>运行时标识（<c>win-x64</c> 等）；检查更新未显式传入时取这里。</summary>
    public string? RuntimeIdentifier { get; init; }

    /// <summary>请求超时；下载大文件时按需要放宽。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
