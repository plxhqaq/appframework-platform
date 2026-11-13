import { KeyOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Input,
  Space,
  Tag,
  Typography,
  theme,
} from 'antd';
import { useState } from 'react';
import { PageHeading } from '../../app/PageHeading';
import { identityApi } from '../../shared/api/identity';
import type { SingleLoginKey } from '../../shared/api/types';
import { DurationPicker } from './DurationPicker';
import {
  DEFAULT_DURATION_MINUTES,
  MAX_DURATION_MINUTES,
  clampDuration,
  formatCountdown,
  formatDateTime,
  formatDuration,
  remainingSeconds,
  useTicker,
} from './singleLoginUi';

interface IssuedKey {
  result: SingleLoginKey;
  /** 签发那一刻绑定的设备：签发后表单还能改，结果卡片不能跟着变。 */
  device: string;
}

/**
 * 密钥登录：给现场客户端发一个 6 位数字口令。
 *
 * 单独成页而不是塞在「身份与权限」的标签页里——现场工程师进管理台基本只做这件事，
 * 手机上要能一屏点完。明文只在签发响应里出现一次，所以结果留在页面上而不是弹窗里，
 * 免得误触遮罩就再也看不到了。
 */
export function SingleKeyPage() {
  const { message } = App.useApp();
  const { token } = theme.useToken();
  const [duration, setDuration] = useState<number | null>(DEFAULT_DURATION_MINUTES);
  const [device, setDevice] = useState('');
  const [issued, setIssued] = useState<IssuedKey | null>(null);

  const now = useTicker(issued !== null);
  const left = issued ? remainingSeconds(issued.result.expiresAt, now) : null;

  const issueMutation = useMutation({
    mutationFn: () =>
      identityApi.issueSingleLoginKey({
        durationMinutes: clampDuration(duration),
        deviceFingerprint: device.trim() || undefined,
      }),
    onSuccess: (result) => {
      setIssued({ result, device: device.trim() });
      message.success('密钥已签发，请立即告知使用人');
    },
    onError: (error: Error) => message.error(error.message),
  });

  return (
    <div className="field-page">
      <PageHeading
        title="密钥登录"
        description="给现场的客户端发一个 6 位数字口令，兑换后拿到临时令牌。"
      />

      <Alert
        type="info"
        showIcon
        message="口令只能兑换一次"
        description={`有效期最长 ${formatDuration(MAX_DURATION_MINUTES)}。兑换出的临时会话能用业务功能，但不能签发新密钥、也不能审批扫码。`}
      />

      <Card size="small" title="签发参数">
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <DurationPicker value={duration} onChange={setDuration} />

          <div className="field-page__field">
            <Typography.Text strong>绑定设备指纹</Typography.Text>
            <Input
              allowClear
              value={device}
              onChange={(event) => setDevice(event.target.value)}
              placeholder="留空则不限制设备"
              autoComplete="off"
            />
            <Typography.Text type="secondary">
              填了就只有这台设备能兑换，指纹在客户端「关于」页可以复制。
            </Typography.Text>
          </div>

          <Button
            type="primary"
            size="large"
            block
            icon={<KeyOutlined />}
            loading={issueMutation.isPending}
            onClick={() => issueMutation.mutate()}
          >
            签发密钥
          </Button>
        </Space>
      </Card>

      {issued ? (
        <Card
          size="small"
          title="密钥已签发"
          extra={
            left === null ? (
              <Tag>已过期</Tag>
            ) : (
              <Tag color="processing">剩余 {formatCountdown(left)}</Tag>
            )
          }
        >
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <div
              className="key-card__code"
              style={{
                background: token.colorPrimaryBg,
                borderColor: token.colorPrimaryBorder,
              }}
            >
              <Typography.Text
                className="key-card__digits"
                style={{ color: token.colorText }}
                copyable={{ text: issued.result.key, tooltips: ['复制密钥', '已复制'] }}
              >
                {issued.result.key}
              </Typography.Text>
            </div>

            <Alert
              type="warning"
              showIcon
              message="明文只显示这一次"
              description="再签发或离开本页后服务端也无法回查，请先念给使用人或截图发送。"
            />

            <Descriptions
              size="small"
              column={1}
              items={[
                {
                  key: 'duration',
                  label: '有效期',
                  children: formatDuration(issued.result.durationMinutes),
                },
                {
                  key: 'expiresAt',
                  label: '到期时间',
                  children: formatDateTime(issued.result.expiresAt),
                },
                { key: 'device', label: '绑定设备', children: issued.device || '不限制' },
              ]}
            />

            <Button
              block
              size="large"
              icon={<ReloadOutlined />}
              onClick={() => setIssued(null)}
            >
              再签发一个
            </Button>
          </Space>
        </Card>
      ) : null}
    </div>
  );
}
