using Platform.Contracts.Devices;
using Platform.Kernel.Errors;

namespace Platform.Modules.Devices.Services;

/// <summary>设备输入校验。只在系统边界（Hub/REST）使用，服务内部之间互信。</summary>
internal static class DeviceValidation
{
    public static string RequireName(string? value, string field)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw BusinessException.Invalid($"{field} 不能为空。");
        }

        if (trimmed.Length > DeviceConventions.MaxNameLength)
        {
            throw BusinessException.Invalid(
                $"{field} 超长（最多 {DeviceConventions.MaxNameLength} 字符）。");
        }

        return trimmed;
    }

    public static string? Optional(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw BusinessException.Invalid($"{field} 超长（最多 {maxLength} 字符）。");
        }

        return trimmed;
    }

    public static int RequireHeartbeatSeconds(int value)
    {
        if (value is < DeviceConventions.MinHeartbeatSeconds or > DeviceConventions.MaxHeartbeatSeconds)
        {
            throw BusinessException.Invalid(
                $"心跳周期必须在 {DeviceConventions.MinHeartbeatSeconds}–{DeviceConventions.MaxHeartbeatSeconds} 秒之间。");
        }

        return value;
    }
}
