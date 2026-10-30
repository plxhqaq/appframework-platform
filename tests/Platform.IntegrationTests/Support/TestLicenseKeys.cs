using System.Security.Cryptography;
using Platform.Contracts.Licensing;

namespace Platform.IntegrationTests.Support;

/// <summary>集成测试用签发密钥对：进程内生成一次，私钥注入宿主配置，公钥用于验签断言。</summary>
internal static class TestLicenseKeys
{
    private static readonly Lazy<KeyPair> Pair = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>注入 <c>Licensing:SigningKeyPkcs8</c> 的私钥。</summary>
    public static string PrivateKeyPkcs8Base64 => Pair.Value.PrivateKey;

    /// <summary>验签用公钥。</summary>
    public static string PublicKeySpkiBase64 => Pair.Value.PublicKey;

    /// <summary>当前公钥对应的密钥 id。</summary>
    public static string KeyId => Pair.Value.KeyId;

    private static KeyPair Create()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = LicenseCryptography.ExportPublicKeySpkiBase64(key);
        return new KeyPair(
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            publicKey,
            LicenseCryptography.ComputeKeyId(publicKey));
    }

    private sealed record KeyPair(string PrivateKey, string PublicKey, string KeyId);
}
