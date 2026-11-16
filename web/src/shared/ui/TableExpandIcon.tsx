import type { MouseEvent } from 'react';
import i18n from '../../i18n';

interface TableExpandIconProps<RecordType> {
  expanded: boolean;
  expandable: boolean;
  record: RecordType;
  onExpand: (record: RecordType, event: MouseEvent<HTMLElement>) => void;
}

/**
 * 表格行展开指示器：右向箭头，展开时旋转 90° 朝下。
 *
 * antd 默认的 ± 由两条伪元素 1px 线叠加，缩放后粗细长短不一致；自带图标是填充字形，
 * 小尺寸下也跟着缩放发虚。这里手绘描边 chevron，笔宽以像素计，缩小时依然清晰。
 */
export function renderTableExpandIcon<RecordType>({
  expanded,
  expandable,
  record,
  onExpand,
}: TableExpandIconProps<RecordType>) {
  if (!expandable) {
    return <span className="table-expand-icon table-expand-icon--spacer" aria-hidden />;
  }

  return (
    <button
      type="button"
      className="table-expand-icon"
      aria-label={i18n.t(expanded ? 'labels.collapseRow' : 'labels.expandRow', { ns: 'common' })}
      aria-expanded={expanded}
      onClick={(event) => {
        onExpand(record, event);
        event.stopPropagation();
      }}
    >
      <svg
        width="14"
        height="14"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={3}
        strokeLinecap="round"
        strokeLinejoin="round"
        style={{ transform: expanded ? 'rotate(90deg)' : undefined }}
        aria-hidden
      >
        <path d="M9 5l7 7-7 7" />
      </svg>
    </button>
  );
}
