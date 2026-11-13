namespace Platform.Kernel.Http;

/// <summary>
/// 请求体上限的公共口径：端点 <c>RequestSizeLimit</c>、<c>FormOptions</c> 与模块业务阈值共用，
/// 避免"业务允许 200MB、宿主默认 128MB/30MB 先拒"的错配。
/// </summary>
public static class RequestLimits
{
    /// <summary>multipart/form-data 的边界、头字段与普通表单字段预留开销（字节）。</summary>
    public const long MultipartOverheadBytes = 8L * 1024 * 1024;

    /// <summary>JSON 请求体（字段转义、信封与包装）预留开销（字节）。</summary>
    public const long JsonOverheadBytes = 8L * 1024 * 1024;
}
