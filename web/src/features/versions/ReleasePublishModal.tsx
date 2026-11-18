import { tr } from '../../i18n/translate';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Descriptions, Input, Modal, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import { formatBytes } from '../../shared/format';
import { packageKindMeta } from './packageUi';

interface ReleasePublishModalProps {
  releaseId: string | null;
  /** publish：核对信息、编辑发布说明并发布；edit：仅保存草稿的发布说明。 */
  mode: 'publish' | 'edit';
  onClose: () => void;
  onChanged: () => void;
}

/**
 * 发布前确认：展示从安装包清单登记下来的信息，发布说明在这里最后修改一次，
 * 确认后才调用发布接口；草稿阶段也可以只进来编辑说明。
 */
export function ReleasePublishModal({
  releaseId,
  mode,
  onClose,
  onChanged,
}: ReleasePublishModalProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [notes, setNotes] = useState('');
  const [notesLoaded, setNotesLoaded] = useState(false);

  const detail = useQuery({
    queryKey: ['versions', 'release', releaseId],
    queryFn: ({ signal }) => versionsApi.getRelease(releaseId!, signal),
    enabled: releaseId !== null,
  });

  useEffect(() => {
    setNotes('');
    setNotesLoaded(false);
  }, [releaseId]);

  useEffect(() => {
    if (!notesLoaded && detail.data) {
      setNotes(detail.data.notes ?? '');
      setNotesLoaded(true);
    }
  }, [detail.data, notesLoaded]);

  const submit = useMutation({
    mutationFn: async () => {
      if (!releaseId) {
        throw new Error(tr("缺少版本信息"));
      }

      const trimmed = notes.trim();
      await versionsApi.updateRelease(releaseId, { notes: trimmed.length > 0 ? trimmed : null });
      return mode === 'publish' ? versionsApi.publishRelease(releaseId) : null;
    },
    onSuccess: async (release) => {
      message.success(
        release
          ? tr("版本 {{arg0}} 已发布", { arg0: release.version })
          : tr("发布说明已保存"),
      );
      await queryClient.invalidateQueries({ queryKey: ['versions'] });
      onChanged();
      onClose();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const release = detail.data;

  return (
    <Modal
      open={releaseId !== null}
      width={640}
      title={
        mode === 'publish'
          ? tr("发布版本 {{arg0}}", { arg0: release?.version ?? '' })
          : tr("编辑发布说明")
      }
      okText={mode === 'publish' ? tr("确认发布") : tr("保存")}
      cancelText={tr("取消")}
      confirmLoading={submit.isPending}
      maskClosable={!submit.isPending}
      closable={!submit.isPending}
      onCancel={submit.isPending ? undefined : onClose}
      onOk={() => void submit.mutateAsync()}
      destroyOnHidden
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
              { key: 'channel', label: tr("渠道"), children: release.channelKey },
              {
                key: 'version',
                label: tr("版本"),
                children: <Typography.Text strong>{release.version}</Typography.Text>,
              },
              {
                key: 'status',
                label: tr("状态"),
                children: (
                  <Tag color={release.status === 'draft' ? undefined : 'green'}>
                    {release.status === 'draft' ? tr("草稿") : tr("已发布")}
                  </Tag>
                ),
              },
              {
                key: 'packages',
                label: tr("发布包"),
                children: tr('{{count}} 个', { count: release.packageCount }),
              },
              { key: 'artifacts', label: tr("产物"), children: release.artifactCount },
              { key: 'files', label: tr("文件数"), children: release.fileCount },
              {
                key: 'size',
                label: tr("总大小"),
                children: formatBytes(release.totalSizeBytes),
              },
            ]}
          />

          {release.packages && release.packages.length > 0 ? (
            <Descriptions
              size="small"
              column={1}
              style={{ marginTop: 12 }}
              items={release.packages.map((pkg) => ({
                key: pkg.id,
                label: packageKindMeta(pkg.kind).label,
                children: `${pkg.fileName} · ${pkg.target} · ${formatBytes(pkg.sizeBytes)}`,
              }))}
            />
          ) : null}

          <Typography.Title level={5} style={{ marginTop: 16 }}>
            {tr("发布说明")}
          </Typography.Title>
          <Input.TextArea
            rows={4}
            maxLength={4000}
            showCount
            value={notes}
            disabled={!notesLoaded}
            onChange={(event) => setNotes(event.target.value)}
            placeholder={tr("取自安装包内清单，可在此补充或修改")}
          />

          {mode === 'publish' ? (
            <Alert
              type="info"
              showIcon
              style={{ marginTop: 40 }}
              message={tr("发布后客户端 check 才会下发该版本")}
              description={tr("确认渠道、版本与发布包信息无误后再发布；已发布版本不能再改说明。")}
            />
          ) : null}
        </>
      ) : null}
    </Modal>
  );
}