using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Kernel.Errors;

namespace Platform.Modules.Identity.Security;

/// <summary>
/// 口令策略：长度 8–128，且同时包含字母与数字。
/// 刻意不强制特殊字符——那会诱导用户使用可预测的 <c>Passw0rd!</c> 式口令；
/// 真正兜底的是 Argon2id 与登录节流。
/// </summary>
public static class PasswordPolicy
{
    /// <summary>校验口令；不合规抛 <see cref="ApiErrorCode.IdentityPasswordPolicyViolation"/>。</summary>
    public static void EnsureValid(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new BusinessException(ApiErrorCode.IdentityPasswordPolicyViolation, "口令不能为空。");
        }

        if (password.Length < IdentityConventions.PasswordMinLength)
        {
            throw new BusinessException(
                ApiErrorCode.IdentityPasswordPolicyViolation,
                $"口令长度不得少于 {IdentityConventions.PasswordMinLength} 个字符。");
        }

        if (password.Length > IdentityConventions.PasswordMaxLength)
        {
            throw new BusinessException(
                ApiErrorCode.IdentityPasswordPolicyViolation,
                $"口令长度不得超过 {IdentityConventions.PasswordMaxLength} 个字符。");
        }

        var hasLetter = false;
        var hasDigit = false;
        foreach (var ch in password)
        {
            hasLetter |= char.IsLetter(ch);
            hasDigit |= char.IsDigit(ch);
        }

        if (!hasLetter || !hasDigit)
        {
            throw new BusinessException(
                ApiErrorCode.IdentityPasswordPolicyViolation,
                "口令必须同时包含字母与数字。");
        }
    }
}
