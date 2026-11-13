using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Kernel.Errors;
using Platform.Modules.Versions.Services;

namespace Platform.UnitTests.Versions;

/// <summary>受众标签的规范化与自动渠道键推导。</summary>
public sealed class AudienceTagRulesTests
{
    private static readonly string[] ExpectedTags = ["hardware:scanner", "win-x64"];

    [Fact]
    public void 规范化会去空白转小写去重并排序()
    {
        var tags = AudienceTagRules.Normalize([" Win-x64 ", "hardware:scanner", "WIN-X64", "win-x64"]);

        Assert.Equal(ExpectedTags, tags);
    }

    [Fact]
    public void 空输入得到空集合()
    {
        Assert.Empty(AudienceTagRules.Normalize(null));
        Assert.Empty(AudienceTagRules.Normalize([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-win")]
    [InlineData("win x64")]
    [InlineData("win/x64")]
    [InlineData("标签")]
    public void 非法标签抛四百(string tag)
    {
        var exception = Assert.Throws<BusinessException>(() => AudienceTagRules.Normalize([tag]));

        Assert.Equal(ApiErrorCode.ValidationFailed, exception.Code);
    }

    [Fact]
    public void 超过上限抛四百()
    {
        var tags = Enumerable.Range(0, VersionConventions.MaxAudienceTags + 1)
            .Select(static index => $"tag-{index}")
            .ToArray();

        Assert.Throws<BusinessException>(() => AudienceTagRules.Normalize(tags));
    }

    [Fact]
    public void 自动渠道键稳定且与标签顺序无关()
    {
        var first = AudienceTagRules.DeriveKey(["hardware:scanner", "win-x64"]);
        var second = AudienceTagRules.DeriveKey(["hardware:scanner", "win-x64"]);
        var other = AudienceTagRules.DeriveKey(["win-x64"]);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.StartsWith(VersionConventions.AutoChannelKeyPrefix, first);
        Assert.Matches(VersionConventions.ChannelKeyPattern, first);
    }
}
