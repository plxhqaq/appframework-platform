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
  online: { label: '在线', color: 'green' },
  offline: { label: '离线' },
};

const STATION_META: Record<DeviceStationStatus, { label: string; color: string }> = {
  idle: { label: '待机', color: 'blue' },
  online: { label: '运行中', color: 'orange' },
};

const ALARM_META: Record<DeviceAlarmLevel, { label: string; color?: string }> = {
  none: { label: '无' },
  info: { label: '提示', color: 'blue' },
  warning: { label: '警告', color: 'orange' },
  error: { label: '错误', color: 'red' },
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
      message.success('设备已更新');
      setEditTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '保存失败');
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
      message.success('重启命令已送达');
      setRestartTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '重启失败');
    } finally {
      setSaving(false);
    }
  };

  const columns: TableColumnsType<DeviceSummary> = [
    {
      title: '设备',
      dataIndex: 'displayName',
      key: 'displayName',
      width: 220,
      render: (value: string, row: DeviceSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{value || '(未命名)'}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {row.shortId}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '机器码',
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 150,
      responsive: ['md'],
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: '分支',
      dataIndex: 'branch',
      key: 'branch',
      width: 100,
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: '框架版本',
      dataIndex: 'frameworkVersion',
      key: 'frameworkVersion',
      width: 110,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: '状态',
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
      title: '报警',
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
      title: '最后心跳',
      dataIndex: 'lastHeartbeatAt',
      key: 'lastHeartbeatAt',
      width: 170,
      responsive: ['lg'],
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '启用',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 200,
      fixed: 'right',
      render: (_: unknown, row: DeviceSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => setDetailId(row.id)}>
            详情
          </Button>
          {canManage ? (
            <Button type="link" size="small" onClick={() => openEditForm(row)}>
              编辑
            </Button>
          ) : null}
          {canRestart ? (
            <Button
              type="link"
              size="small"
              danger
              disabled={!row.isEnabled || row.onlineStatus !== 'online'}
              title={
                !row.isEnabled
                  ? '设备已停用'
                  : row.onlineStatus !== 'online'
                    ? '设备离线，无法重启'
                    : undefined
              }
              onClick={() => openRestartForm(row)}
            >
              重启
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title="设备" size="small">
        <Alert
          type="warning"
          showIcon
          message="当前账号没有可用的应用上下文"
          description="设备按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。"
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title="设备"
        extra={
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            刷新
          </Button>
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Input.Search
            key={`keyword-${appKey}`}
            allowClear
            placeholder="设备名 / 机器码"
            style={{ width: 220, maxWidth: '100%' }}
            defaultValue={keyword}
            onSearch={(value) => {
              setKeyword(value.trim() || undefined);
              setPage(1);
            }}
          />
          <Select<DeviceOnlineStatus>
            allowClear
            placeholder="全部在线状态"
            style={{ width: 160, maxWidth: '100%' }}
            value={onlineStatus}
            onChange={(value) => {
              setOnlineStatus(value);
              setPage(1);
            }}
            options={[
              { value: 'online', label: '在线' },
              { value: 'offline', label: '离线' },
            ]}
          />
          <Input.Search
            key={`branch-${appKey}`}
            allowClear
            placeholder="分支"
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
                重试
              </Button>
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
            showTotal: (total: number) => `共 ${total} 台设备`,
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: '尚无设备连接（客户端连接 /hubs/devices 后自动注册）' }}
        />
      </Card>

      <Modal
        open={editTarget !== null}
        title={editTarget ? `编辑设备：${editTarget.displayName}` : '编辑设备'}
        confirmLoading={saving}
        onOk={() => void submitEdit()}
        onCancel={() => setEditTarget(null)}
      >
        <Form form={editForm} layout="vertical">
          <Form.Item
            name="customName"
            label="自定义名（前缀拼在自动名之前，留空清除）"
            rules={[{ max: 128 }]}
          >
            <Input placeholder="3 号线-涂布机" />
          </Form.Item>
          <Form.Item name="isEnabled" label="启用" valuePropName="checked">
            <Switch />
          </Form.Item>
          <Typography.Text type="secondary">
            停用后设备仍可连接，但管理端定向下发与重启会拒绝。
          </Typography.Text>
        </Form>
      </Modal>

      <Modal
        open={restartTarget !== null}
        title={restartTarget ? `重启设备：${restartTarget.displayName}` : '重启设备'}
        confirmLoading={saving}
        okText="发送重启"
        okButtonProps={{ danger: true, icon: <ThunderboltOutlined /> }}
        onOk={() => void submitRestart()}
        onCancel={() => setRestartTarget(null)}
      >
        <Form form={restartForm} layout="vertical">
          <Form.Item name="reason" label="重启原因（可选，设备端展示）" rules={[{ max: 256 }]}>
            <Input placeholder="配置下发后重启使新参数生效" />
          </Form.Item>
          <Alert
            type="warning"
            showIcon
            message="重启会中断设备当前作业"
            description="命令只送达在线设备；设备端由客户端实现倒计时与重启动作。"
          />
        </Form>
      </Modal>

      <Drawer
        open={detailId !== null}
        width={responsiveWidth(isMobile, 720)}
        title={detail.data ? detail.data.displayName : '设备详情'}
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
                重试
              </Button>
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
        { key: 'shortId', label: '短键', children: device.shortId },
        {
          key: 'deviceKey',
          label: '设备键',
          children: (
            <Typography.Text code style={{ wordBreak: 'break-all' }}>
              {device.deviceKey}
            </Typography.Text>
          ),
        },
        {
          key: 'machineCode',
          label: '机器码',
          children: <Typography.Text code>{device.machineCode}</Typography.Text>,
        },
        {
          key: 'installGuid',
          label: '安装标识',
          children: device.installGuid ?? '-',
        },
        { key: 'autoName', label: '自动名', children: device.autoName ?? '-' },
        { key: 'customName', label: '自定义名', children: device.customName ?? '-' },
        { key: 'ip', label: 'IP 地址', children: device.ipAddress ?? '-' },
        { key: 'branch', label: '分支', children: device.branch ?? '-' },
        { key: 'frameworkVersion', label: '框架版本', children: device.frameworkVersion ?? '-' },
        {
          key: 'status',
          label: '在线 / 工位',
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
          label: '报警',
          children: alarm.color ? (
            <Tag color={alarm.color} title={device.alarmMessage ?? undefined}>
              {alarm.label}
            </Tag>
          ) : (
            '无'
          ),
        },
        { key: 'alarmMessage', label: '报警摘要', children: device.alarmMessage ?? '-' },
        { key: 'heartbeatSeconds', label: '心跳周期（秒）', children: device.heartbeatSeconds },
        {
          key: 'firstConnectedAt',
          label: '首次连接',
          children: device.firstConnectedAt
            ? new Date(device.firstConnectedAt).toLocaleString()
            : '-',
        },
        {
          key: 'lastConnectedAt',
          label: '本次连接',
          children: device.lastConnectedAt
            ? new Date(device.lastConnectedAt).toLocaleString()
            : '-',
        },
        {
          key: 'lastHeartbeatAt',
          label: '最后心跳',
          children: device.lastHeartbeatAt
            ? new Date(device.lastHeartbeatAt).toLocaleString()
            : '-',
        },
        {
          key: 'isEnabled',
          label: '启用',
          children: device.isEnabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>,
        },
      ]}
    />
  );
}
