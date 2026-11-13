using System.Buffers.Binary;
using System.Text;
using Platform.Contracts;
using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Packages;

/// <summary>
/// PE 包清单探测：读取 exe/msi 的 VERSIONINFO <c>Comments</c>，支持新 <c>AF-PACKAGE/1</c> 管道串
/// 与老 <c>AF-MANIFEST/1</c>（由适配器映射）。
/// <para>生产服务端在 Linux，解析不依赖 Windows <c>FileVersionInfo</c>。</para>
/// </summary>
public static class PePackageManifestProbe
{
    /// <summary>从可随机读取的 PE 流读取 Comments 并解析清单；无资源/非 PE 返回「缺清单」。</summary>
    public static PackageProbeResult TryRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var comments = PeVersionInfoReader.ReadStringValue(stream, "Comments");
        if (string.IsNullOrWhiteSpace(comments))
        {
            return PackageProbeResult.Missing("安装包没有 VERSIONINFO Comments（可人工登记 full 包）。");
        }

        var text = comments.Trim();
        if (text.StartsWith(PackageManifest.SchemaId + "|", StringComparison.OrdinalIgnoreCase))
        {
            return PackageManifest.TryParsePipe(
                text,
                VersionConventions.ManifestSourceEmbedded,
                out var manifest,
                out var error)
                ? PackageProbeResult.Ok(manifest!)
                : PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, error!);
        }

        if (text.StartsWith(LegacyManifestAdapter.InstallerMarker + "|", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyManifestAdapter.TryMapInstallerManifest(text);
        }

        return PackageProbeResult.Missing(
            $"Comments 前缀不是 {PackageManifest.SchemaId} 或 {LegacyManifestAdapter.InstallerMarker}（可人工登记 full 包）。");
    }
}

