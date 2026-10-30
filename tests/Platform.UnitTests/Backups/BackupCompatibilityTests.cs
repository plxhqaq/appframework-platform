using Platform.Modules.Backups.Services;

namespace Platform.UnitTests.Backups;

/// <summary>兼容矩阵：同渠道硬门禁 + 主版本兼容（force 只放宽主版本）。</summary>
public sealed class BackupCompatibilityTests
{
    [Theory]
    [InlineData("stable", "1.4.0", "stable", "1.9.2", false, true)]
    [InlineData("stable", "1.4.0", "stable", "1.9.2", true, true)]
    [InlineData(null, null, null, null, false, false)]
    [InlineData("stable", "1.4.0", "stable", "2.0.0", false, false)]
    [InlineData("stable", "1.4.0", "stable", "2.0.0", true, true)]
    [InlineData("stable", "1.4.0", "beta", "1.4.0", true, false)]
    [InlineData("stable", "1.4.0", "beta", "1.4.0", false, false)]
    [InlineData("stable", "1.4.0", "stable", null, false, false)]
    public void 渠道与主版本门禁符合矩阵(
        string? backupChannel,
        string? backupVersion,
        string? targetChannel,
        string? targetVersion,
        bool force,
        bool expectedCompatible)
    {
        var result = BackupCompatibilityRules.Evaluate(
            backupChannel, backupVersion, targetChannel, targetVersion, force);

        Assert.Equal(expectedCompatible, result.Compatible);
        if (!expectedCompatible)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        }
    }

    [Fact]
    public void 跨渠道即便强制也给出渠道原因()
    {
        var result = BackupCompatibilityRules.Evaluate("stable", "1.0.0", "beta", "1.0.0", force: true);

        Assert.False(result.Compatible);
        Assert.StartsWith("渠道不同", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.4.0", 1)]
    [InlineData("12", 12)]
    [InlineData("v1.4.0", null)]
    [InlineData("-1.0.0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 主版本解析只认点分第一段数字(string? version, int? expected)
    {
        Assert.Equal(expected, BackupCompatibilityRules.Major(version));
    }
}
