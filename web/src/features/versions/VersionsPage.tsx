import { tr } from '../../i18n/translate';
import { CloudUploadOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons';
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
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { ReleaseChannel, ReleaseSummary, ReleaseStatus } from '../../shared/api/types';
import { formatBytes, formatDateTime } from '../../shared/format';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';
import { ChannelFormModal } from './ChannelFormModal';
import { DriverMediaPanel } from './DriverMediaPanel';
import { PackageUploadModal } from './PackageUploadModal';
import { ReleaseCreateModal } from './ReleaseCreateModal';
import { ReleaseDetailDrawer } from './ReleaseDetailDrawer';
import { ReleasePublishModal } from './ReleasePublishModal';

const STATUS_META: Record<ReleaseStatus, { label: string; color?: string }> = {
  draft: { get label() { return tr("草稿"); } },
  published: { get label() { return tr("已发布"); }, color: 'green' },
  archived: { get label() { return tr("已归档"); }, color: 'orange' },
};

/**
 * 版本与渠道页。
 *
 * 应用隔离轴来自令牌：平台管理员未显式进入应用时落在首个启用应用，
 * 上下文切换统一在顶部 Header 完成（见 AppContextSelect）。
 */
export function VersionsPage() {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const canManageChannels = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsChannelManage));
  const canPublish = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsReleasePublish));
  const canArchive = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsReleaseArchive));
  const canDelete = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsReleaseDelete));
  const canUpload = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsArtifactUpload));
  const canDownload = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.versionsArtifactDownload),
  );

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [channelFormOpen, setChannelFormOpen] = useState(false);
  const [editingChannel, setEditingChannel] = useState<ReleaseChannel | null>(null);
  const [releaseFormOpen, setReleaseFormOpen] = useState(false);
  const [packageFormOpen, setPackageFormOpen] = useState(false);
  const [publishId, setPublishId] = useState<string | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [channelFilter, setChannelFilter] = useState<string | undefined>();
  const [statusFilter, setStatusFilter] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  // 上下文在 Header 切换；旧应用的筛选与页码不能沿用
  useEffect(() => {
    setChannelFilter(undefined);
    setStatusFilter(undefined);
    setPage(1);
  }, [appKey]);

  const channels = useQuery({
    queryKey: ['versions', 'channels', appKey],
    queryFn: ({ signal }) => versionsApi.listChannels(signal),
    enabled: appKey !== null,
  });

  const releases = useQuery({
    queryKey: ['versions', 'releases', appKey, channelFilter, statusFilter, page, pageSize],
    queryFn: ({ signal }) =>
      versionsApi.listReleases(
        { channelKey: channelFilter, status: statusFilter, page, pageSize },
        signal,
      ),
    enabled: appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['versions'] });
  };

  const archiveRelease = async (row: ReleaseSummary) => {
    try {
      await versionsApi.archiveRelease(row.id);
      message.success(tr("版本 {{arg0}} 已归档", { arg0: row.version }));
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("操作失败"));
    }
  };

  const confirmArchive = (row: ReleaseSummary) => {
    modal.confirm({
      title: tr("归档版本 {{arg0}}？", { arg0: row.version }),
      content: tr("归档后客户端 check 会回退到上一个已发布版本。如果只是误操作，重新上传同版本发布包即可恢复为草稿。"),
      okText: tr("归档"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => archiveRelease(row),
    });
  };

  const deleteReleaseRow = async (row: ReleaseSummary) => {
    try {
      await versionsApi.deleteRelease(row.id);
      message.success(tr("版本 {{arg0}} 已删除", { arg0: row.version }));
      if (detailId === row.id) {
        setDetailId(null);
      }
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("操作失败"));
    }
  };

  const confirmDelete = (row: ReleaseSummary) => {
    modal.confirm({
      title: row.status === 'draft'
        ? tr("丢弃草稿版本 {{arg0}}？", { arg0: row.version })
        : tr("彻底删除已归档版本 {{arg0}}？", { arg0: row.version }),
      content: row.status === 'draft'
        ? tr("发布包登记与文件清单会一并移除，不再被其它版本引用的已落盘内容立即回收。此操作不可恢复。")
        : tr("发布包、运行时产物与文件清单会一并删除，不再被引用的安装包与文件内容将从磁盘回收以节省空间。此操作不可恢复。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => deleteReleaseRow(row),
    });
  };

  const channelColumns: TableColumnsType<ReleaseChannel> = [
    {
      title: tr("渠道键"),
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (key: string) => <Typography.Text code>{key}</Typography.Text>,
    },
    {
      title: tr("名称"),
      dataIndex: 'name',
      key: 'name',
      width: 180,
      render: (name: string, channel: ReleaseChannel) => (
        <Space size={4}>
          <span>{name}</span>
          {channel.autoCreated ? <Tag color="orange">{tr("自动")}</Tag> : null}
        </Space>
      ),
    },
    {
      title: tr("受众标签"),
      dataIndex: 'audienceTags',
      key: 'audienceTags',
      width: 220,
      responsive: ['md'],
      render: (tags: string[] | null | undefined) =>
        tags && tags.length > 0 ? (
          <Space size={[0, 4]} wrap>
            {tags.map((tag) => (
              <Tag key={tag}>{tag}</Tag>
            ))}
          </Space>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
    {
      title: tr("说明"),
      dataIndex: 'description',
      key: 'description',
      responsive: ['md'],
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: tr("最新版本"),
      dataIndex: 'latestVersion',
      key: 'latestVersion',
      width: 120,
      responsive: ['md'],
      render: (value: string | null) =>
        value ? <Tag color="blue">{value}</Tag> : <Typography.Text type="secondary">{tr("未发布")}</Typography.Text>,
    },
    {
      title: tr("版本数"),
      dataIndex: 'releaseCount',
      key: 'releaseCount',
      width: 80,
      responsive: ['md'],
    },
    {
      title: tr("状态"),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 100,
      fixed: 'right',
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
            {tr("编辑")}</Button>
        ) : null,
    },
  ];

  const releaseColumns: TableColumnsType<ReleaseSummary> = [
    { title: tr("渠道"), dataIndex: 'channelKey', key: 'channelKey', width: 120 },
    {
      title: tr("版本"),
      dataIndex: 'version',
      key: 'version',
      width: 140,
      render: (value: string) => <Typography.Text strong>{value}</Typography.Text>,
    },
    {
      title: tr("状态"),
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: ReleaseStatus) => {
        const meta = STATUS_META[status];
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: tr("产物"),
      dataIndex: 'artifactCount',
      key: 'artifactCount',
      width: 70,
      responsive: ['md'],
    },
    {
      title: tr("安装包"),
      dataIndex: 'packageCount',
      key: 'packageCount',
      width: 80,
      responsive: ['md'],
    },
    {
      title: tr("文件数"),
      dataIndex: 'fileCount',
      key: 'fileCount',
      width: 80,
      responsive: ['md'],
    },
    {
      title: tr("总大小"),
      dataIndex: 'totalSizeBytes',
      key: 'totalSizeBytes',
      width: 110,
      responsive: ['md'],
      render: (value: number) => formatBytes(value),
    },
    {
      title: tr("发布时间"),
      dataIndex: 'publishedAt',
      key: 'publishedAt',
      width: 170,
      responsive: ['md'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 220,
      fixed: 'right',
      render: (_: unknown, row: ReleaseSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => setDetailId(row.id)}>
            {tr("详情")}</Button>
          {canPublish && row.status === 'draft' ? (
            <Button type="link" size="small" onClick={() => setPublishId(row.id)}>
              {tr("发布")}</Button>
          ) : null}
          {canArchive && row.status !== 'archived' ? (
            <Button type="link" size="small" danger onClick={() => confirmArchive(row)}>
              {tr("归档")}</Button>
          ) : null}
          {canDelete && row.status !== 'published' ? (
            <Button type="link" size="small" danger onClick={() => confirmDelete(row)}>
              {tr("删除")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title={tr("版本与渠道")} size="small">
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有可用的应用上下文")}
          description={tr("版本数据按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。")}
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title={tr("渠道")}
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
              {tr("新建渠道")}</Button>
          ) : null
        }
      >
        {channels.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(channels.error as Error).message}
            action={
              <Button size="small" onClick={() => void channels.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<ReleaseChannel>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={channels.isPending}
          dataSource={channels.data ?? []}
          columns={channelColumns}
          pagination={false}
          locale={{ emptyText: tr("尚未创建渠道（如 stable / beta）") }}
        />
      </Card>

      <Card
        size="small"
        title={tr("版本")}
        extra={
          <Space>
            {canUpload ? (
              <Button icon={<CloudUploadOutlined />} onClick={() => setPackageFormOpen(true)}>
                {tr("上传安装包")}</Button>
            ) : null}
            {canPublish && canUpload ? (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setReleaseFormOpen(true)}>
                {tr("新建版本")}</Button>
            ) : null}
          </Space>
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Select
            allowClear
            placeholder={tr("全部渠道")}
            style={{ width: 180, maxWidth: '100%' }}
            value={channelFilter}
            onChange={(value) => {
              setChannelFilter(value);
              setPage(1);
            }}
            options={(channels.data ?? []).map((channel) => ({
              value: channel.key,
              label: `${channel.name}（${channel.key}）`,
            }))}
          />
          <Select
            allowClear
            placeholder={tr("全部状态")}
            style={{ width: 160, maxWidth: '100%' }}
            value={statusFilter}
            onChange={(value) => {
              setStatusFilter(value);
              setPage(1);
            }}
            options={[
              { value: 'draft', label: tr("草稿") },
              { value: 'published', label: tr("已发布") },
              { value: 'archived', label: tr("已归档") },
            ]}
          />
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            {tr("刷新")}</Button>
        </Space>

        {releases.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(releases.error as Error).message}
            action={
              <Button size="small" onClick={() => void releases.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<ReleaseSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={releases.isPending}
          dataSource={releases.data?.items ?? []}
          columns={releaseColumns}
          pagination={responsivePagination(isMobile, {
            current: releases.data?.page ?? page,
            pageSize: releases.data?.pageSize ?? pageSize,
            total: releases.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 个版本", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: <Empty description={tr("尚无版本；上传文件后按清单创建版本")} /> }}
        />
      </Card>

      <DriverMediaPanel
        appKey={appKey}
        canUpload={canUpload}
        canDownload={canDownload}
        canArchive={canArchive}
      />

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

      <PackageUploadModal
        open={packageFormOpen}
        onClose={() => setPackageFormOpen(false)}
        onSaved={() => void refresh()}
        onOpenRelease={(releaseId) => setDetailId(releaseId)}
      />

      <ReleasePublishModal
        releaseId={publishId}
        mode="publish"
        onClose={() => setPublishId(null)}
        onChanged={() => void refresh()}
      />

      <ReleaseDetailDrawer
        releaseId={detailId}
        canPublish={canPublish}
        canArchive={canArchive}
        canDelete={canDelete}
        canDownload={canDownload}
        canUpload={canUpload}
        onClose={() => setDetailId(null)}
        onChanged={() => void refresh()}
      />
    </Space>
  );
}