/// <summary>
/// 最小 PE32/PE32+ VERSIONINFO 解析器（仅用于读取内嵌构建清单，格式依据 Microsoft PE/COFF 规范）。
/// <para>
/// 按 PE 头 → 节表 → 资源目录（Type/Name/Language）→ 数据项 → VS_VERSIONINFO → StringFileInfo → 目标键
/// 逐层定位，只读命中资源，文件再大也不整份进内存；畸形长度/越界一律按「无清单」返回。
/// </para>
/// </summary>
internal static class PeVersionInfoReader
{
    private const uint PeSignature = 0x00004550; // "PE\0\0"
    private const int ResourceDataDirectoryIndex = 2;
    private const int RtVersion = 16;
    private const int MaxResourceBytes = 1024 * 1024; // 清单仅数百字符；超限视为坏包

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer);

    private readonly record struct ResourceEntry(uint RelativeOffset, bool IsDirectory);

    /// <summary>读取 VS_VERSIONINFO StringFileInfo 中指定键的值（找不到/不可解析返回 null）。</summary>
    public static string? ReadStringValue(Stream stream, string key)
    {
        try
        {
            if (!stream.CanSeek)
            {
                return null;
            }

            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var length = stream.Length;
            if (length < 0x40)
            {
                return null;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 24L > length)
            {
                return null;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != PeSignature)
            {
                return null;
            }

            reader.ReadUInt16(); // Machine
            int sectionCount = reader.ReadUInt16();
            stream.Position += 12; // TimeDateStamp / PointerToSymbolTable / NumberOfSymbols
            int optionalHeaderSize = reader.ReadUInt16();
            stream.Position += 2; // Characteristics

            long optionalStart = stream.Position;
            ushort magic = reader.ReadUInt16();
            int dataDirectoryOffset = magic switch
            {
                0x10B => 96,  // PE32
                0x20B => 112, // PE32+
                _ => -1,
            };
            if (dataDirectoryOffset < 0)
            {
                return null;
            }

            stream.Position = optionalStart + dataDirectoryOffset + ResourceDataDirectoryIndex * 8L;
            uint resourceRva = reader.ReadUInt32();
            uint resourceSize = reader.ReadUInt32();
            if (resourceRva == 0 || resourceSize == 0)
            {
                return null;
            }

            // 节表位于可选头之后；RVA → 文件偏移依赖节映射。
            long sectionTable = optionalStart + optionalHeaderSize;
            if (sectionTable + sectionCount * 40L > length)
            {
                return null;
            }

            var sections = new Section[sectionCount];
            stream.Position = sectionTable;
            for (var i = 0; i < sectionCount; i++)
            {
                stream.Position += 8; // Name
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                stream.Position += 16; // PointerToRelocations / PointerToLineNumbers / Characteristics 等
                sections[i] = new Section(virtualAddress, virtualSize, rawSize, rawPointer);
            }

            var resourceRoot = RvaToOffset(resourceRva, sections, length);
            if (resourceRoot is null)
            {
                return null;
            }

            // 资源目录三层：Type=16（RT_VERSION）→ Name（任意）→ Language（任意）。
            var type = FindResourceEntry(reader, resourceRoot.Value, 0, RtVersion);
            if (type is not { IsDirectory: true })
            {
                return null;
            }

            var name = FindResourceEntry(reader, resourceRoot.Value, type.Value.RelativeOffset, null);
            if (name is not { IsDirectory: true })
            {
                return null;
            }

            var language = FindResourceEntry(reader, resourceRoot.Value, name.Value.RelativeOffset, null);
            if (language is not { IsDirectory: false })
            {
                return null;
            }

            long dataEntryOffset = resourceRoot.Value + language.Value.RelativeOffset;
            if (dataEntryOffset + 16 > length)
            {
                return null;
            }

            stream.Position = dataEntryOffset;
            uint dataRva = reader.ReadUInt32();
            uint dataSize = reader.ReadUInt32();
            if (dataSize == 0 || dataSize > MaxResourceBytes)
            {
                return null;
            }

            var dataOffset = RvaToOffset(dataRva, sections, length);
            if (dataOffset is null || dataOffset.Value + dataSize > length)
            {
                return null;
            }

            var data = new byte[dataSize];
            stream.Position = dataOffset.Value;
            stream.ReadExactly(data);
            return ParseStringValue(data, key);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // 畸形 PE 的越界长度/字符串区间：按“无清单”处理，不让坏包打断上传请求。
            return null;
        }
    }

    /// <summary>在指定资源目录查找数字 Id（<paramref name="id"/> 为 null 时取第一个条目）。</summary>
    private static ResourceEntry? FindResourceEntry(
        BinaryReader reader,
        long resourceRoot,
        uint directoryRelativeOffset,
        uint? id)
    {
        long directory = resourceRoot + directoryRelativeOffset;
        reader.BaseStream.Position = directory;
        reader.ReadUInt32(); // Characteristics
        reader.ReadUInt32(); // TimeDateStamp
        reader.ReadUInt16(); // MajorVersion
        reader.ReadUInt16(); // MinorVersion
        ushort named = reader.ReadUInt16();
        ushort ids = reader.ReadUInt16();

        int total = named + ids;
        for (var i = 0; i < total; i++)
        {
            reader.BaseStream.Position = directory + 16 + i * 8L;
            uint nameOrId = reader.ReadUInt32();
            uint offsetField = reader.ReadUInt32();
            bool isNamed = (nameOrId & 0x8000_0000u) != 0;
            if (id is not null && (isNamed || nameOrId != id.Value))
            {
                continue;
            }

            return new ResourceEntry(offsetField & 0x7FFF_FFFFu, (offsetField & 0x8000_0000u) != 0);
        }

        return null;
    }

    private static long? RvaToOffset(uint rva, Section[] sections, long fileLength)
    {
        foreach (var section in sections)
        {
            uint span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + span)
            {
                continue;
            }

            long offset = section.RawPointer + (rva - section.VirtualAddress);
            return offset < fileLength ? offset : null;
        }

        return null;
    }

    /// <summary>解析 VS_VERSIONINFO：根块 → StringFileInfo → StringTable → 目标字符串块。</summary>
    private static string? ParseStringValue(byte[] data, string key)
    {
        if (!TryReadBlock(data, 0, data.Length, out var root)
            || !root.Key.Equals("VS_VERSION_INFO", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var child in EnumerateChildren(data, root))
        {
            if (!child.Key.Equals("StringFileInfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var table in EnumerateChildren(data, child))
            {
                foreach (var value in EnumerateChildren(data, table))
                {
                    if (value.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return value.Text;
                    }
                }
            }
        }

        return null;
    }

    private readonly record struct VersionBlock(string Key, int ValueOffset, int RawValueLength, string Text, int End);

    private static IEnumerable<VersionBlock> EnumerateChildren(byte[] data, VersionBlock parent)
    {
        var position = Align4(parent.ValueOffset + parent.RawValueLength);
        while (position + 6 <= parent.End)
        {
            if (!TryReadBlock(data, position, parent.End, out var child))
            {
                yield break;
            }

            yield return child;
            position = Align4(child.End);
        }
    }

    /// <summary>
    /// 读取一个 VS_VERSIONINFO 块：wLength/wValueLength/wType + UTF-16 szKey + 4 字节对齐的 value。
    /// <paramref name="limit"/> 为父块终点（防畸形长度越界）。
    /// </summary>
    private static bool TryReadBlock(byte[] data, int offset, int limit, out VersionBlock block)
    {
        block = default;
        if (offset < 0 || offset + 6 > limit)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
        if (length < 6 || offset + length > limit)
        {
            return false;
        }

        int blockEnd = offset + length;
        int keyStart = offset + 6;
        int keyEnd = keyStart;
        while (keyEnd + 1 < blockEnd && (data[keyEnd] != 0 || data[keyEnd + 1] != 0))
        {
            keyEnd += 2;
        }

        var key = Encoding.Unicode.GetString(data, keyStart, keyEnd - keyStart);
        int valueOffset = Align4(keyEnd + 2);
        int rawValueLength = type == 1 ? valueLength * 2 : valueLength;
        var text = valueOffset <= blockEnd && type == 1 && rawValueLength > 0
            ? ReadNullTerminatedUtf16(data, valueOffset, Math.Min(rawValueLength, blockEnd - valueOffset))
            : string.Empty;

        block = new VersionBlock(key, valueOffset, rawValueLength, text, blockEnd);
        return true;
    }

    private static string ReadNullTerminatedUtf16(byte[] data, int offset, int maxBytes)
    {
        int end = offset;
        while (end + 1 < offset + maxBytes && (data[end] != 0 || data[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(data, offset, end - offset);
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
