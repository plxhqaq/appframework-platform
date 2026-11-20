import { tr } from '../i18n/translate';

/** 剩余宽限 ≤5 分钟时标红，提示即将物理删除。 */
export const GRACE_WARNING_MINUTES = 5;

export function isGraceCritical(remainingMinutes: number): boolean {
  return remainingMinutes <= GRACE_WARNING_MINUTES;
}

/** 剩余宽限文案：0 表示已到期，等待下一轮清理。 */
export function remainingGraceLabel(remainingMinutes: number): string {
  if (remainingMinutes <= 0) {
    return tr('等待下次清理');
  }
  if (remainingMinutes < 60) {
    return tr('剩余 {{arg0}} 分钟', { arg0: remainingMinutes });
  }
  const hours = Math.floor(remainingMinutes / 60);
  const minutes = remainingMinutes % 60;
  return minutes > 0
    ? tr('剩余 {{arg0}} 小时 {{arg1}} 分钟', { arg0: hours, arg1: minutes })
    : tr('剩余 {{arg0}} 小时', { arg0: hours });
}

/** 文件状态：缺失行在回收站可见但不可恢复，只能立即清理。 */
export function fileStateMeta(fileIntact: boolean): {
  label: string;
  color?: string;
  restorable: boolean;
} {
  return fileIntact
    ? { label: tr('文件完好'), color: 'green', restorable: true }
    : { label: tr('文件已缺失'), color: 'red', restorable: false };
}
