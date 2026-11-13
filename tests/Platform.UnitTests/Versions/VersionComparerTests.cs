using Platform.Contracts.Versions;

namespace Platform.UnitTests.Versions;

/// <summary>版本语义比较：三段/四段兼容、预发布优先级与构建元数据忽略。</summary>
public sealed class VersionComparerTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.3.0", 0)]
    [InlineData("1.9.0", "1.10.0", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1.0.0.225", "1.0.0.226", -1)]
    [InlineData("1.0.0", "1.0.0.1", -1)]
    [InlineData("1.2.3-rc.1", "1.2.3", -1)]
    [InlineData("1.2.3", "1.2.3-rc.1", 1)]
    [InlineData("1.2.3-alpha", "1.2.3-alpha.1", -1)]
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha.beta", -1)]
    [InlineData("1.2.3-alpha.beta", "1.2.3-beta", -1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.11", -1)]
    [InlineData("1.2.3-1", "1.2.3-alpha", -1)]
    [InlineData("1.2.3-rc.1+build.1", "1.2.3-rc.1+build.2", 0)]
    [InlineData("1.2.3+build.9", "1.2.3", 0)]
    [InlineData("  1.2.3  ", "1.2.3", 0)]
    public void 版本比较符合语义(string left, string right, int expected)
    {
        Assert.True(VersionComparer.TryCompare(left, right, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-")]
    public void 非法版本解析失败(string? value) =>
        Assert.False(VersionComparer.TryParse(value, out _));

    [Fact]
    public void 任一版本非法时直接比较失败()
    {
        Assert.False(VersionComparer.TryCompare("1.2.3", "bad", out _));
        Assert.False(VersionComparer.TryCompare(null, "1.2.3", out _));
    }

    [Fact]
    public void 解析结果保留四段与后缀()
    {
        Assert.True(VersionComparer.TryParse("1.2.3.4-rc.1+build.7", out var version));
        Assert.Equal(1, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Patch);
        Assert.Equal(4, version.Revision);
        Assert.Equal("rc.1", version.Prerelease);
        Assert.Equal("build.7", version.BuildMetadata);
        Assert.Equal("1.2.3.4-rc.1+build.7", version.ToString());
    }

    [Fact]
    public void 三段版本补零后等价()
    {
        Assert.True(VersionComparer.TryParse("1.2.3", out var three));
        Assert.True(VersionComparer.TryParse("1.2.3.0", out var four));
        Assert.Equal(0, three.Revision);
        Assert.Equal(0, VersionComparer.Compare(three, four));
        Assert.Equal("1.2.3", three.ToString());
    }
}
