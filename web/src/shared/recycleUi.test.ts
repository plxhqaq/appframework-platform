import { describe, expect, it } from 'vitest';
import {
  GRACE_WARNING_MINUTES,
  fileStateMeta,
  isGraceCritical,
  remainingGraceLabel,
} from './recycleUi';

describe('回收站展示口径', () => {
  it('剩余 5 分钟及以内告警', () => {
    expect(isGraceCritical(GRACE_WARNING_MINUTES)).toBe(true);
    expect(isGraceCritical(GRACE_WARNING_MINUTES - 1)).toBe(true);
    expect(isGraceCritical(GRACE_WARNING_MINUTES + 1)).toBe(false);
  });

  it('剩余宽限文案覆盖分钟/小时/到期', () => {
    expect(remainingGraceLabel(0)).toBe('等待下次清理');
    expect(remainingGraceLabel(12)).toBe('剩余 12 分钟');
    expect(remainingGraceLabel(60)).toBe('剩余 1 小时');
    expect(remainingGraceLabel(95)).toBe('剩余 1 小时 35 分钟');
  });

  it('文件缺失行不可恢复', () => {
    expect(fileStateMeta(true)).toEqual({ label: '文件完好', color: 'green', restorable: true });
    expect(fileStateMeta(false)).toEqual({ label: '文件已缺失', color: 'red', restorable: false });
  });
});
