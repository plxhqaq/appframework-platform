import i18n from '../i18n';

/** 按当前界面语言格式化日期时间；空值回退为调用方给的占位符。 */
export function formatDateTime(
  value: string | number | Date | null | undefined,
  fallback = '-',
): string {
  if (value === null || value === undefined || value === '') {
    return fallback;
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return fallback;
  }

  return new Intl.DateTimeFormat(i18n.language, {
    dateStyle: 'medium',
    timeStyle: 'medium',
  }).format(date);
}

/** 按当前界面语言格式化日期（不含时间）。 */
export function formatDate(
  value: string | number | Date | null | undefined,
  fallback = '-',
): string {
  if (value === null || value === undefined || value === '') {
    return fallback;
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return fallback;
  }

  return new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(date);
}

/** 人类可读的字节数。 */
export function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`;
  }

  const units = ['KB', 'MB', 'GB', 'TB'];
  let size = value / 1024;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }

  return `${size.toFixed(size >= 100 ? 0 : 1)} ${units[unit]}`;
}
