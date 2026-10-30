namespace Platform.Modules.Identity.Security;

/// <summary>
/// Argon2id 参数。**必须随每条哈希一起存库**：校验时按该条哈希自己记录的参数重算，
/// 否则调整一次全局参数就会让所有既有账号无法登录。
/// </summary>
public sealed class Argon2Options
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Argon2";

    /// <summary>并行度（lanes）。</summary>
    public int DegreeOfParallelism { get; set; } = 8;

    /// <summary>内存开销（KiB）。默认 64MB；低内存部署可下调，但不得低于 8×并行度。</summary>
    public int MemorySizeKiB { get; set; } = 65536;

    /// <summary>迭代次数。</summary>
    public int Iterations { get; set; } = 4;

    /// <summary>输出字节数。</summary>
    public int HashLength { get; set; } = 64;

    /// <summary>盐字节数。</summary>
    public int SaltLength { get; set; } = 64;

    /// <summary>校验参数合法（Argon2 要求 m ≥ 8p）。</summary>
    public bool IsValid() =>
        DegreeOfParallelism > 0
        && MemorySizeKiB >= 8 * DegreeOfParallelism
        && Iterations > 0
        && HashLength >= 16
        && SaltLength >= 16;
}
