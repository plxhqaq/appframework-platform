import { useEffect, useState } from 'react';

/**
 * 单次密钥与扫码登录共用的口径。
 *
 * 上限与服务端 `SingleLogin:MaxDurationMinutes` / `QrLogin:MaxDurationMinutes` 对齐：
 * 现场按班次作业，半小时不够用，两边都放宽到 2 小时。服务端对超限请求按上限钳制，
 * 这里提前挡住，免得现场以为设了 4 小时其实只拿到 2 小时。
 */
export const MAX_DURATION_MINUTES = 120;

export const DEFAULT_DURATION_MINUTES = 30;

export const DURATION_PRESETS = [
  { value: 15, label: '15 分钟' },
  { value: 30, label: '30 分钟' },
  { value: 60, label: '1 小时' },
  { value: 120, label: '2 小时' },
];

/** 把输入框里的任意值收敛成合法分钟数（空值取默认）。 */
export function clampDuration(value: number | null | undefined): number {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return DEFAULT_DURATION_MINUTES;
  }
  return Math.min(MAX_DURATION_MINUTES, Math.max(1, Math.trunc(value)));
}

/** 时长的人话说法，给结果卡片用。 */
export function formatDuration(minutes: number): string {
  if (minutes % 60 === 0) {
    return `${minutes / 60} 小时`;
  }
  return `${minutes} 分钟`;
}

export const QR_STATUS_META: Record<string, { color: string; label: string }> = {
  pending: { color: 'gold', label: '待审批' },
  approved: { color: 'green', label: '已批准' },
  rejected: { color: 'red', label: '已拒绝' },
  expired: { color: 'default', label: '已过期' },
};

export function qrStatusMeta(status: string): { color: string; label: string } {
  return QR_STATUS_META[status] ?? { color: 'default', label: status };
}

export function formatDateTime(value?: string | null): string {
  return value ? new Date(value).toLocaleString() : '-';
}

/** 剩余时间的 mm:ss；已到期返回 null，由调用方决定怎么提示。 */
export function remainingSeconds(expiresAt: string, now: number): number | null {
  const left = Math.floor((new Date(expiresAt).getTime() - now) / 1000);
  return left > 0 ? left : null;
}

export function formatCountdown(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return `${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`;
}

/**
 * 每秒走一次的当前时刻，给倒计时用。
 * active 为 false 时不起定时器；卸载即清，重复渲染不会叠出第二个循环。
 */
export function useTicker(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!active) {
      return;
    }
    setNow(Date.now());
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [active]);

  return now;
}
