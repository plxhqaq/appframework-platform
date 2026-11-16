using System.Globalization;
using System.Runtime.InteropServices;

namespace Platform.Api.Diagnostics;

/// <summary>整机（宿主机）资源探测：CPU 取累计时间差，内存取物理总量与可用量。</summary>
/// <remarks>
/// 进程指标只反映本进程，仪表盘上的「服务器资源」需要整机口径：
/// Linux 读 /proc/stat 与 /proc/meminfo（容器里通常就是宿主数据），
/// Windows 调 GetSystemTimes 与 GlobalMemoryStatusEx。
/// 其他平台返回 null，由调用方显示为不可用。
/// </remarks>
internal static class ServerResourceProbe
{
    private const string LinuxStatPath = "/proc/stat";
    private const string LinuxMemInfoPath = "/proc/meminfo";

    /// <summary>读取整机 CPU 累计计时；当前平台不支持时返回 null。</summary>
    public static ServerCpuTimes? TryReadCpuTimes()
    {
        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxCpuTimes();
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsCpuTimes();
        }

        return null;
    }

    /// <summary>读取整机物理内存总量与可用量；当前平台不支持时返回 null。</summary>
    public static ServerMemoryInfo? TryReadMemory()
    {
        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxMemory();
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsMemory();
        }

        return null;
    }

    /// <summary>两次累计计时的增量得到整机 CPU 占用率；计数器不前进时返回 null。</summary>
    public static double? CpuUsagePercent(ServerCpuTimes previous, ServerCpuTimes current)
    {
        var totalDelta = current.Total - previous.Total;
        if (totalDelta <= 0)
        {
            return null;
        }

        var busyDelta = totalDelta - (current.Idle - previous.Idle);
        return Math.Round(Math.Clamp(busyDelta * 100d / totalDelta, 0, 100), 1);
    }

    private static ServerCpuTimes? ReadLinuxCpuTimes()
    {
        try
        {
            foreach (var line in File.ReadLines(LinuxStatPath))
            {
                if (!line.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    continue;
                }

                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 6)
                {
                    return null;
                }

                long total = 0;
                for (var index = 1; index < fields.Length; index++)
                {
                    if (!TryParseCount(fields[index], out var value))
                    {
                        return null;
                    }

                    total += value;
                }

                // 空闲口径与 top 一致：idle + iowait
                var idle = (TryParseCount(fields[4], out var idleValue) ? idleValue : 0)
                           + (TryParseCount(fields[5], out var iowaitValue) ? iowaitValue : 0);
                return new ServerCpuTimes(idle, total);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ServerCpuTimes? ReadWindowsCpuTimes()
    {
        // 内核时间包含空闲时间，总量 = 内核 + 用户，空闲单独扣除。
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return new ServerCpuTimes(idle, kernel + user);
    }

    private static ServerMemoryInfo? ReadLinuxMemory()
    {
        try
        {
            long totalKb = 0;
            long availableKb = 0;
            long fallbackAvailableKb = 0;

            foreach (var line in File.ReadLines(LinuxMemInfoPath))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = line[..colon];
                var valueText = line[(colon + 1)..].Trim();
                var space = valueText.IndexOf(' ');
                var numberText = space > 0 ? valueText[..space] : valueText;
                if (!long.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
                {
                    continue;
                }

                switch (key)
                {
                    case "MemTotal":
                        totalKb = kilobytes;
                        break;
                    case "MemAvailable":
                        availableKb = kilobytes;
                        break;
                    case "MemFree":
                    case "Buffers":
                    case "Cached":
                        // MemAvailable 缺失（老内核）时的兜底口径
                        fallbackAvailableKb += kilobytes;
                        break;
                }
            }

            if (totalKb <= 0)
            {
                return null;
            }

            if (availableKb <= 0)
            {
                availableKb = fallbackAvailableKb;
            }

            var totalBytes = totalKb * 1024;
            var availableBytes = Math.Clamp(availableKb * 1024, 0, totalBytes);
            return new ServerMemoryInfo(totalBytes, availableBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ServerMemoryInfo? ReadWindowsMemory()
    {
        var status = MemoryStatusEx.Create();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        var totalBytes = (long)Math.Min(status.ullTotalPhys, long.MaxValue);
        var availableBytes = (long)Math.Min(status.ullAvailPhys, (ulong)totalBytes);
        return new ServerMemoryInfo(totalBytes, availableBytes);
    }

    private static bool TryParseCount(string text, out long value) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MemoryStatusEx Create() =>
            new() { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>整机 CPU 累计计时；数值单位不重要，只用于求两次采样的增量。</summary>
internal readonly record struct ServerCpuTimes(long Idle, long Total);

/// <summary>整机物理内存：总量与可用量（字节）。</summary>
internal readonly record struct ServerMemoryInfo(long TotalBytes, long AvailableBytes);
