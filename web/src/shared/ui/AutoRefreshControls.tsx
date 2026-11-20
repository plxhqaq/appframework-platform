import { InputNumber, Space, Switch, Typography } from 'antd';
import { tr } from '../../i18n/translate';
import {
  MAX_INTERVAL_SECONDS,
  MIN_INTERVAL_SECONDS,
  clampIntervalSeconds,
  type AutoRefresh,
} from '../../shared/useAutoRefresh';

/** 自动刷新开关 + 间隔输入，放在列表卡片标题栏的 `extra` 里，和「刷新」按钮并排。 */
export function AutoRefreshControls({ state }: { state: AutoRefresh }) {
  return (
    <Space size={8} wrap>
      <Space size={6} align="center">
        <Switch size="small" checked={state.enabled} onChange={state.setEnabled} />
        <Typography.Text>{tr("自动刷新")}</Typography.Text>
      </Space>
      <Space size={4} align="center">
        <InputNumber
          size="small"
          min={MIN_INTERVAL_SECONDS}
          max={MAX_INTERVAL_SECONDS}
          precision={0}
          value={state.intervalSeconds}
          disabled={!state.enabled}
          onChange={(value) => {
            if (typeof value === 'number' && Number.isFinite(value)) {
              state.setIntervalSeconds(clampIntervalSeconds(value));
            }
          }}
          style={{ width: 84 }}
          aria-label={tr("自动刷新间隔（秒）")}
        />
        <Typography.Text type={state.enabled ? undefined : 'secondary'}>{tr("秒")}</Typography.Text>
      </Space>
    </Space>
  );
}
