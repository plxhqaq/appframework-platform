/**
 * 浏览器端文件哈希：上传票据要求先声明 SHA-256 与字节数，服务端上传后二次校验。
 *
 * `crypto.subtle.digest` 只能整块计算，超大文件会短暂占用等量内存；
 * 发行脚本/CI 上传大产物时应走 CLI，而不是管理台。
 */
export async function sha256Hex(data: BufferSource): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(digest))
    .map((byte) => byte.toString(16).padStart(2, '0'))
    .join('');
}

/** 计算文件哈希；返回小写十六进制摘要。 */
export async function hashFile(file: File): Promise<string> {
  return sha256Hex(await file.arrayBuffer());
}

/**
 * 规范清单路径：Windows 分隔符转 `/`，去掉目录选择的根目录段。
 * 与服务端 `NormalizePath` 的口径一致——空段、`.`、`..`、绝对路径都会被拒绝。
 */
export function normalizeReleasePath(rawPath: string, stripRoot: boolean): string {
  const normalized = rawPath.trim().replace(/\\/g, '/');
  if (!stripRoot) {
    return normalized;
  }

  const index = normalized.indexOf('/');
  return index >= 0 ? normalized.slice(index + 1) : normalized;
}
