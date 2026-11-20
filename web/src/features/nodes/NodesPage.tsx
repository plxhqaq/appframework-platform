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
  Tabs,
  Tag,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { nodesApi } from '../../shared/api/nodes';
import type {
  DeviceAlarmLevel,
  DeviceOnlineStatus,
  DeviceStationStatus,
  InstallationSummary,
  NodeDetail,
  NodeSummary,
  UpdateNodeRequest,
} from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { renderTableExpandIcon } from '../../shared/ui/TableExpandIcon';
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
 * 物理节点页（平台级，跨应用）。
 *
 * 节点状态由该节点全部安装实例聚合：任一在线即在线，任一运行即运行中，
 * 报警取最严重级别。应用则是节点详情里的子标签，一台机器装三个产品只占一行。
 */
export function NodesPage() {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.nodeManage));
  const canRestart = useAuthStore((state) => state.hasPermission(PERMISSIONS.nodeRestart));

  const [editForm] = Form.useForm<EditFormValues>();
  const [restartForm] = Form.useForm<RestartFormValues>();

  const [editTarget, setEditTarget] = useState<NodeSummary | null>(null);
  const [restartTarget, setRestartTarget] = useState<NodeSummary | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [onlineStatus, setOnlineStatus] = useState<DeviceOnlineStatus | undefined>();
  const [isEnabled, setIsEnabled] = useState<boolean | undefined>();
  const [keyword, setKeyword] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [saving, setSaving] = useState(false);

  const nodes = useQuery({
    queryKey: ['nodes', 'list', onlineStatus, isEnabled, keyword, page, pageSize],
    queryFn: ({ signal }) =>
      nodesApi.listNodes({ onlineStatus, isEnabled, keyword, page, pageSize }, signal),
    // 跃迁事件失效为主，60s 兜底。
    refetchInterval: 60_000,
  });

  const detail = useQuery({
    queryKey: ['nodes', 'detail', detailId],
    queryFn: ({ signal }) => nodesApi.getNode(detailId!, signal),
    enabled: detailId !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['nodes'] });
    await queryClient.invalidateQueries({ queryKey: ['installations'] });
  };

  const openEditForm = (row: NodeSummary) => {
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
      const body: UpdateNodeRequest = {
        customName: values.customName?.trim() ?? '',
        isEnabled: values.isEnabled,
      };
      await nodesApi.updateNode(editTarget.id, body);
      message.success(tr("节点已更新"));
      setEditTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    } finally {
      setSaving(false);
    }
  };

  const deleteNodeRow = async (row: NodeSummary) => {
    try {
      await nodesApi.deleteNode(row.id);
      message.success(tr("节点 {{arg0}} 已删除", { arg0: row.displayName }));
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("删除失败"));
    }
  };

  const confirmDeleteNode = (row: NodeSummary) => {
    modal.confirm({
      title: tr("删除节点 {{arg0}}？", { arg0: row.displayName }),
      content: tr("只有不再挂有任何安装实例的节点可删除；授权与会话按机器码留痕，不随节点行删除而清除。此操作不可恢复。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => deleteNodeRow(row),
    });
  };

  const openRestartForm = (row: NodeSummary) => {
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
      const result = await nodesApi.restartNode(restartTarget.id, {
        reason: values.reason?.trim() || null,
      });
      message.success(
        tr("重启命令已送达 {{arg0}} 个在线安装实例", { arg0: result.installationCount }),
      );
      setRestartTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("重启失败"));
    } finally {
      setSaving(false);
    }
  };

  const columns: TableColumnsType<NodeSummary> = [
    {
      title: tr("节点"),
      dataIndex: 'displayName',
      key: 'displayName',
      width: 220,
      render: (value: string, row: NodeSummary) => (
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
      title: tr("应用"),
      dataIndex: 'appKeys',
      key: 'appKeys',
      width: 180,
      responsive: ['lg'],
      render: (value: string[]) =>
        value.length > 0 ? (
          <Space size={4} wrap>
            {value.map((appKey) => (
              <Tag key={appKey}>{appKey}</Tag>
            ))}
          </Space>
        ) : (
          '-'
        ),
    },
    {
      title: tr("安装实例"),
      key: 'installations',
      width: 100,
      align: 'center',
      render: (_: unknown, row: NodeSummary) =>
        `${row.onlineInstallationCount} / ${row.installationCount}`,
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 150,
      render: (_: unknown, row: NodeSummary) => (
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
      render: (level: DeviceAlarmLevel, row: NodeSummary) => {
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
      width: 250,
      fixed: 'right',
      render: (_: unknown, row: NodeSummary) => (
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
                  ? tr("节点已停用")
                  : row.onlineStatus !== 'online'
                    ? tr("节点离线，无法重启")
                    : undefined
              }
              onClick={() => openRestartForm(row)}
            >
              {tr("重启")}</Button>
          ) : null}
          {canManage ? (
            <Button
              type="link"
              size="small"
              danger
              disabled={row.installationCount > 0}
              title={row.installationCount > 0 ? tr("仍有 {{arg0}} 个安装实例，需先删除", { arg0: row.installationCount }) : undefined}
              onClick={() => confirmDeleteNode(row)}
            >
              {tr("删除")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title={tr("节点")}
        extra={
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            {tr("刷新")}</Button>
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Input.Search
            allowClear
            placeholder={tr("节点名 / 机器码 / IP")}
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
          <Select<'enabled' | 'disabled'>
            allowClear
            placeholder={tr("全部启用状态")}
            style={{ width: 160, maxWidth: '100%' }}
            value={isEnabled === undefined ? undefined : isEnabled ? 'enabled' : 'disabled'}
            onChange={(value) => {
              setIsEnabled(value === undefined ? undefined : value === 'enabled');
              setPage(1);
            }}
            options={[
              { value: 'enabled', label: tr("启用") },
              { value: 'disabled', label: tr("停用") },
            ]}
          />
        </Space>

        {nodes.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(nodes.error as Error).message}
            action={
              <Button size="small" onClick={() => void nodes.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<NodeSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={nodes.isPending}
          dataSource={nodes.data?.items ?? []}
          columns={columns}
          pagination={responsivePagination(isMobile, {
            current: nodes.data?.page ?? page,
            pageSize: nodes.data?.pageSize ?? pageSize,
            total: nodes.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 个节点", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: tr("尚无节点注册（客户端连接 /hubs/devices 后自动创建）") }}
        />
      </Card>

      <Modal
        open={editTarget !== null}
        title={editTarget ? tr("编辑节点：{{arg0}}", { arg0: editTarget.displayName }) : tr("编辑节点")}
        confirmLoading={saving}
        onOk={() => void submitEdit()}
        onCancel={() => setEditTarget(null)}
      >
        <Form form={editForm} layout="vertical">
          <Form.Item
            name="customName"
            label={tr("自定义名（优先于机器名展示，留空清除）")}
            rules={[{ max: 128 }]}
          >
            <Input placeholder={tr("3 号线-涂布机")} />
          </Form.Item>
          <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Typography.Text type="secondary">
            {tr("停用节点后，该机器上全部应用都会被拒绝注册/心跳与定向下发。")}</Typography.Text>
        </Form>
      </Modal>

      <Modal
        open={restartTarget !== null}
        title={restartTarget ? tr("重启节点：{{arg0}}", { arg0: restartTarget.displayName }) : tr("重启节点")}
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
            message={tr("该节点全部在线安装实例都会收到重启命令")}
            description={
              restartTarget
                ? tr("将发送给 {{arg0}} 个在线安装实例；离线实例收不到命令。", {
                    arg0: restartTarget.onlineInstallationCount,
                  })
                : undefined
            }
          />
        </Form>
      </Modal>

      <Drawer
        open={detailId !== null}
        width={responsiveWidth(isMobile, 960)}
        title={detail.data ? detail.data.displayName : tr("节点详情")}
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
        {detail.data ? <NodeDetailView node={detail.data} /> : null}
      </Drawer>
    </Space>
  );
}

function NodeDetailView({ node }: { node: NodeDetail }) {
  const online = ONLINE_META[node.onlineStatus];
  const station = STATION_META[node.stationStatus];
  const alarm = ALARM_META[node.alarmLevel];

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Descriptions
        size="small"
        column={{ xs: 1, md: 2 }}
        bordered
        items={[
          { key: 'shortId', label: tr("短键"), children: node.shortId },
          {
            key: 'machineCode',
            label: tr("机器码"),
            children: <Typography.Text code>{node.machineCode}</Typography.Text>,
          },
          { key: 'machineName', label: tr("机器名"), children: node.autoName ?? '-' },
          { key: 'customName', label: tr("自定义名"), children: node.customName ?? '-' },
          { key: 'ip', label: tr("IP 地址"), children: node.ipAddress ?? '-' },
          {
            key: 'status',
            label: tr("在线 / 工位"),
            children: (
              <Space size={4}>
                <Tag color={online.color}>{online.label}</Tag>
                {node.onlineStatus === 'online' ? (
                  <Tag color={station.color}>{station.label}</Tag>
                ) : null}
              </Space>
            ),
          },
          {
            key: 'alarm',
            label: tr("报警"),
            children: alarm.color ? (
              <Tag color={alarm.color} title={node.alarmMessage ?? undefined}>
                {alarm.label}
              </Tag>
            ) : (
              tr("无")
            ),
          },
          { key: 'alarmMessage', label: tr("报警摘要"), children: node.alarmMessage ?? '-' },
          {
            key: 'firstSeenAt',
            label: tr("首次出现"),
            children: node.firstSeenAt ? formatDateTime(node.firstSeenAt) : '-',
          },
          {
            key: 'lastSeenAt',
            label: tr("最近上报"),
            children: node.lastSeenAt ? formatDateTime(node.lastSeenAt) : '-',
          },
          {
            key: 'lastHeartbeatAt',
            label: tr("最后心跳"),
            children: node.lastHeartbeatAt ? formatDateTime(node.lastHeartbeatAt) : '-',
          },
          {
            key: 'isEnabled',
            label: tr("启用"),
            children: node.isEnabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>,
          },
          {
            key: 'createdAt',
            label: tr("创建时间"),
            children: formatDateTime(node.createdAt),
          },
          {
            key: 'updatedAt',
            label: tr("更新时间"),
            children: formatDateTime(node.updatedAt),
          },
        ]}
      />

      <InstallationTabs installations={node.installations} />
    </Space>
  );
}

function InstallationTabs({ installations }: { installations: InstallationSummary[] }) {
  if (installations.length === 0) {
    return <Alert type="info" showIcon message={tr("该节点尚无安装实例")} />;
  }

  const groups = new Map<string, InstallationSummary[]>();
  installations.forEach((installation) => {
    const list = groups.get(installation.appKey) ?? [];
    list.push(installation);
    groups.set(installation.appKey, list);
  });

  const columns: TableColumnsType<InstallationSummary> = [
    {
      title: tr("安装实例"),
      key: 'installation',
      width: 180,
      render: (_: unknown, row: InstallationSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{row.displayName || tr("(未命名)")}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {row.shortId}
          </Typography.Text>
        </Space>
      ),
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
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 150,
      render: (_: unknown, row: InstallationSummary) => (
        <Space size={4} wrap>
          {!row.nodeEnabled ? (
            <Tag color="red">{tr("节点停用")}</Tag>
          ) : !row.isEnabled ? (
            <Tag>{tr("已停用")}</Tag>
          ) : row.onlineStatus === 'offline' ? (
            <Tag>{tr("离线")}</Tag>
          ) : (
            <Tag color={row.stationStatus === 'idle' ? 'green' : 'orange'}>
              {row.stationStatus === 'idle' ? tr("在线/待机") : tr("运行中")}
            </Tag>
          )}
        </Space>
      ),
    },
    {
      title: tr("报警"),
      dataIndex: 'alarmLevel',
      key: 'alarmLevel',
      width: 100,
      responsive: ['md'],
      render: (level: DeviceAlarmLevel) => {
        const meta = ALARM_META[level];
        return meta.color ? <Tag color={meta.color}>{meta.label}</Tag> : meta.label;
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
  ];

  const appKeys = [...groups.keys()].sort((left, right) => left.localeCompare(right));

  return (
    <Tabs
      size="small"
      items={appKeys.map((appKey) => ({
        key: appKey,
        label: `${appKey}（${groups.get(appKey)?.length ?? 0}）`,
        children: (
          <Table<InstallationSummary>
            rowKey="id"
            size="small"
            pagination={false}
            dataSource={groups.get(appKey) ?? []}
            columns={columns}
            scroll={{ x: 'max-content' }}
            expandable={{
              expandIcon: renderTableExpandIcon,
              expandedRowRender: (row) => (
                <Descriptions
                  size="small"
                  column={{ xs: 1, md: 2 }}
                  items={[
                    { key: 'installGuid', label: tr("安装标识"), children: row.installGuid },
                    {
                      key: 'machineCode',
                      label: tr("机器码"),
                      children: <Typography.Text code>{row.machineCode}</Typography.Text>,
                    },
                    {
                      key: 'clientName',
                      label: tr("应用内录入名"),
                      children: row.clientName ?? '-',
                    },
                    {
                      key: 'customName',
                      label: tr("管理端自定义名"),
                      children: row.customName ?? '-',
                    },
                    {
                      key: 'isEnabled',
                      label: tr("安装实例启用"),
                      children: row.isEnabled ? tr("启用") : tr("停用"),
                    },
                    { key: 'nodeEnabled', label: tr("节点启用"), children: row.nodeEnabled ? tr("启用") : tr("停用") },
                    { key: 'alarmMessage', label: tr("报警摘要"), children: row.alarmMessage ?? '-' },
                    {
                      key: 'heartbeatSeconds',
                      label: tr("心跳周期（秒）"),
                      children: row.heartbeatSeconds,
                    },
                    {
                      key: 'lastConnectedAt',
                      label: tr("本次连接"),
                      children: row.lastConnectedAt ? formatDateTime(row.lastConnectedAt) : '-',
                    },
                  ]}
                />
              ),
            }}
          />
        ),
      }))}
    />
  );
}
