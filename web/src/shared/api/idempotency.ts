/**
 * 幂等键。`crypto.randomUUID` 只在安全上下文（https/localhost）暴露；管理台允许整站
 * http 部署（内网现场），那里退回 `crypto.getRandomValues` —— 它是非安全上下文仍可用的
 * Crypto 成员 —— 自己拼 UUID v4。
 */
export function idempotencyKey(): string {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0'));
  return [
    hex.slice(0, 4).join(''),
    hex.slice(4, 6).join(''),
    hex.slice(6, 8).join(''),
    hex.slice(8, 10).join(''),
    hex.slice(10).join(''),
  ].join('-');
}
