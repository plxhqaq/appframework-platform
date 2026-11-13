/** 登录后的回跳目标：只认站内相对路径，`//host`、`http://…`、`/\host` 一律丢弃。 */
export function safeReturnTo(value: string | null | undefined): string | null {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return null;
  }
  // 回跳到登录页自己会绕圈
  if (value === '/login' || value.startsWith('/login?') || value.startsWith('/login/')) {
    return null;
  }
  return value;
}

/** 带 returnTo 的登录路径。扫码直达审批页时靠它把「登录 → 回到那一单」接起来。 */
export function loginPath(returnTo: string | null | undefined): string {
  const target = safeReturnTo(returnTo);
  return target ? `/login?returnTo=${encodeURIComponent(target)}` : '/login';
}
