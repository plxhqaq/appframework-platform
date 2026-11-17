import { resolveApiUrl } from './api/client';

/** 用 a[download] 保存已带令牌取回的 Blob，并释放临时 URL。 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/**
 * 交给浏览器下载管理器：文件不进页面内存，支持大文件流式落盘与暂停后断点续传。
 * 只用于带票据的 GET 地址；需要 Bearer 的接口仍走 `saveBlob`。
 */
export function startDownload(url: string): void {
  const anchor = document.createElement('a');
  anchor.href = resolveApiUrl(url);
  anchor.rel = 'noopener';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}
