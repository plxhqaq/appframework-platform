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
import { installationLabel, installationsApi } from '../../shared/api/installations';
import type {
  DeviceAlarmLevel,
  DeviceOnlineStatus,
  DeviceStationStatus,
  InstallationDetail,
  InstallationSummary,
  UpdateInstallationRequest,
} from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';

interface EditFormValues {
  customName: string;
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
 * 安装实例页（应用级）。
 *
 * 安装实例 = 某个应用在某物理节点上的一次安装；名称取应用内录入的设备名（管理台可覆盖），
 * 机器名/机器码属于节点只读，分支/版本/在线/工位/报警归安装实例。
 * 节点停用时即使实例启用也不可下发。
 */
export function InstallationsPage() {
  const { message } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const canManage = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.installationManage),
  );
  const canRestart = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.installationRestart),
  );

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [editForm] = Form.useForm<EditFormValues>();
  const [restartForm] = Form.useForm<RestartFormValues>();

  const [editTarget, setEditTarget] = useState<InstallationSummary | null>(null);
  const [restartTarget, setRestartTarget] = useState<InstallationSummary | null>(null);
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

  const installations = useQuery({
    queryKey: ['installations', 'list', appKey, onlineStatus, branch, keyword, page, pageSize],
    queryFn: ({ signal }) =>
      installationsApi.listInstallations(
        { onlineStatus, branch, keyword, page, pageSize },
        signal,
      ),
    enabled: appKey !== null,
  });

  const detail = useQuery({
    queryKey: ['installations', 'detail', detailId],
    queryFn: ({ signal }) => installationsApi.getInstallation(detailId!, signal),
    enabled: detailId !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['installations'] });
  };

  const openEditForm = (row: InstallationSummary) => {
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
      const body: UpdateInstallationRequest = {
        customName: values.customName?.trim() ?? '',
        isEnabled: values.isEnabled,
      };
      await installationsApi.updateInstallation(editTarget.id, body);
      message.success(tr("安装实例已更新"));
      setEditTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    } finally {
      setSaving(false);
    }
  };

  const openRestartForm = (row: InstallationSummary) => {
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
      await installationsApi.restartInstallation(restartTarget.id, {
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

  const columns: TableColumnsType<InstallationSummary> = [
    {
      title: tr("安装实例"),
      key: 'installation',
      width: 220,
      render: (_: unknown, row: InstallationSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{installationLabel(row)}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {tr("节点：{{arg0}}", { arg0: row.nodeDisplayName })}
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
      render: (_: unknown, row: InstallationSummary) => (
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
      render: (level: DeviceAlarmLevel, row: InstallationSummary) => {
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
      key: 'isEnabled',
      width: 130,
      render: (_: unknown, row: InstallationSummary) => (
        <Space size={4} wrap>
          {row.isEnabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>}
          {!row.nodeEnabled ? <Tag color="red">{tr("节点停用")}</Tag> : null}
        </Space>
      ),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 200,
      fixed: 'right',
      render: (_: unknown, row: InstallationSummary) => (
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
              disabled={!row.nodeEnabled || !row.isEnabled || row.onlineStatus !== 'online'}
              title={
                !row.nodeEnabled
                  ? tr("节点已停用，无法重启")
                  : !row.isEnabled
                    ? tr("安装实例已停用，无法重启")
                    : row.onlineStatus !== 'online'
                      ? tr("安装实例离线，无法重启")
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
      <Card title={tr("安装实例")} size="small">
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有可用的应用上下文")}
          description={tr("安装实例按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。")}
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title={tr("安装实例")}
        extra={
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            {tr("刷新")}</Button>
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Input.Search
            key={`keyword-${appKey}`}
            allowClear
            placeholder={tr("实例名 / 节点名 / 机器码 / IP")}
            style={{ width: 240, maxWidth: '100%' }}
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

        {installations.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(installations.error as Error).message}
            action={
              <Button size="small" onClick={() => void installations.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<InstallationSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={installations.isPending}
          dataSource={installations.data?.items ?? []}
          columns={columns}
          pagination={responsivePagination(isMobile, {
            current: installations.data?.page ?? page,
            pageSize: installations.data?.pageSize ?? pageSize,
            total: installations.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 个安装实例", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: tr("尚无安装实例（客户端连接 /hubs/devices 后自动注册）") }}
        />
      </Card>

      <Modal
        open={editTarget !== null}
        title={
          editTarget
            ? tr("编辑安装实例：{{arg0}}", { arg0: installationLabel(editTarget) })
            : tr("编辑安装实例")
        }
        confirmLoading={saving}
        onOk={() => void submitEdit()}
        onCancel={() => setEditTarget(null)}
      >
        <Form form={editForm} layout="vertical">
          <Form.Item
            name="customName"
            label={tr("自定义名（优先于应用内录入名展示，留空清除）")}
            rules={[{ max: 128 }]}
            extra={
              editTarget
                ? tr("应用内录入名：{{arg0}}", { arg0: editTarget.clientName || "-" })
                : undefined
            }
          >
            <Input placeholder={tr("例如：一号工位")} allowClear />
          </Form.Item>
          <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Typography.Text type="secondary">
            {tr("应用级开关；节点停用时即使这里启用也不会注册与下发。")}</Typography.Text>
        </Form>
      </Modal>

      <Modal
        open={restartTarget !== null}
        title={
          restartTarget
            ? tr("重启安装实例：{{arg0}}", { arg0: installationLabel(restartTarget) })
            : tr("重启安装实例")
        }
        confirmLoading={saving}
        okText={tr("发送重启")}
        okButtonProps={{ danger: true, icon: <ThunderboltOutlined /> }}
        onOk={() => void submitRestart()}
        onCancel={() => setRestartTarget(null)}
      >
        <Form form={restartForm} layout="vertical">
          <Form.Item name="reason" label={tr("重启原因（可选，客户端展示）")} rules={[{ max: 256 }]}>
            <Input placeholder={tr("配置下发后重启使新参数生效")} />
          </Form.Item>
          <Alert
            type="warning"
            showIcon
            message={tr("重启只会中断该安装实例的当前作业")}
            description={tr("命令只送达在线安装实例；同机其他应用不受影响。")}
          />
        </Form>
      </Modal>

      <Drawer
        open={detailId !== null}
        width={responsiveWidth(isMobile, 720)}
        title={
          detail.data
            ? tr("安装实例：{{arg0}}", { arg0: installationLabel(detail.data) })
            : tr("安装实例详情")
        }
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
        {detail.data ? <InstallationDescriptions installation={detail.data} /> : null}
      </Drawer>
    </Space>
  );
}

function InstallationDescriptions({ installation }: { installation: InstallationDetail }) {
  const online = ONLINE_META[installation.onlineStatus];
  const station = STATION_META[installation.stationStatus];
  const alarm = ALARM_META[installation.alarmLevel];

  return (
    <Descriptions
      size="small"
      column={{ xs: 1, md: 2 }}
      bordered
      items={[
        { key: 'shortId', label: tr("短键"), children: installation.shortId },
        {
          key: 'displayName',
          label: tr("展示名"),
          children: installation.displayName || installation.shortId,
        },
        {
          key: 'clientName',
          label: tr("应用内录入名"),
          children: installation.clientName ?? '-',
        },
        {
          key: 'customName',
          label: tr("管理端自定义名"),
          children: installation.customName ?? '-',
        },
        {
          key: 'node',
          label: tr("节点"),
          children: installation.nodeDisplayName,
        },
        { key: 'nodeId', label: tr("节点 ID"), children: installation.nodeId },
        {
          key: 'installGuid',
          label: tr("安装标识"),
          children: installation.installGuid,
        },
        {
          key: 'machineCode',
          label: tr("机器码"),
          children: <Typography.Text code>{installation.machineCode}</Typography.Text>,
        },
        { key: 'branch', label: tr("分支"), children: installation.branch ?? '-' },
        {
          key: 'frameworkVersion',
          label: tr("框架版本"),
          children: installation.frameworkVersion ?? '-',
        },
        {
          key: 'status',
          label: tr("在线 / 工位"),
          children: (
            <Space size={4}>
              <Tag color={online.color}>{online.label}</Tag>
              {installation.onlineStatus === 'online' ? (
                <Tag color={station.color}>{station.label}</Tag>
              ) : null}
            </Space>
          ),
        },
        {
          key: 'alarm',
          label: tr("报警"),
          children: alarm.color ? (
            <Tag color={alarm.color} title={installation.alarmMessage ?? undefined}>
              {alarm.label}
            </Tag>
          ) : (
            tr("无")
          ),
        },
        {
          key: 'alarmMessage',
          label: tr("报警摘要"),
          children: installation.alarmMessage ?? '-',
        },
        {
          key: 'heartbeatSeconds',
          label: tr("心跳周期（秒）"),
          children: installation.heartbeatSeconds,
        },
        {
          key: 'firstConnectedAt',
          label: tr("首次连接"),
          children: installation.firstConnectedAt
            ? formatDateTime(installation.firstConnectedAt)
            : '-',
        },
        {
          key: 'lastConnectedAt',
          label: tr("本次连接"),
          children: installation.lastConnectedAt
            ? formatDateTime(installation.lastConnectedAt)
            : '-',
        },
        {
          key: 'lastHeartbeatAt',
          label: tr("最后心跳"),
          children: installation.lastHeartbeatAt
            ? formatDateTime(installation.lastHeartbeatAt)
            : '-',
        },
        {
          key: 'isEnabled',
          label: tr("安装实例启用"),
          children: installation.isEnabled ? (
            <Tag color="green">{tr("启用")}</Tag>
          ) : (
            <Tag>{tr("停用")}</Tag>
          ),
        },
        {
          key: 'nodeEnabled',
          label: tr("节点启用"),
          children: installation.nodeEnabled ? (
            <Tag color="green">{tr("启用")}</Tag>
          ) : (
            <Tag color="red">{tr("停用")}</Tag>
          ),
        },
        {
          key: 'createdAt',
          label: tr("创建时间"),
          children: formatDateTime(installation.createdAt),
        },
        {
          key: 'updatedAt',
          label: tr("更新时间"),
          children: formatDateTime(installation.updatedAt),
        },
      ]}
    />
  );
}
