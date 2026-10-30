import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Space, Table, Tag, Typography } from 'antd';
import { versionsApi } from '../../shared/api/versions';
import type { ReleaseArtifact, ReleaseFile, ReleaseStatus } from '../../shared/api/types';
import { formatBytes } from '../../shared/format';

interface ReleaseDetailDrawerProps {
  releaseId: string | null;
  canPublish: boolean;
  canArchive: boolean;
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
  onClose,
  onChanged,
}: ReleaseDetailDrawerProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();

  const detail = useQuery({
    queryKey: ['versions', 'release', releaseId],
    queryFn: ({ signal }) => versionsApi.getRelease(releaseId!, signal),
    enabled: releaseId !== null,
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

  const release = detail.data;
  const statusMeta = release ? STATUS_META[release.status] : null;

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
            column={2}
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
