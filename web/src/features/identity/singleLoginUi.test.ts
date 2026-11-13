import { describe, expect, it } from 'vitest';
import {
  DEFAULT_DURATION_MINUTES,
  MAX_DURATION_MINUTES,
  clampDuration,
  formatCountdown,
  formatDuration,
  qrStatusMeta,
  remainingSeconds,
} from './singleLoginUi';

describe('现场临时登录的时长与状态口径', () => {
  it('上限是 2 小时，与服务端钳制口径一致', () => {
    expect(MAX_DURATION_MINUTES).toBe(120);
    expect(clampDuration(120)).toBe(120);
    expect(clampDuration(600)).toBe(120);
  });

  it('空值取默认，非法值收敛到合法区间', () => {
    expect(clampDuration(null)).toBe(DEFAULT_DURATION_MINUTES);
    expect(clampDuration(undefined)).toBe(DEFAULT_DURATION_MINUTES);
    expect(clampDuration(Number.NaN)).toBe(DEFAULT_DURATION_MINUTES);
    expect(clampDuration(0)).toBe(1);
    expect(clampDuration(-30)).toBe(1);
    expect(clampDuration(45.7)).toBe(45);
  });

  it('整小时按小时说，其余按分钟说', () => {
    expect(formatDuration(120)).toBe('2 小时');
    expect(formatDuration(60)).toBe('1 小时');
    expect(formatDuration(45)).toBe('45 分钟');
  });

  it('四个扫码状态都有中文标签，未知状态原样显示', () => {
    expect(qrStatusMeta('pending').label).toBe('待审批');
    expect(qrStatusMeta('approved').label).toBe('已批准');
    expect(qrStatusMeta('rejected').label).toBe('已拒绝');
    expect(qrStatusMeta('expired').label).toBe('已过期');
    expect(qrStatusMeta('whatever')).toEqual({ color: 'default', label: 'whatever' });
  });

  it('到期后倒计时返回 null 而不是负数', () => {
    const now = Date.parse('2026-11-11T10:00:00Z');
    expect(remainingSeconds('2026-11-11T10:02:05Z', now)).toBe(125);
    expect(remainingSeconds('2026-11-11T09:59:59Z', now)).toBeNull();
    expect(remainingSeconds('2026-11-11T10:00:00Z', now)).toBeNull();
  });

  it('倒计时补零到 mm:ss', () => {
    expect(formatCountdown(125)).toBe('02:05');
    expect(formatCountdown(3600)).toBe('60:00');
    expect(formatCountdown(5)).toBe('00:05');
  });
});
