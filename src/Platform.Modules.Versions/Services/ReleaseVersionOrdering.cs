using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Services;

/// <summary>发布版本的统一排序口径：先按语义版本号，同版本再按发布时间。</summary>
internal static class ReleaseVersionOrdering
{
    /// <summary>比较两个版本（含发布时间兜底）；正数表示 <paramref name="left"/> 更新。</summary>
    public static int Compare(
        string? leftVersion,
        DateTimeOffset? leftPublishedAt,
        string? rightVersion,
        DateTimeOffset? rightPublishedAt)
    {
        var result = VersionComparer.TryCompare(leftVersion, rightVersion, out var versionResult)
            ? versionResult
            : string.CompareOrdinal(leftVersion ?? string.Empty, rightVersion ?? string.Empty);

        return result != 0 ? result : Nullable.Compare(leftPublishedAt, rightPublishedAt);
    }
}
