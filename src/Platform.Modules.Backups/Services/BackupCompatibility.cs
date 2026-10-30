using Platform.Contracts.Backups;

namespace Platform.Modules.Backups.Services;

/// <summary>兼容矩阵：同渠道是硬门禁（<c>force</c> 不放宽），主版本在非强制时须一致。</summary>
public static class BackupCompatibilityRules
{
    /// <summary>评估备份能否下发到目标。</summary>
    /// <param name="backupChannel">备份渠道。</param>
    /// <param name="backupFrameworkVersion">备份框架版本。</param>
    /// <param name="targetChannel">目标渠道；空按默认渠道。</param>
    /// <param name="targetFrameworkVersion">目标框架版本；空视为不可判定。</param>
    /// <param name="force">是否放宽主版本；渠道门禁不受影响。</param>
    public static BackupCompatibility Evaluate(
        string? backupChannel,
        string? backupFrameworkVersion,
        string? targetChannel,
        string? targetFrameworkVersion,
        bool force)
    {
        var backup = BackupConventions.NormalizeChannel(backupChannel);
        var target = BackupConventions.NormalizeChannel(targetChannel);
        if (!string.Equals(backup, target, StringComparison.OrdinalIgnoreCase))
        {
            return new BackupCompatibility(
                false,
                $"渠道不同：备份[{backup}] vs 目标[{target}]（渠道不允许强制跨用）");
        }

        if (force)
        {
            return new BackupCompatibility(true, null);
        }

        var backupMajor = Major(backupFrameworkVersion);
        var targetMajor = Major(targetFrameworkVersion);
        if (backupMajor is not null && targetMajor is not null && backupMajor == targetMajor)
        {
            return new BackupCompatibility(true, null);
        }

        return new BackupCompatibility(
            false,
            $"主版本不同：备份[{backupFrameworkVersion}] vs 目标[{targetFrameworkVersion}]（可显式 force 放宽）");
    }

    /// <summary>取版本号主段（点分第一段）；无法解析返回 <c>null</c>。</summary>
    public static int? Major(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var dot = version.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? version : version[..dot];
        return int.TryParse(head, out var major) && major >= 0 ? major : null;
    }
}
