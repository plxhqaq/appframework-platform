using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Identity.Security;

/// <summary>口令编码结果。参数已内嵌在 <see cref="Encoded"/> 中，调用方只需存一个字段。</summary>
public readonly record struct EncodedPasswordHash(string Encoded, bool NeedsRehash);

/// <summary>
/// Argon2id 口令哈希。编码格式 <c>$argon2id$v=19$m=65536,t=4,p=8$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>，
/// 盐与参数都随哈希存储，因此调参只影响新哈希。
/// </summary>
public interface IPasswordHasher
{
    /// <summary>用当前参数哈希口令。</summary>
    EncodedPasswordHash Hash(string password);

    /// <summary>用该条哈希记录的参数校验；格式非法一律失败。</summary>
    bool Verify(string password, string encoded);

    /// <summary>该哈希是否需要用当前参数重算。</summary>
    bool NeedsRehash(string encoded);
}

/// <summary>Argon2id 实现。</summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const string Algorithm = "argon2id";
    private const int Version = 19;

    private readonly Argon2Options _options;

    /// <summary>构造即校验参数，避免"配置写错 → 所有人登不进来"。</summary>
    public Argon2PasswordHasher(IOptions<Argon2Options> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (!_options.IsValid())
        {
            throw new InvalidOperationException(
                $"Argon2 配置非法：m={_options.MemorySizeKiB}, t={_options.Iterations}, "
                + $"p={_options.DegreeOfParallelism}, l={_options.HashLength}。要求各值大于 0 且 m ≥ 8p。");
        }
    }

    /// <inheritdoc />
    public EncodedPasswordHash Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(_options.SaltLength);
        var hash = Derive(
            password,
            salt,
            _options.DegreeOfParallelism,
            _options.MemorySizeKiB,
            _options.Iterations,
            _options.HashLength);

        return new EncodedPasswordHash(Encode(salt, hash), false);
    }

    /// <inheritdoc />
    public bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || !TryParse(encoded, out var parsed))
        {
            return false;
        }

        var computed = Derive(
            password,
            parsed.Salt,
            parsed.Parallelism,
            parsed.MemoryKiB,
            parsed.Iterations,
            parsed.Hash.Length);

        return computed.Length == parsed.Hash.Length
               && CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
    }

    /// <inheritdoc />
    public bool NeedsRehash(string encoded) =>
        !TryParse(encoded, out var parsed)
        || parsed.Parallelism != _options.DegreeOfParallelism
        || parsed.MemoryKiB != _options.MemorySizeKiB
        || parsed.Iterations != _options.Iterations
        || parsed.Hash.Length != _options.HashLength;

    private string Encode(byte[] salt, byte[] hash) =>
        $"${Algorithm}$v={Version}$m={_options.MemorySizeKiB},t={_options.Iterations},p={_options.DegreeOfParallelism}"
        + $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

    private static byte[] Derive(
        string password,
        byte[] salt,
        int parallelism,
        int memoryKiB,
        int iterations,
        int hashLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKiB,
            Iterations = iterations,
        };
        return argon2.GetBytes(hashLength);
    }

    private static bool TryParse(string? encoded, out ParsedHash parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(encoded) || !encoded.StartsWith('$'))
        {
            return false;
        }

        var parts = encoded.Split('$');
        // ["", "argon2id", "v=19", "m=...,t=...,p=...", salt, hash]
        if (parts.Length != 6
            || !string.Equals(parts[1], Algorithm, StringComparison.Ordinal)
            || !string.Equals(parts[2], $"v={Version}", StringComparison.Ordinal))
        {
            return false;
        }

        int memory = 0, iterations = 0, parallelism = 0;
        foreach (var item in parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2 || !int.TryParse(pair[1], out var value) || value <= 0)
            {
                return false;
            }

            switch (pair[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        if (memory < 8 * parallelism || iterations <= 0 || parallelism <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] hash;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            hash = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || hash.Length == 0)
        {
            return false;
        }

        parsed = new ParsedHash(salt, hash, memory, iterations, parallelism);
        return true;
    }

    private readonly record struct ParsedHash(
        byte[] Salt,
        byte[] Hash,
        int MemoryKiB,
        int Iterations,
        int Parallelism);
}
