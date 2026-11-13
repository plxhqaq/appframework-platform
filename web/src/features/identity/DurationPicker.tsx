import { InputNumber, Segmented, Space, Typography } from 'antd';
import { DURATION_PRESETS, MAX_DURATION_MINUTES, clampDuration } from './singleLoginUi';

/**
 * 临时登录有效期选择器：预设一点即走，也能填任意分钟数。
 *
 * 输入过程中不钳制（否则清空重填会被弹回默认值），失焦与提交时才收敛，
 * 上限与服务端 `MaxDurationMinutes` 一致，免得现场以为设了 4 小时其实只拿到 2 小时。
 */
export function DurationPicker({
  value,
  onChange,
  label = '有效期',
}: {
  value: number | null;
  onChange: (value: number | null) => void;
  label?: string;
}) {
  return (
    <div className="field-page__field">
      <Typography.Text strong>{label}</Typography.Text>
      <Segmented
        block
        value={value ?? undefined}
        options={DURATION_PRESETS}
        onChange={(next) => onChange(clampDuration(Number(next)))}
      />
      <Space size="small" wrap>
        <InputNumber
          min={1}
          max={MAX_DURATION_MINUTES}
          precision={0}
          value={value}
          onChange={onChange}
          onBlur={() => onChange(clampDuration(value))}
          style={{ width: 104 }}
          aria-label="自定义有效期（分钟）"
        />
        <Typography.Text type="secondary">
          分钟，可填 1–{MAX_DURATION_MINUTES}
        </Typography.Text>
      </Space>
    </div>
  );
}
