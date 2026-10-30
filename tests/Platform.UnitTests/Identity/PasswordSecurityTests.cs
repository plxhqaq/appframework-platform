using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Security;

namespace Platform.UnitTests.Identity;

/// <summary>
/// Argon2id 编码必须把参数写进哈希本身：否则调一次全局参数，存量账号就全部登不进来。
/// 口令策略只做长度与字符类别校验，不追求特殊字符。
/// </summary>
public sealed class PasswordSecurityTests
{
    private static readonly Argon2Options FastOptions = new()
    {
        DegreeOfParallelism = 1,
        MemorySizeKiB = 8192,
        Iterations = 1,
        HashLength = 32,
        SaltLength = 16,
    };

    [Fact]
    public void 哈希与校验往返一致且拒绝错误口令()
    {
        var hasher = CreateHasher(FastOptions);

        var encoded = hasher.Hash("Admin@12345").Encoded;

        Assert.StartsWith("$argon2id$v=19$m=8192,t=1,p=1$", encoded, StringComparison.Ordinal);
        Assert.True(hasher.Verify("Admin@12345", encoded));
        Assert.False(hasher.Verify("Admin@12346", encoded));
    }

    [Fact]
    public void 相同口令每次哈希不同()
    {
        var hasher = CreateHasher(FastOptions);

        Assert.NotEqual(hasher.Hash("Admin@12345").Encoded, hasher.Hash("Admin@12345").Encoded);
    }

    [Fact]
    public void 调整参数后旧哈希仍能校验并提示重算()
    {
        var oldHasher = CreateHasher(FastOptions);
        var encoded = oldHasher.Hash("Admin@12345").Encoded;

        var newHasher = CreateHasher(new Argon2Options
        {
            DegreeOfParallelism = 2,
            MemorySizeKiB = 16384,
            Iterations = 2,
            HashLength = 32,
            SaltLength = 16,
        });

        // 校验按哈希内嵌参数重算，因此旧口令不受全局调参影响
        Assert.True(newHasher.Verify("Admin@12345", encoded));
        Assert.True(newHasher.NeedsRehash(encoded));
        Assert.False(oldHasher.NeedsRehash(encoded));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=1,t=1,p=1$AAAA$AAAA")]
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$not-base64$not-base64")]
    public void 非法哈希一律校验失败(string encoded)
    {
        var hasher = CreateHasher(FastOptions);

        Assert.False(hasher.Verify("Admin@12345", encoded));
        Assert.True(hasher.NeedsRehash(encoded));
    }

    [Fact]
    public void 非法参数在构造时即拒绝()
    {
        var invalid = new Argon2Options { DegreeOfParallelism = 8, MemorySizeKiB = 32 };

        var exception = Assert.Throws<InvalidOperationException>(() => CreateHasher(invalid));
        Assert.Contains("Argon2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Abcdef12")]
    [InlineData("a1bcdefghijklmnopqrstuvwxyz")]
    public void 合规口令通过策略(string password) => PasswordPolicy.EnsureValid(password);

    [Theory]
    [InlineData("")]
    [InlineData("Ab1")]
    [InlineData("abcdefgh")]
    [InlineData("12345678")]
    public void 不合规口令抛策略错误(string password)
    {
        var exception = Assert.Throws<BusinessException>(() => PasswordPolicy.EnsureValid(password));
        Assert.Equal(ApiErrorCode.IdentityPasswordPolicyViolation, exception.Code);
    }

    [Fact]
    public void 超长口令被拒绝()
    {
        var password = "a1" + new string('x', IdentityConventions.PasswordMaxLength);

        var exception = Assert.Throws<BusinessException>(() => PasswordPolicy.EnsureValid(password));
        Assert.Equal(ApiErrorCode.IdentityPasswordPolicyViolation, exception.Code);
    }

    private static Argon2PasswordHasher CreateHasher(Argon2Options options) =>
        new(Options.Create(options));
}
