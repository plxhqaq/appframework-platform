import { tr } from '../../i18n/translate';
import { DownloadOutlined, EditOutlined, InboxOutlined, SendOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { startDownload } from '../../shared/download';
import { versionsApi } from '../../shared/api/versions';
import type {
  ReleaseArtifact,
  ReleaseFile,
  ReleasePackage,
  ReleaseStatus,
} from '../../shared/api/types';
import { useState } from 'react';
import { formatBytes, formatDateTime } from '../../shared/format';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { manifestSourceLabel, packageKindMeta, packageStatusMeta } from './packageUi';
import { ReleasePublishModal } from './ReleasePublishModal';

interface ReleaseDetailDrawerProps {
  releaseId: string | null;
  canPublish: boolean;
  canArchive: boolean;
  canDownload: boolean;
  canUpload: boolean;
  onClose: () => void;
  onChanged: () => void;
}

const STATUS_META: Record<ReleaseStatus, { label: string; color?: string }> = {
  draft: { get label() { return tr("草稿"); } },
  published: { get label() { return tr("已发布"); }, color: 'green' },
  archived: { get label() { return tr("已归档"); }, color: 'orange' },
};

/** 版本详情：产物与文件清单是客户端做文件级差异的唯一依据，原样展示哈希便于核对。 */
export function ReleaseDetailDrawer({
  releaseId,
  canPublish,
  canArchive,
  canDownload,
  canUpload,
  onClose,
  onChanged,
}: ReleaseDetailDrawerProps) {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const [downloadingId, setDownloadingId] = useState<string | null>(null);
  const [reviewMode, setReviewMode] = useState<'publish' | 'edit' | null>(null);

  const detail = useQuery({
    queryKey: ['versions', 'release', releaseId],
    queryFn: ({ signal }) => versionsApi.getRelease(releaseId!, signal),
    enabled: releaseId !== null,
    // 增量包摄取是异步的；有未完成包时持续刷新到 ready/failed。
    refetchInterval: (query) => {
      const packages = query.state.data?.packages ?? [];
      return packages.some((pkg) => pkg.status === 'pending' || pkg.status === 'processing')
        ? 60_000
        : false;
    },
  });

  const archive = useMutation({
    mutationFn: () => versionsApi.archiveRelease(releaseId!),
    onSuccess: async (release) => {
      message.success(tr("版本 {{arg0}} 已归档", { arg0: release.version }));
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const removePackage = useMutation({
    mutationFn: (id: string) => versionsApi.deletePackage(id),
    onSuccess: async (deleted) => {
      message.success(tr("已删除发布包 {{arg0}}", { arg0: deleted.fileName }));
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const activatePackage = useMutation({
    mutationFn: (id: string) => versionsApi.activatePackage(id),
    onSuccess: async (activated) => {
      message.success(tr("已启用发布包 {{arg0}}", { arg0: activated.fileName }));
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const retryPackage = useMutation({
    mutationFn: (id: string) => versionsApi.retryPackage(id),
    onSuccess: async (queued) => {
      message.success(tr("已重新排队摄取 {{arg0}}", { arg0: queued.fileName }));
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const downloadPackage = async (pkg: ReleasePackage) => {
    setDownloadingId(pkg.id);
    try {
      // 票据签发时服务端会把非 zip 包打成 zip；下载交给浏览器，支持大文件流式与断点续传。
      const ticket = await versionsApi.createPackageDownloadTicket(pkg.id);
      startDownload(ticket.url);
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("下载失败"));
    } finally {
      setDownloadingId(null);
    }
  };

  const confirmArchive = () => {
    modal.confirm({
      title: tr("归档版本 {{arg0}}？", { arg0: release?.version ?? '' }),
      content: tr("归档后客户端 check 会回退到上一个已发布版本。如果只是误操作，重新上传同版本发布包即可恢复为草稿。"),
      okText: tr("归档"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => archive.mutateAsync(),
    });
  };

  const confirmDelete = (pkg: ReleasePackage) => {
    modal.confirm({
      title: tr("删除发布包 {{arg0}}？", { arg0: pkg.fileName }),
      content: tr("登记记录会立即移除；已落盘内容由孤儿清理任务回收。已发布版本下的包不可删除。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => removePackage.mutateAsync(pkg.id),
    });
  };

  const release = detail.data;
  const statusMeta = release ? STATUS_META[release.status] : null;
  const packages = release?.packages ?? [];

  return (
    <Drawer
      open={releaseId !== null}
      width={responsiveWidth(isMobile, 760)}
      title={release ? tr("版本 {{arg0}}", { arg0: release.version }) : tr("版本详情")}
      onClose={() => {
        setReviewMode(null);
        onClose();
      }}
      styles={{ body: { padding: isMobile ? 12 : 24 } }}
      extra={
        release ? (
          <Space>
            {canPublish && release.status === 'draft' ? (
              <>
                <Button icon={<EditOutlined />} onClick={() => setReviewMode('edit')}>
                  {tr("编辑说明")}</Button>
                <Button
                  type="primary"
                  icon={<SendOutlined />}
                  onClick={() => setReviewMode('publish')}
                >
                  {tr("发布")}</Button>
              </>
            ) : null}
            {canArchive && release.status !== 'archived' ? (
              <Button
                danger
                icon={<InboxOutlined />}
                loading={archive.isPending}
                onClick={confirmArchive}
              >
                {tr("归档")}</Button>
            ) : null}
          </Space>
        ) : null
      }
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

      {release ? (
        <>
          <Descriptions
            size="small"
            column={{ xs: 1, md: 2 }}
            bordered
            items={[
              { key: 'channel', label: tr("渠道"), children: release.channelKey },
              {
                key: 'status',
                label: tr("状态"),
                children: <Tag color={statusMeta?.color}>{statusMeta?.label}</Tag>,
              },
              { key: 'artifacts', label: tr("产物"), children: release.artifactCount },
              { key: 'files', label: tr("文件数"), children: release.fileCount },
              { key: 'size', label: tr("总大小"), children: formatBytes(release.totalSizeBytes) },
              {
                key: 'packages',
                label: tr("发布包"),
                children: tr('{{count}} 个 · {{size}}', {
                  count: release.packageCount ?? packages.length,
                  size: formatBytes(release.packageSizeBytes ?? 0),
                }),
              },
              {
                key: 'published',
                label: tr("发布时间"),
                children: release.publishedAt ? formatDateTime(release.publishedAt) : '-',
              },
              {
                key: 'archived',
                label: tr("归档时间"),
                children: release.archivedAt ? formatDateTime(release.archivedAt) : '-',
              },
              { key: 'createdBy', label: tr("创建人"), children: release.createdBy ?? '-' },
              { key: 'notes', label: tr("发布说明"), span: 2, children: release.notes ?? '-' },
            ]}
          />

          <Typography.Title level={5} style={{ marginTop: 24 }}>
            {tr("发布包")}</Typography.Title>
          <Table<ReleasePackage>
            scroll={{ x: 'max-content' }}
            rowKey="id"
            size="small"
            dataSource={packages}
            pagination={false}
            locale={{ emptyText: tr("尚未登记安装包；客户端 check 可回退到上面的文件级清单") }}
            columns={[
              {
                title: tr("类型"),
                dataIndex: 'kind',
                key: 'kind',
                width: 130,
                render: (kind: string, row) => (
                  <Space size={4}>
                    <Tag color={packageKindMeta(kind).color}>{packageKindMeta(kind).label}</Tag>
                    {row.isActive ? <Tag color="green">{tr("启用包")}</Tag> : null}
                    {row.reused ? <Tag>{tr("复用")}</Tag> : null}
                  </Space>
                ),
              },
              {
                title: tr("目标 / 格式"),
                key: 'target',
                width: 150,
                render: (_: unknown, row) => (
                  <Space size={4}>
                    <Typography.Text code>{row.target}</Typography.Text>
                    <Tag>{row.format.toUpperCase()}</Tag>
                  </Space>
                ),
              },
              {
                title: tr("文件"),
                dataIndex: 'fileName',
                key: 'fileName',
                ellipsis: true,
                render: (fileName: string) => (
                  <Typography.Text copyable={{ text: fileName }}>{fileName}</Typography.Text>
                ),
              },
              {
                title: tr("大小"),
                dataIndex: 'sizeBytes',
                key: 'sizeBytes',
                width: 100,
                render: (size: number) => formatBytes(size),
              },
              {
                title: tr("清单文件数"),
                dataIndex: 'fileCount',
                key: 'fileCount',
                width: 100,
                responsive: ['md'],
                render: (fileCount: number | null | undefined) =>
                  fileCount != null ? fileCount : <Typography.Text type="secondary">-</Typography.Text>,
              },
              {
                title: tr("状态"),
                dataIndex: 'status',
                key: 'status',
                width: 110,
                render: (status: string, row) => {
                  const meta = packageStatusMeta(status);
                  const tag = <Tag color={meta.color}>{meta.label}</Tag>;
                  return row.errorMessage ? (
                    <Tooltip title={`${row.errorCode ?? ''} ${row.errorMessage}`.trim()}>
                      {tag}
                    </Tooltip>
                  ) : (
                    tag
                  );
                },
              },
              {
                title: tr("清单"),
                key: 'manifest',
                width: 150,
                render: (_: unknown, row) =>
                  row.manifestHash ? (
                    <Space direction="vertical" size={0}>
                      <Typography.Text>{manifestSourceLabel(row.manifestSource)}</Typography.Text>
                      <Typography.Text type="secondary" copyable={{ text: row.manifestHash }}>
                        {row.manifestHash.slice(0, 10)}…
                      </Typography.Text>
                    </Space>
                  ) : (
                    <Typography.Text type="secondary">
                      {manifestSourceLabel(row.manifestSource)}
                    </Typography.Text>
                  ),
              },
              {
                title: 'SHA-256',
                dataIndex: 'sha256',
                key: 'sha256',
                width: 130,
                responsive: ['md'],
                render: (sha: string) => (
                  <Typography.Text type="secondary" copyable={{ text: sha }}>
                    {sha.slice(0, 10)}…
                  </Typography.Text>
                ),
              },
              {
                title: tr("操作"),
                key: 'actions',
                width: 240,
                fixed: 'right',
                render: (_: unknown, row) => (
                  <Space size="small">
                    {canUpload
                    && !row.isActive
                    && row.status === 'ready'
                    && release.status !== 'archived' ? (
                      <Button
                        type="link"
                        size="small"
                        loading={activatePackage.isPending}
                        onClick={() => activatePackage.mutate(row.id)}
                      >
                        {tr("设为启用")}</Button>
                    ) : null}
                    {canDownload && row.status === 'ready' ? (
                      <Button
                        type="link"
                        size="small"
                        icon={<DownloadOutlined />}
                        loading={downloadingId === row.id}
                        onClick={() => void downloadPackage(row)}
                      >
                        {tr("下载")}</Button>
                    ) : null}
                    {canUpload && row.kind === 'incremental' && row.status === 'failed' ? (
                      <Button
                        type="link"
                        size="small"
                        loading={retryPackage.isPending}
                        onClick={() => retryPackage.mutate(row.id)}
                      >
                        {tr("重试")}</Button>
                    ) : null}
                    {canArchive && release.status === 'draft' ? (
                      <Button
                        type="link"
                        size="small"
                        danger
                        loading={removePackage.isPending}
                        onClick={() => confirmDelete(row)}
                      >
                        {tr("删除")}</Button>
                    ) : null}
                  </Space>
                ),
              },
            ]}
          />

          <Typography.Title level={5} style={{ marginTop: 24 }}>
            {tr("运行时产物")}</Typography.Title>
          <Collapse
            defaultActiveKey={release.artifacts.map((artifact) => artifact.runtimeIdentifier)}
            items={release.artifacts.map((artifact: ReleaseArtifact) => ({
              key: artifact.runtimeIdentifier,
              label: (
                <Space wrap>
                  <Typography.Text strong>{artifact.runtimeIdentifier}</Typography.Text>
                  <Tag>{artifact.fileCount} {tr("个文件")}</Tag>
                  <Tag>{formatBytes(artifact.totalSizeBytes)}</Tag>
                  <Typography.Text type="secondary" copyable={{ text: artifact.manifestHash }}>
                    {tr("清单哈希")}{artifact.manifestHash.slice(0, 12)}…
                  </Typography.Text>
                </Space>
              ),
              children: (
                <Table<ReleaseFile>
                  scroll={{ x: 'max-content' }}
                  rowKey="path"
                  size="small"
                  dataSource={artifact.files}
                  pagination={responsivePagination(isMobile, {
                    pageSize: 20,
                    showSizeChanger: false,
                  })}
                  columns={[
                    {
                      title: tr("路径"),
                      dataIndex: 'path',
                      key: 'path',
                      render: (path: string) => (
                        <Typography.Text code style={{ wordBreak: 'break-all' }}>
                          {path}
                        </Typography.Text>
                      ),
                    },
                    {
                      title: tr("大小"),
                      dataIndex: 'sizeBytes',
                      key: 'sizeBytes',
                      width: 100,
                      render: (size: number) => formatBytes(size),
                    },
                    {
                      title: 'SHA-256',
                      dataIndex: 'sha256',
                      key: 'sha256',
                      width: 150,
                      render: (sha: string) => (
                        <Typography.Text type="secondary" copyable={{ text: sha }}>
                          {sha.slice(0, 12)}…
                        </Typography.Text>
                      ),
                    },
                  ]}
                />
              ),
            }))}
          />
        </>
      ) : null}

      {canPublish && release?.status === 'draft' ? (
        <ReleasePublishModal
          releaseId={reviewMode ? releaseId : null}
          mode={reviewMode ?? 'publish'}
          onClose={() => setReviewMode(null)}
          onChanged={onChanged}
        />
      ) : null}
    </Drawer>
  );
}
