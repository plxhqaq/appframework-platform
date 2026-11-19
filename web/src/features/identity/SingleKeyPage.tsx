import { tr } from '../../i18n/translate';
import { DesktopOutlined, KeyOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Col,
  Descriptions,
  Empty,
  Grid,
  Input,
  Row,
  Space,
  Tag,
  Typography,
  theme,
} from 'antd';
import { useState } from 'react';
import { PageHeading } from '../../app/PageHeading';
import { identityApi } from '../../shared/api/identity';
import { installationLabel } from '../../shared/api/installations';
import type { InstallationSummary, SingleLoginKey } from '../../shared/api/types';
import { InstallationPickerModal } from '../../shared/ui/InstallationPickerModal';
import { useAuthStore } from '../../stores/auth';
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
  /** 签发那一刻绑定的设备描述（实例说明与/或指纹）：签发后表单还能改，结果卡片不能跟着变。 */
  device: string;
}

/** 绑定快照文案：实例（名称 · 机器码）与手输指纹并存时用「/」连接。 */
function bindingText(fingerprint: string, installation: InstallationSummary | null): string {
  const parts: string[] = [];
  if (installation) {
    parts.push(`${installationLabel(installation)} · ${installation.machineCode}`);
  }
  if (fingerprint) {
    parts.push(fingerprint);
  }
  return parts.join(' / ');
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
  const screens = Grid.useBreakpoint();
  const [duration, setDuration] = useState<number | null>(DEFAULT_DURATION_MINUTES);
  const [device, setDevice] = useState('');
  const [bound, setBound] = useState<InstallationSummary | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [issued, setIssued] = useState<IssuedKey | null>(null);

  // 选择弹窗列的是「当前应用」的安装实例：与签发目标应用同源（平台上下文回退首个应用）。
  const session = useAuthStore((state) => state.session);
  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const now = useTicker(issued !== null);
  const left = issued ? remainingSeconds(issued.result.expiresAt, now) : null;

  const issueMutation = useMutation({
    mutationFn: () =>
      identityApi.issueSingleLoginKey({
        durationMinutes: clampDuration(duration),
        deviceFingerprint: device.trim() || undefined,
        machineCode: bound?.machineCode ?? undefined,
        installGuid: bound?.installGuid ?? undefined,
      }),
    onSuccess: (result) => {
      setIssued({ result, device: bindingText(device.trim(), bound) });
      message.success(tr("密钥已签发，请立即告知使用人"));
    },
    onError: (error: Error) => message.error(error.message),
  });

  return (
    <div className="field-page field-page--wide">
      <PageHeading
        title={tr("密钥登录")}
        description={tr("给现场的客户端发一个 6 位数字口令，兑换后拿到临时令牌。")}
      />

      <Alert
        type="info"
        showIcon
        message={tr("口令只能兑换一次")}
        description={tr("有效期最长 {{arg0}}。兑换出的临时会话能用业务功能，但不能签发新密钥、也不能审批扫码。", { arg0: formatDuration(MAX_DURATION_MINUTES) })}
      />

      {/* 宽屏左右分栏；手机上结果排到表单上面，签发完不用往回滚。 */}
      <Row gutter={[16, 16]}>
        <Col xs={{ span: 24, order: issued ? 2 : 1 }} xl={{ span: 10, order: 1 }}>
          <Card size="small" title={tr("签发参数")} className="key-panel">
            <Space direction="vertical" size="middle" style={{ width: '100%' }}>
              <DurationPicker value={duration} onChange={setDuration} />

              <div className="field-page__field">
                <Typography.Text strong>{tr("绑定设备")}</Typography.Text>
                {bound ? (
                  <div style={{ marginTop: 8 }}>
                    <Tag closable color="blue" onClose={() => setBound(null)}>
                      {`${installationLabel(bound)} · ${bound.machineCode}`}
                    </Tag>
                  </div>
                ) : null}
                <Space.Compact style={{ width: '100%', marginTop: bound ? 8 : 0 }}>
                  <Input
                    allowClear
                    value={device}
                    onChange={(event) => setDevice(event.target.value)}
                    placeholder={tr("留空则不限制设备")}
                    autoComplete="off"
                  />
                  <Button
                    icon={<DesktopOutlined />}
                    disabled={appKey === null}
                    onClick={() => setPickerOpen(true)}
                  >
                    {tr("选择设备")}</Button>
                </Space.Compact>
                <Typography.Text type="secondary">
                  {tr("填了就只有这台设备能兑换，指纹在客户端「关于」页可以复制。")}</Typography.Text>
                <Typography.Text type="secondary">
                  {tr("也可以直接选择安装实例绑定（离线实例同样可选）；两种绑定同时填写时都必须匹配。")}</Typography.Text>
              </div>

              <Button
                type="primary"
                size="large"
                block
                icon={<KeyOutlined />}
                loading={issueMutation.isPending}
                onClick={() => issueMutation.mutate()}
              >
                {tr("签发密钥")}</Button>
            </Space>
          </Card>
        </Col>

        {issued ? (
          <Col xs={{ span: 24, order: 1 }} xl={{ span: 14, order: 2 }}>
            <Card
              size="small"
              title={tr("密钥已签发")}
              className="key-panel"
              extra={
                left === null ? (
                  <Tag>{tr("已过期")}</Tag>
                ) : (
                  <Tag color="processing">{tr("剩余")}{formatCountdown(left)}</Tag>
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
                    copyable={{ text: issued.result.key, tooltips: [tr("复制密钥"), tr("已复制")] }}
                  >
                    {issued.result.key}
                  </Typography.Text>
                </div>

                <Alert
                  type="warning"
                  showIcon
                  message={tr("明文只显示这一次")}
                  description={tr("再签发或离开本页后服务端也无法回查，请先念给使用人或截图发送。")}
                />

                <Descriptions
                  size="small"
                  column={1}
                  items={[
                    {
                      key: 'duration',
                      label: tr("有效期"),
                      children: formatDuration(issued.result.durationMinutes),
                    },
                    {
                      key: 'expiresAt',
                      label: tr("到期时间"),
                      children: formatDateTime(issued.result.expiresAt),
                    },
                    { key: 'device', label: tr("绑定设备"), children: issued.device || tr("不限制") },
                  ]}
                />

                <Button
                  block
                  size="large"
                  icon={<ReloadOutlined />}
                  onClick={() => setIssued(null)}
                >
                  {tr("再签发一个")}</Button>
              </Space>
            </Card>
          </Col>
        ) : screens.xl ? (
          <Col xl={{ span: 14, order: 2 }}>
            <Card size="small" title={tr("密钥预览")} className="key-panel key-panel--empty">
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={tr("签发后的 6 位口令与到期时间会显示在这里；明文只出现一次，记得先念给使用人。")}
              />
            </Card>
          </Col>
        ) : null}
      </Row>

      <InstallationPickerModal
        open={pickerOpen}
        appKey={appKey}
        mode="any"
        single
        title={tr("选择绑定设备")}
        selected={bound ? new Map([[bound.id, bound]]) : new Map()}
        onConfirm={(map) => {
          setBound([...map.values()][0] ?? null);
          setPickerOpen(false);
        }}
        onCancel={() => setPickerOpen(false)}
      />
    </div>
  );
}
