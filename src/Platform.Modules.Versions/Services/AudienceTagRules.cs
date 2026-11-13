using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Contracts.Versions;
using Platform.Kernel.Errors;

namespace Platform.Modules.Versions.Services;

/// <summary>受众标签的规范化、校验与自动渠道键推导。</summary>
public static partial class AudienceTagRules
{
    /// <summary>去空白、转小写、去重并按序排列；空标签或格式非法抛 400。</summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in tags)
        {
            var tag = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(tag))
            {
                throw BusinessException.Invalid("受众标签不能为空。");
            }

            if (!TagRegex().IsMatch(tag))
            {
                throw BusinessException.Invalid(
                    $"受众标签 {raw} 非法，必须匹配 {VersionConventions.AudienceTagPattern}。");
            }

            normalized.Add(tag);
        }

        if (normalized.Count > VersionConventions.MaxAudienceTags)
        {
            throw BusinessException.Invalid(
                $"受众标签最多 {VersionConventions.MaxAudienceTags} 个。");
        }

        return [.. normalized];
    }

    /// <summary>按规范化后的标签集合推导自动渠道键；同一集合永远得到同一个键。</summary>
    public static string DeriveKey(IReadOnlyList<string> normalizedTags)
    {
        ArgumentNullException.ThrowIfNull(normalizedTags);

        var canonical = string.Join('\n', normalizedTags);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return VersionConventions.AutoChannelKeyPrefix + Convert.ToHexStringLower(hash)[..8];
    }

    /// <summary>自动渠道的显示名（超长截断到渠道名上限 256）。</summary>
    public static string DeriveName(IReadOnlyList<string> normalizedTags)
    {
        var name = "自动渠道 " + string.Join(" / ", normalizedTags);
        return name.Length <= 256 ? name : name[..256];
    }

    [GeneratedRegex(VersionConventions.AudienceTagPattern, RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}
