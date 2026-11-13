import { AppstoreOutlined, PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Empty,
  Select,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import { useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { ReleaseChannel, ReleaseSummary, ReleaseStatus } from '../../shared/api/types';
import { formatBytes } from '../../shared/format';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { ChannelFormModal } from './ChannelFormModal';
import { ReleaseCreateModal } from './ReleaseCreateModal';
import { ReleaseDetailDrawer } from './ReleaseDetailDrawer';

const STATUS_META: Record<ReleaseStatus, { label: string; color?: string }> = {
  draft: { label: '草稿' },
  published: { label: '已发布', color: 'green' },
  archived: { label: '已归档', color: 'orange' },
};

/**
 * 版本与渠道页。
 *
 * 应用隔离轴来自令牌：平台管理员未显式进入应用时落在首个启用应用，
 * 页面顶部的"当前应用"选择器通过 `/admin/auth/context` 切换上下文。
 */
export function VersionsPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const switchAppContext = useAuthStore((state) => state.switchAppContext);
  const switching = useAuthStore((state) => state.pending);
  const canManageChannels = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsChannelManage));
  const canPublish = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsReleasePublish));
  const canArchive = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsReleaseArchive));
  const canUpload = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsArtifactUpload));

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [channelFormOpen, setChannelFormOpen] = useState(false);
  const [editingChannel, setEditingChannel] = useState<ReleaseChannel | null>(null);
  const [releaseFormOpen, setReleaseFormOpen] = useState(false);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [channelFilter, setChannelFilter] = useState<string | undefined>();
  const [statusFilter, setStatusFilter] = useState<string | undefined>();

  const channels = useQuery({
    queryKey: ['versions', 'channels', appKey],
    queryFn: ({ signal }) => versionsApi.listChannels(signal),
    enabled: appKey !== null,
  });

  const releases = useQuery({
    queryKey: ['versions', 'releases', appKey, channelFilter, statusFilter],
    queryFn: ({ signal }) =>
      versionsApi.listReleases(
        { channelKey: channelFilter, status: statusFilter, pageSize: 50 },
        signal,
      ),
    enabled: appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['versions'] });
  };

  const handleSwitchContext = async (next: string) => {
    if (next === appKey) {
      return;
    }

    try {
      const updated = await switchAppContext(next);
      message.success(`已进入应用 ${updated.appKey ?? next}`);
      setChannelFilter(undefined);
      setStatusFilter(undefined);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '切换应用失败');
    }
  };

  const mutateRelease = async (action: 'publish' | 'archive', row: ReleaseSummary) => {
    try {
      if (action === 'publish') {
        await versionsApi.publishRelease(row.id);
        message.success(`版本 ${row.version} 已发布`);
      } else {
        await versionsApi.archiveRelease(row.id);
        message.success(`版本 ${row.version} 已归档`);
      }
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '操作失败');
    }
  };

  const channelColumns = [
    {
      title: '渠道键',
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (key: string) => <Typography.Text code>{key}</Typography.Text>,
    },
    { title: '名称', dataIndex: 'name', key: 'name', width: 160 },
    {
      title: '说明',
      dataIndex: 'description',
      key: 'description',
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: '最新版本',
      dataIndex: 'latestVersion',
      key: 'latestVersion',
      width: 120,
      render: (value: string | null) =>
        value ? <Tag color="blue">{value}</Tag> : <Typography.Text type="secondary">未发布</Typography.Text>,
    },
    { title: '版本数', dataIndex: 'releaseCount', key: 'releaseCount', width: 80 },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 100,
      render: (_: unknown, channel: ReleaseChannel) =>
        canManageChannels ? (
          <Button
            type="link"
            size="small"
            onClick={() => {
              setEditingChannel(channel);
              setChannelFormOpen(true);
            }}
          >
            编辑
          </Button>
        ) : null,
    },
  ];

  const releaseColumns = [
    { title: '渠道', dataIndex: 'channelKey', key: 'channelKey', width: 120 },
    {
      title: '版本',
      dataIndex: 'version',
      key: 'version',
      width: 140,
      render: (value: string) => <Typography.Text strong>{value}</Typography.Text>,
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: ReleaseStatus) => {
        const meta = STATUS_META[status];
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    { title: '产物', dataIndex: 'artifactCount', key: 'artifactCount', width: 70 },
    { title: '文件数', dataIndex: 'fileCount', key: 'fileCount', width: 80 },
    {
      title: '总大小',
      dataIndex: 'totalSizeBytes',
      key: 'totalSizeBytes',
      width: 110,
      render: (value: number) => formatBytes(value),
    },
    {
      title: '发布时间',
      dataIndex: 'publishedAt',
      key: 'publishedAt',
      width: 170,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '操作',
      key: 'actions',
      width: 180,
      render: (_: unknown, row: ReleaseSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => setDetailId(row.id)}>
            详情
          </Button>
          {canPublish && row.status === 'draft' ? (
            <Button type="link" size="small" onClick={() => void mutateRelease('publish', row)}>
              发布
            </Button>
          ) : null}
          {canArchive && row.status !== 'archived' ? (
            <Button type="link" size="small" danger onClick={() => void mutateRelease('archive', row)}>
              归档
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title="版本与渠道" size="small">
        <Alert
          type="warning"
          showIcon
          message="当前账号没有可用的应用上下文"
          description="版本数据按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。"
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card size="small">
        <Space wrap>
          <AppstoreOutlined />
          <Typography.Text>当前应用</Typography.Text>
          <Select
            value={appKey}
            loading={switching}
            style={{ minWidth: 200 }}
            options={appKeys.map((key) => ({ value: key, label: key }))}
            onChange={(value) => void handleSwitchContext(value)}
            disabled={switching || appKeys.length <= 1}
          />
          <Tooltip title="应用上下文来自令牌；切换后版本、授权等应用级模块的数据范围随之切换。">
            <Typography.Text type="secondary">
              版本与渠道按应用隔离，前端不传 appKey。
            </Typography.Text>
          </Tooltip>
        </Space>
      </Card>

      <Card
        size="small"
        title="渠道"
        extra={
          canManageChannels ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => {
                setEditingChannel(null);
                setChannelFormOpen(true);
              }}
            >
              新建渠道
            </Button>
          ) : null
        }
      >
        {channels.isError ? (
          <Alert type="error" showIcon style={{ marginBottom: 16 }} message={(channels.error as Error).message} />
        ) : null}
        <Table<ReleaseChannel>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={channels.isPending}
          dataSource={channels.data ?? []}
          columns={channelColumns}
          pagination={false}
          locale={{ emptyText: '尚未创建渠道（如 stable / beta）' }}
        />
      </Card>

      <Card
        size="small"
        title="版本"
        extra={
          canPublish && canUpload ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setReleaseFormOpen(true)}>
              新建版本
            </Button>
          ) : null
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Select
            allowClear
            placeholder="全部渠道"
            style={{ width: 180 }}
            value={channelFilter}
            onChange={setChannelFilter}
            options={(channels.data ?? []).map((channel) => ({
              value: channel.key,
              label: `${channel.name}（${channel.key}）`,
            }))}
          />
          <Select
            allowClear
            placeholder="全部状态"
            style={{ width: 160 }}
            value={statusFilter}
            onChange={setStatusFilter}
            options={[
              { value: 'draft', label: '草稿' },
              { value: 'published', label: '已发布' },
              { value: 'archived', label: '已归档' },
            ]}
          />
          <Button onClick={() => void refresh()}>刷新</Button>
        </Space>

        {releases.isError ? (
          <Alert type="error" showIcon style={{ marginBottom: 16 }} message={(releases.error as Error).message} />
        ) : null}
        <Table<ReleaseSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={releases.isPending}
          dataSource={releases.data?.items ?? []}
          columns={releaseColumns}
          pagination={false}
          locale={{ emptyText: <Empty description="尚无版本；上传文件后按清单创建版本" /> }}
        />
      </Card>

      <ChannelFormModal
        open={channelFormOpen}
        channel={editingChannel}
        onClose={() => setChannelFormOpen(false)}
        onSaved={() => void refresh()}
      />

      <ReleaseCreateModal
        open={releaseFormOpen}
        channels={(channels.data ?? []).filter((channel) => channel.isEnabled)}
        onClose={() => setReleaseFormOpen(false)}
        onSaved={() => void refresh()}
      />

      <ReleaseDetailDrawer
        releaseId={detailId}
        canPublish={canPublish}
        canArchive={canArchive}
        onClose={() => setDetailId(null)}
        onChanged={() => void refresh()}
      />
    </Space>
  );
}
