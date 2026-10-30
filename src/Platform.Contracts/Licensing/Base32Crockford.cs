using System.Text;

namespace Platform.Contracts.Licensing;

/// <summary>
/// Crockford Base32 编解码（机器码与离线授权码专用）。
/// <para>
/// 字母表刻意剔除 I / L / O / U：现场口述或手抄设备码时不会把 1/0 认错。
/// 解码侧忽略分隔符与大小写，并把 I/L 归一为 1、O 归一为 0；
/// 末尾不足 8 位的填充位必须为 0，否则视为截断/篡改。
/// </para>
/// </summary>
public static class Base32Crockford
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>编码为 Crockford Base32（大写、无分隔符）。</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        int charCount = (data.Length * 8 + 4) / 5;
        char[] chars = new char[charCount];
        int buffer = 0;
        int bits = 0;
        int index = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[index++] = Alphabet[(buffer >> bits) & 0x1F];
            }
        }

        if (bits > 0)
        {
            chars[index++] = Alphabet[(buffer << (5 - bits)) & 0x1F];
        }

        return new string(chars, 0, index);
    }

    /// <summary>
    /// 解码 Crockford Base32。忽略 '-'、空格、下划线等分隔符与大小写；
    /// 含非法字符时返回 false（不做静默丢弃，避免把错码解成另一个有效码）。
    /// </summary>
    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var bytes = new List<byte>(text.Length * 5 / 8 + 1);
        int buffer = 0;
        int bits = 0;

        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);
            if (c is '-' or '_' or ' ')
            {
                continue;
            }

            c = c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => c,
            };

            int value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        if (bits > 0 && (buffer & ((1 << bits) - 1)) != 0)
        {
            return false;
        }

        data = [.. bytes];
        return true;
    }

    /// <summary>规范化粘贴输入：剥离一切分隔符与空白并转大写。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>按 <paramref name="groupSize"/> 分组展示（如 8 字符一组），只用于人读，不参与存储。</summary>
    public static string FormatGroups(string? text, int groupSize = 8)
    {
        if (string.IsNullOrEmpty(text) || groupSize <= 0)
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length + text.Length / groupSize);
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0 && i % groupSize == 0)
            {
                builder.Append('-');
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}
