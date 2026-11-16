import { formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { ReloadOutlined, ThunderboltOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Drawer,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { devicesApi } from '../../shared/api/devices';
import type {
  DeviceAlarmLevel,
  DeviceDetail,
  DeviceOnlineStatus,
  DeviceStationStatus,
  DeviceSummary,
  UpdateDeviceRequest,
} from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';

interface EditFormValues {
  customName?: string;
  isEnabled: boolean;
}

interface RestartFormValues {
  reason?: string;
}

const DEFAULT_PAGE_SIZE = 20;

const ONLINE_META: Record<DeviceOnlineStatus, { label: string; color?: string }> = {
  online: { get label() { return tr("在线"); }, color: 'green' },
  offline: { get label() { return tr("离线"); } },
};

const STATION_META: Record<DeviceStationStatus, { label: string; color: string }> = {
  idle: { get label() { return tr("待机"); }, color: 'blue' },
  online: { get label() { return tr("运行中"); }, color: 'orange' },
};

const ALARM_META: Record<DeviceAlarmLevel, { label: string; color?: string }> = {
  none: { get label() { return tr("无"); } },
  info: { get label() { return tr("提示"); }, color: 'blue' },
  warning: { get label() { return tr("警告"); }, color: 'orange' },
  error: { get label() { return tr("错误"); }, color: 'red' },
};

/**
 * 设备页。
 *
 * 在线判定完全由服务端给出（数据库在线标记 + 实时连接），前端只做展示与按钮门禁；
 * 定向下发/重启是否放行仍由服务端二次校验，隐藏按钮不构成安全边界。
 */
export function DevicesPage() {
  const { message } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.deviceManage));
  const canRestart = useAuthStore((state) => state.hasPermission(PERMISSIONS.deviceRestart));

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [editForm] = Form.useForm<EditFormValues>();
  const [restartForm] = Form.useForm<RestartFormValues>();

  const [editTarget, setEditTarget] = useState<DeviceSummary | null>(null);
  const [restartTarget, setRestartTarget] = useState<DeviceSummary | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [onlineStatus, setOnlineStatus] = useState<DeviceOnlineStatus | undefined>();
  const [branch, setBranch] = useState<string | undefined>();
  const [keyword, setKeyword] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setOnlineStatus(undefined);
    setBranch(undefined);
    setKeyword(undefined);
    setPage(1);
    setDetailId(null);
  }, [appKey]);

  const devices = useQuery({
    queryKey: ['devices', 'list', appKey, onlineStatus, branch, keyword, page, pageSize],
    queryFn: ({ signal }) =>
      devicesApi.listDevices({ onlineStatus, branch, keyword, page, pageSize }, signal),
    enabled: appKey !== null,
  });

  const detail = useQuery({
    queryKey: ['devices', 'detail', detailId],
    queryFn: ({ signal }) => devicesApi.getDevice(detailId!, signal),
    enabled: detailId !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['devices'] });
  };

  const openEditForm = (row: DeviceSummary) => {
    setEditTarget(row);
    editForm.resetFields();
    editForm.setFieldsValue({ customName: row.customName ?? '', isEnabled: row.isEnabled });
  };

  const submitEdit = async () => {
    if (!editTarget) {
      return;
    }

    const values = await editForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      const body: UpdateDeviceRequest = {
        customName: values.customName?.trim() ?? '',
        isEnabled: values.isEnabled,
      };
      await devicesApi.updateDevice(editTarget.id, body);
      message.success(tr("设备已更新"));
      setEditTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    } finally {
      setSaving(false);
    }
  };

  const openRestartForm = (row: DeviceSummary) => {
    setRestartTarget(row);
    restartForm.resetFields();
    restartForm.setFieldsValue({ reason: '' });
  };

  const submitRestart = async () => {
    if (!restartTarget) {
      return;
    }

    const values = await restartForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      await devicesApi.restartDevice(restartTarget.id, {
        reason: values.reason?.trim() || null,
      });
      message.success(tr("重启命令已送达"));
      setRestartTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("重启失败"));
    } finally {
      setSaving(false);
    }
  };

  const columns: TableColumnsType<DeviceSummary> = [
    {
      title: tr("设备"),
      dataIndex: 'displayName',
      key: 'displayName',
      width: 220,
      render: (value: string, row: DeviceSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{value || tr("(未命名)")}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {row.shortId}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: tr("机器码"),
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 150,
      responsive: ['md'],
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: tr("分支"),
      dataIndex: 'branch',
      key: 'branch',
      width: 100,
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("框架版本"),
      dataIndex: 'frameworkVersion',
      key: 'frameworkVersion',
      width: 110,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 150,
      render: (_: unknown, row: DeviceSummary) => (
        <Space size={4} wrap>
          <Tag color={ONLINE_META[row.onlineStatus].color}>{ONLINE_META[row.onlineStatus].label}</Tag>
          {row.onlineStatus === 'online' ? (
            <Tag color={STATION_META[row.stationStatus].color}>
              {STATION_META[row.stationStatus].label}
            </Tag>
          ) : null}
        </Space>
      ),
    },
    {
      title: tr("报警"),
      dataIndex: 'alarmLevel',
      key: 'alarmLevel',
      width: 120,
      responsive: ['md'],
      render: (level: DeviceAlarmLevel, row: DeviceSummary) => {
        const meta = ALARM_META[level];
        return meta.color ? (
          <Tag color={meta.color} title={row.alarmMessage ?? undefined}>
            {meta.label}
          </Tag>
        ) : (
          <Typography.Text type="secondary">{meta.label}</Typography.Text>
        );
      },
    },
    {
      title: tr("最后心跳"),
      dataIndex: 'lastHeartbeatAt',
      key: 'lastHeartbeatAt',
      width: 170,
      responsive: ['lg'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
    {
      title: tr("启用"),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 200,
      fixed: 'right',
      render: (_: unknown, row: DeviceSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => setDetailId(row.id)}>
            {tr("详情")}</Button>
          {canManage ? (
            <Button type="link" size="small" onClick={() => openEditForm(row)}>
              {tr("编辑")}</Button>
          ) : null}
          {canRestart ? (
            <Button
              type="link"
              size="small"
              danger
              disabled={!row.isEnabled || row.onlineStatus !== 'online'}
              title={
                !row.isEnabled
                  ? tr("设备已停用")
                  : row.onlineStatus !== 'online'
                    ? tr("设备离线，无法重启")
                    : undefined
              }
              onClick={() => openRestartForm(row)}
            >
              {tr("重启")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title={tr("设备")} size="small">
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有可用的应用上下文")}
          description={tr("设备按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。")}
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title={tr("设备")}
        extra={
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            {tr("刷新")}</Button>
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Input.Search
            key={`keyword-${appKey}`}
            allowClear
            placeholder={tr("设备名 / 机器码")}
            style={{ width: 220, maxWidth: '100%' }}
            defaultValue={keyword}
            onSearch={(value) => {
              setKeyword(value.trim() || undefined);
              setPage(1);
            }}
          />
          <Select<DeviceOnlineStatus>
            allowClear
            placeholder={tr("全部在线状态")}
            style={{ width: 160, maxWidth: '100%' }}
            value={onlineStatus}
            onChange={(value) => {
              setOnlineStatus(value);
              setPage(1);
            }}
            options={[
              { value: 'online', label: tr("在线") },
              { value: 'offline', label: tr("离线") },
            ]}
          />
          <Input.Search
            key={`branch-${appKey}`}
            allowClear
            placeholder={tr("分支")}
            style={{ width: 160, maxWidth: '100%' }}
            defaultValue={branch}
            onSearch={(value) => {
              setBranch(value.trim() || undefined);
              setPage(1);
            }}
          />
        </Space>

        {devices.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(devices.error as Error).message}
            action={
              <Button size="small" onClick={() => void devices.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<DeviceSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={devices.isPending}
          dataSource={devices.data?.items ?? []}
          columns={columns}
          pagination={responsivePagination(isMobile, {
            current: devices.data?.page ?? page,
            pageSize: devices.data?.pageSize ?? pageSize,
            total: devices.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 台设备", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: tr("尚无设备连接（客户端连接 /hubs/devices 后自动注册）") }}
        />
      </Card>

      <Modal
        open={editTarget !== null}
        title={editTarget ? tr("编辑设备：{{arg0}}", { arg0: editTarget.displayName }) : tr("编辑设备")}
        confirmLoading={saving}
        onOk={() => void submitEdit()}
        onCancel={() => setEditTarget(null)}
      >
        <Form form={editForm} layout="vertical">
          <Form.Item
            name="customName"
            label={tr("自定义名（前缀拼在自动名之前，留空清除）")}
            rules={[{ max: 128 }]}
          >
            <Input placeholder={tr("3 号线-涂布机")} />
          </Form.Item>
          <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Typography.Text type="secondary">
            {tr("停用后设备仍可连接，但管理端定向下发与重启会拒绝。")}</Typography.Text>
        </Form>
      </Modal>

      <Modal
        open={restartTarget !== null}
        title={restartTarget ? tr("重启设备：{{arg0}}", { arg0: restartTarget.displayName }) : tr("重启设备")}
        confirmLoading={saving}
        okText={tr("发送重启")}
        okButtonProps={{ danger: true, icon: <ThunderboltOutlined /> }}
        onOk={() => void submitRestart()}
        onCancel={() => setRestartTarget(null)}
      >
        <Form form={restartForm} layout="vertical">
          <Form.Item name="reason" label={tr("重启原因（可选，设备端展示）")} rules={[{ max: 256 }]}>
            <Input placeholder={tr("配置下发后重启使新参数生效")} />
          </Form.Item>
          <Alert
            type="warning"
            showIcon
            message={tr("重启会中断设备当前作业")}
            description={tr("命令只送达在线设备；设备端由客户端实现倒计时与重启动作。")}
          />
        </Form>
      </Modal>

      <Drawer
        open={detailId !== null}
        width={responsiveWidth(isMobile, 720)}
        title={detail.data ? detail.data.displayName : tr("设备详情")}
        onClose={() => setDetailId(null)}
        styles={{ body: { padding: isMobile ? 12 : 24 } }}
      >
        {detail.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={(detail.error as Error).message}
            action={
              <Button size="small" onClick={() => void detail.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        {detail.data ? <DeviceDescriptions device={detail.data} /> : null}
      </Drawer>
    </Space>
  );
}

function DeviceDescriptions({ device }: { device: DeviceDetail }) {
  const online = ONLINE_META[device.onlineStatus];
  const station = STATION_META[device.stationStatus];
  const alarm = ALARM_META[device.alarmLevel];

  return (
    <Descriptions
      size="small"
      column={{ xs: 1, md: 2 }}
      bordered
      items={[
        { key: 'shortId', label: tr("短键"), children: device.shortId },
        {
          key: 'deviceKey',
          label: tr("设备键"),
          children: (
            <Typography.Text code style={{ wordBreak: 'break-all' }}>
              {device.deviceKey}
            </Typography.Text>
          ),
        },
        {
          key: 'machineCode',
          label: tr("机器码"),
          children: <Typography.Text code>{device.machineCode}</Typography.Text>,
        },
        {
          key: 'installGuid',
          label: tr("安装标识"),
          children: device.installGuid ?? '-',
        },
        { key: 'autoName', label: tr("自动名"), children: device.autoName ?? '-' },
        { key: 'customName', label: tr("自定义名"), children: device.customName ?? '-' },
        { key: 'ip', label: tr("IP 地址"), children: device.ipAddress ?? '-' },
        { key: 'branch', label: tr("分支"), children: device.branch ?? '-' },
        { key: 'frameworkVersion', label: tr("框架版本"), children: device.frameworkVersion ?? '-' },
        {
          key: 'status',
          label: tr("在线 / 工位"),
          children: (
            <Space size={4}>
              <Tag color={online.color}>{online.label}</Tag>
              {device.onlineStatus === 'online' ? (
                <Tag color={station.color}>{station.label}</Tag>
              ) : null}
            </Space>
          ),
        },
        {
          key: 'alarm',
          label: tr("报警"),
          children: alarm.color ? (
            <Tag color={alarm.color} title={device.alarmMessage ?? undefined}>
              {alarm.label}
            </Tag>
          ) : (
            tr("无")
          ),
        },
        { key: 'alarmMessage', label: tr("报警摘要"), children: device.alarmMessage ?? '-' },
        { key: 'heartbeatSeconds', label: tr("心跳周期（秒）"), children: device.heartbeatSeconds },
        {
          key: 'firstConnectedAt',
          label: tr("首次连接"),
          children: device.firstConnectedAt
            ? formatDateTime(device.firstConnectedAt)
            : '-',
        },
        {
          key: 'lastConnectedAt',
          label: tr("本次连接"),
          children: device.lastConnectedAt
            ? formatDateTime(device.lastConnectedAt)
            : '-',
        },
        {
          key: 'lastHeartbeatAt',
          label: tr("最后心跳"),
          children: device.lastHeartbeatAt
            ? formatDateTime(device.lastHeartbeatAt)
            : '-',
        },
        {
          key: 'isEnabled',
          label: tr("启用"),
          children: device.isEnabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>,
        },
      ]}
    />
  );
}
