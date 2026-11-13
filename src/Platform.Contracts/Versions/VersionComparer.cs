using System.Globalization;
using System.Text.RegularExpressions;

namespace Platform.Contracts.Versions;

/// <summary>解析后的版本号：三段或四段数字 + 可选预发布后缀与构建元数据。</summary>
/// <param name="Major">主版本。</param>
/// <param name="Minor">次版本。</param>
/// <param name="Patch">修订号。</param>
/// <param name="Revision">第四段（GitVersion 风格，缺省为 0）。</param>
/// <param name="Prerelease">预发布后缀（不含前导 <c>-</c>），正式版为 <c>null</c>。</param>
/// <param name="BuildMetadata">构建元数据（不含前导 <c>+</c>），不参与比较。</param>
public readonly record struct ParsedVersion(
    int Major,
    int Minor,
    int Patch,
    int Revision,
    string? Prerelease,
    string? BuildMetadata)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var value = Revision > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Revision}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

        if (!string.IsNullOrEmpty(Prerelease))
        {
            value += $"-{Prerelease}";
        }

        if (!string.IsNullOrEmpty(BuildMetadata))
        {
            value += $"+{BuildMetadata}";
        }

        return value;
    }
}

/// <summary>
/// 版本语义比较：兼容三段 SemVer 与四段 GitVersion。四段按 <c>major.minor.patch.revision</c>
/// 数值比较（<c>1.2.3 == 1.2.3.0</c>）；预发布按 SemVer 优先级；<c>+build</c> 不参与比较。
/// 服务端、客户端 SDK 与更新器共用同一口径。
/// </summary>
public static class VersionComparer
{
    private static readonly Regex Pattern = new(
        VersionConventions.VersionPattern,
        RegexOptions.CultureInvariant);

    /// <summary>解析版本号；空白会被去掉，非法格式返回 <c>false</c>。</summary>
    public static bool TryParse(string? value, out ParsedVersion version)
    {
        version = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || !Pattern.IsMatch(text))
        {
            return false;
        }

        var buildIndex = text.IndexOf('+', StringComparison.Ordinal);
        var build = buildIndex >= 0 ? text[(buildIndex + 1)..] : null;
        var withoutBuild = buildIndex >= 0 ? text[..buildIndex] : text;

        string? prerelease = null;
        var prereleaseIndex = withoutBuild.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseIndex >= 0)
        {
            prerelease = withoutBuild[(prereleaseIndex + 1)..];
            withoutBuild = withoutBuild[..prereleaseIndex];
        }

        var segments = withoutBuild.Split('.');
        if (segments.Length is < 3 or > 4)
        {
            return false;
        }

        if (!TryParseSegment(segments[0], out var major)
            || !TryParseSegment(segments[1], out var minor)
            || !TryParseSegment(segments[2], out var patch))
        {
            return false;
        }

        var revision = 0;
        if (segments.Length == 4 && !TryParseSegment(segments[3], out revision))
        {
            return false;
        }

        version = new ParsedVersion(major, minor, patch, revision, prerelease, build);
        return true;
    }

    /// <summary>比较两个已解析版本；负数表示 <paramref name="left"/> 更旧。</summary>
    public static int Compare(ParsedVersion left, ParsedVersion right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0)
        {
            return result;
        }

        result = left.Minor.CompareTo(right.Minor);
        if (result != 0)
        {
            return result;
        }

        result = left.Patch.CompareTo(right.Patch);
        if (result != 0)
        {
            return result;
        }

        result = left.Revision.CompareTo(right.Revision);
        return result != 0 ? result : ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    /// <summary>直接比较两个版本字符串；任一非法返回 <c>false</c>。</summary>
    public static bool TryCompare(string? left, string? right, out int result)
    {
        result = 0;
        if (!TryParse(left, out var parsedLeft) || !TryParse(right, out var parsedRight))
        {
            return false;
        }

        result = Compare(parsedLeft, parsedRight);
        return true;
    }

    private static bool TryParseSegment(string value, out int parsed)
    {
        parsed = 0;
        return value.Length is > 0 and <= 5
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
    }

    private static int ComparePrerelease(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        // 正式版（无预发布后缀）优先于任何预发布版本。
        if (string.IsNullOrEmpty(left))
        {
            return 1;
        }

        if (string.IsNullOrEmpty(right))
        {
            return -1;
        }

        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        var shared = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
        for (var index = 0; index < shared; index++)
        {
            var result = CompareIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumeric = IsNumeric(left);
        var rightIsNumeric = IsNumeric(right);

        if (leftIsNumeric && rightIsNumeric)
        {
            return CompareNumeric(left, right);
        }

        if (leftIsNumeric)
        {
            return -1;
        }

        return rightIsNumeric ? 1 : string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareNumeric(string left, string right)
    {
        // 去掉前导零后按长度再按字典序比较，避免超长数字标识溢出。
        var leftTrimmed = left.TrimStart('0');
        var rightTrimmed = right.TrimStart('0');
        return leftTrimmed.Length != rightTrimmed.Length
            ? leftTrimmed.Length.CompareTo(rightTrimmed.Length)
            : string.CompareOrdinal(leftTrimmed, rightTrimmed);
    }
}
