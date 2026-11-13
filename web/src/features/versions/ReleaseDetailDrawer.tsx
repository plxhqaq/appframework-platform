import { DownloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { requestBlob } from '../../shared/api/client';
import { versionsApi } from '../../shared/api/versions';
import type {
  ReleaseArtifact,
  ReleaseFile,
  ReleasePackage,
  ReleaseStatus,
} from '../../shared/api/types';
import { useState } from 'react';
import { formatBytes } from '../../shared/format';
import { manifestSourceLabel, packageKindMeta, packageStatusMeta, safeDownloadName } from './packageUi';

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
  draft: { label: '草稿' },
  published: { label: '已发布', color: 'green' },
  archived: { label: '已归档', color: 'orange' },
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
  const queryClient = useQueryClient();
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const detail = useQuery({
    queryKey: ['versions', 'release', releaseId],
    queryFn: ({ signal }) => versionsApi.getRelease(releaseId!, signal),
    enabled: releaseId !== null,
    // 增量包摄取是异步的；有未完成包时持续刷新到 ready/failed。
    refetchInterval: (query) => {
      const packages = query.state.data?.packages ?? [];
      return packages.some((pkg) => pkg.status === 'pending' || pkg.status === 'processing')
        ? 3000
        : false;
    },
  });

  const action = useMutation({
    mutationFn: async (kind: 'publish' | 'archive') =>
      kind === 'publish'
        ? versionsApi.publishRelease(releaseId!)
        : versionsApi.archiveRelease(releaseId!),
    onSuccess: async (release) => {
      message.success(`版本 ${release.version} 已${release.status === 'archived' ? '归档' : '发布'}`);
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const removePackage = useMutation({
    mutationFn: (id: string) => versionsApi.deletePackage(id),
    onSuccess: async (deleted) => {
      message.success(`已删除发布包 ${deleted.fileName}`);
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const retryPackage = useMutation({
    mutationFn: (id: string) => versionsApi.retryPackage(id),
    onSuccess: async (queued) => {
      message.success(`已重新排队摄取 ${queued.fileName}`);
      await queryClient.invalidateQueries({ queryKey: ['versions', 'release', releaseId] });
      onChanged();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const downloadPackage = async (pkg: ReleasePackage) => {
    setDownloadingId(pkg.id);
    try {
      const blob = await requestBlob(`/api/v1/client/versions/packages/${pkg.id}/download`);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = safeDownloadName(pkg.fileName);
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '下载失败');
    } finally {
      setDownloadingId(null);
    }
  };

  const confirmDelete = (pkg: ReleasePackage) => {
    modal.confirm({
      title: `删除发布包 ${pkg.fileName}？`,
      content: '登记记录会立即移除；已落盘内容由孤儿清理任务回收。已发布版本下的包不可删除。',
      okText: '删除',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => removePackage.mutateAsync(pkg.id),
    });
  };

  const release = detail.data;
  const statusMeta = release ? STATUS_META[release.status] : null;
  const packages = release?.packages ?? [];

  return (
    <Drawer
      open={releaseId !== null}
      width={760}
      title={release ? `版本 ${release.version}` : '版本详情'}
      onClose={onClose}
      extra={
        release ? (
          <Space>
            {canPublish && release.status === 'draft' ? (
              <Button type="primary" loading={action.isPending} onClick={() => action.mutate('publish')}>
                发布
              </Button>
            ) : null}
            {canArchive && release.status !== 'archived' ? (
              <Button danger loading={action.isPending} onClick={() => action.mutate('archive')}>
                归档
              </Button>
            ) : null}
          </Space>
        ) : null
      }
    >
      {detail.isError ? (
        <Alert type="error" showIcon message={(detail.error as Error).message} />
      ) : null}

      {release ? (
        <>
          <Descriptions
            size="small"
            column={{ xs: 1, md: 2 }}
            bordered
            items={[
              { key: 'channel', label: '渠道', children: release.channelKey },
              {
                key: 'status',
                label: '状态',
                children: <Tag color={statusMeta?.color}>{statusMeta?.label}</Tag>,
              },
              { key: 'artifacts', label: '产物', children: release.artifactCount },
              { key: 'files', label: '文件数', children: release.fileCount },
              { key: 'size', label: '总大小', children: formatBytes(release.totalSizeBytes) },
              {
                key: 'packages',
                label: '发布包',
                children: `${release.packageCount ?? packages.length} 个 · ${formatBytes(
                  release.packageSizeBytes ?? 0,
                )}`,
              },
              {
                key: 'published',
                label: '发布时间',
                children: release.publishedAt ? new Date(release.publishedAt).toLocaleString() : '-',
              },
              {
                key: 'archived',
                label: '归档时间',
                children: release.archivedAt ? new Date(release.archivedAt).toLocaleString() : '-',
              },
              { key: 'createdBy', label: '创建人', children: release.createdBy ?? '-' },
              { key: 'notes', label: '发布说明', span: 2, children: release.notes ?? '-' },
            ]}
          />

          <Typography.Title level={5} style={{ marginTop: 24 }}>
            发布包
          </Typography.Title>
          <Table<ReleasePackage>
            scroll={{ x: 'max-content' }}
            rowKey="id"
            size="small"
            dataSource={packages}
            pagination={false}
            locale={{ emptyText: '尚未登记安装包；客户端 check 可回退到上面的文件级清单' }}
            columns={[
              {
                title: '类型',
                dataIndex: 'kind',
                key: 'kind',
                width: 130,
                render: (kind: string, row) => (
                  <Space size={4}>
                    <Tag color={packageKindMeta(kind).color}>{packageKindMeta(kind).label}</Tag>
                    {row.reused ? <Tag>复用</Tag> : null}
                  </Space>
                ),
              },
              {
                title: '目标 / 格式',
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
                title: '文件',
                dataIndex: 'fileName',
                key: 'fileName',
                ellipsis: true,
                render: (fileName: string) => (
                  <Typography.Text copyable={{ text: fileName }}>{fileName}</Typography.Text>
                ),
              },
              {
                title: '大小',
                dataIndex: 'sizeBytes',
                key: 'sizeBytes',
                width: 100,
                render: (size: number) => formatBytes(size),
              },
              {
                title: '清单文件数',
                dataIndex: 'fileCount',
                key: 'fileCount',
                width: 100,
                render: (fileCount: number | null | undefined) =>
                  fileCount != null ? fileCount : <Typography.Text type="secondary">-</Typography.Text>,
              },
              {
                title: '状态',
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
                title: '清单',
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
                render: (sha: string) => (
                  <Typography.Text type="secondary" copyable={{ text: sha }}>
                    {sha.slice(0, 10)}…
                  </Typography.Text>
                ),
              },
              {
                title: '操作',
                key: 'actions',
                width: 190,
                render: (_: unknown, row) => (
                  <Space size="small">
                    {canDownload && row.status === 'ready' ? (
                      <Button
                        type="link"
                        size="small"
                        icon={<DownloadOutlined />}
                        loading={downloadingId === row.id}
                        onClick={() => void downloadPackage(row)}
                      >
                        下载
                      </Button>
                    ) : null}
                    {canUpload && row.kind === 'incremental' && row.status === 'failed' ? (
                      <Button
                        type="link"
                        size="small"
                        loading={retryPackage.isPending}
                        onClick={() => retryPackage.mutate(row.id)}
                      >
                        重试
                      </Button>
                    ) : null}
                    {canArchive && release.status === 'draft' ? (
                      <Button
                        type="link"
                        size="small"
                        danger
                        loading={removePackage.isPending}
                        onClick={() => confirmDelete(row)}
                      >
                        删除
                      </Button>
                    ) : null}
                  </Space>
                ),
              },
            ]}
          />

          <Typography.Title level={5} style={{ marginTop: 24 }}>
            运行时产物
          </Typography.Title>
          <Collapse
            defaultActiveKey={release.artifacts.map((artifact) => artifact.runtimeIdentifier)}
            items={release.artifacts.map((artifact: ReleaseArtifact) => ({
              key: artifact.runtimeIdentifier,
              label: (
                <Space wrap>
                  <Typography.Text strong>{artifact.runtimeIdentifier}</Typography.Text>
                  <Tag>{artifact.fileCount} 个文件</Tag>
                  <Tag>{formatBytes(artifact.totalSizeBytes)}</Tag>
                  <Typography.Text type="secondary" copyable={{ text: artifact.manifestHash }}>
                    清单哈希 {artifact.manifestHash.slice(0, 12)}…
                  </Typography.Text>
                </Space>
              ),
              children: (
                <Table<ReleaseFile>
                  scroll={{ x: 'max-content' }}
                  rowKey="path"
                  size="small"
                  dataSource={artifact.files}
                  pagination={{ pageSize: 20, showSizeChanger: false }}
                  columns={[
                    {
                      title: '路径',
                      dataIndex: 'path',
                      key: 'path',
                      render: (path: string) => <Typography.Text code>{path}</Typography.Text>,
                    },
                    {
                      title: '大小',
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
    </Drawer>
  );
}
