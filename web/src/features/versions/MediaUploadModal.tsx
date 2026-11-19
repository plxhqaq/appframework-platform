import { tr } from '../../i18n/translate';
import { FileOutlined, InboxOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import { Alert, App, Button, Descriptions, Modal, Progress, Space, Tag, Typography, Upload } from 'antd';
import { useEffect, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { DriverMedia } from '../../shared/api/types';
import { hashFile } from '../../shared/files';
import { formatBytes } from '../../shared/format';
import { mediaVersionLabel } from './packageUi';

interface MediaUploadModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
}

type MediaUploadStage = 'hash' | 'upload' | 'register';

interface MediaUploadProgress {
  stage: MediaUploadStage;
  loaded: number;
  total: number;
}

const STAGE_LABEL: Record<MediaUploadStage, string> = {
  get hash() { return tr("计算文件哈希"); },
  get upload() { return tr("上传媒体包"); },
  get register() { return tr("登记媒体包"); },
};

/**
 * 上传驱动媒体包（af-media/1 zip）：管理台不手填元数据，
 * 媒体键/版本/名称/安装顺序全部取包内 af-media.json；文件名与媒体键不一致时服务端拒绝。
 */
export function MediaUploadModal({ open, onClose, onSaved }: MediaUploadModalProps) {
  const { message } = App.useApp();
  const [file, setFile] = useState<File | null>(null);
  const [progress, setProgress] = useState<MediaUploadProgress | null>(null);
  const [result, setResult] = useState<DriverMedia | null>(null);

  useEffect(() => {
    if (open) {
      setFile(null);
      setProgress(null);
      setResult(null);
    }
  }, [open]);

  const applyProgress = (stage: MediaUploadStage, loaded: number, total: number) =>
    setProgress({ stage, loaded, total });

  const activate = useMutation({
    mutationFn: () => versionsApi.activateMedia(result!.id),
    onSuccess: async (activated) => {
      setResult(activated);
      message.success(
        tr("已启用 {{arg0}}/{{arg1}}", { arg0: activated.mediaKey, arg1: mediaVersionLabel(activated.version) }),
      );
      onSaved();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const mutation = useMutation({
    mutationFn: async () => {
      if (!file) {
        throw new Error(tr("请先选择媒体包文件"));
      }

      applyProgress('hash', 0, file.size);
      const sha256 = await hashFile(file, (loaded, total) => applyProgress('hash', loaded, total));

      applyProgress('upload', 0, file.size);
      const ticket = await versionsApi.createUploadTicket({ sha256, sizeBytes: file.size });
      await versionsApi.uploadWithProgress(ticket, file, (loaded, total) =>
        applyProgress('upload', loaded, total),
      );

      applyProgress('register', 1, 1);
      return versionsApi.registerMedia({ sha256, sizeBytes: file.size, fileName: file.name });
    },
    onSuccess: (registered) => {
      setResult(registered);
      onSaved();
      message.success(
        registered.reused
          ? tr("已复用既有内容并登记 {{arg0}}/{{arg1}}", {
          arg0: registered.mediaKey,
          arg1: mediaVersionLabel(registered.version),
        })
          : tr("已登记媒体包 {{arg0}}/{{arg1}}", {
          arg0: registered.mediaKey,
          arg1: mediaVersionLabel(registered.version),
        }),
      );
    },
    onError: (error: Error) => message.error(error.message),
    onSettled: () => setProgress(null),
  });

  const percent =
    progress && progress.total > 0
      ? Math.min(100, Math.round((progress.loaded / progress.total) * 100))
      : 0;

  return (
    <Modal
      open={open}
      width={560}
      title={tr("上传驱动媒体包")}
      okText={result ? tr("完成") : tr("上传并登记")}
      cancelText={tr("关闭")}
      confirmLoading={mutation.isPending}
      maskClosable={!mutation.isPending}
      closable={!mutation.isPending}
      onCancel={mutation.isPending ? undefined : onClose}
      onOk={() => {
        if (result) {
          onClose();
          return;
        }
        if (!file) {
          message.error(tr("请先选择媒体包文件"));
          return;
        }
        void mutation.mutateAsync().catch(() => {
          // 错误已提示；保留已选文件便于重试
        });
      }}
      afterClose={() => {
        mutation.reset();
        setFile(null);
        setProgress(null);
        setResult(null);
      }}
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 12 }}
        message={tr("媒体信息取自包内 af-media.json")}
        description={tr("媒体键、版本、名称与安装顺序由打包脚本（pack-media.ps1）写入包内清单；同媒体键可登记多个版本，启用版本唯一。")}
      />

      <Upload.Dragger
        accept=".zip"
        maxCount={1}
        showUploadList={false}
        disabled={mutation.isPending || result !== null}
        beforeUpload={(selected) => {
          setFile(selected);
          setResult(null);
          return false;
        }}
      >
        {file ? (
          <Space direction="vertical" size={2}>
            <Typography.Text strong>
              <FileOutlined /> {file.name}
            </Typography.Text>
            <Typography.Text type="secondary">{formatBytes(file.size)}</Typography.Text>
            <Typography.Text type="secondary">{tr("点击或拖拽可重新选择")}</Typography.Text>
          </Space>
        ) : (
          <>
            <p className="ant-upload-drag-icon">
              <InboxOutlined />
            </p>
            <p className="ant-upload-text">{tr("点击或拖拽媒体包到此处")}</p>
            <p className="ant-upload-hint">{tr("仅支持 af-media/1 协议的 zip（media-<组件>.zip）")}</p>
          </>
        )}
      </Upload.Dragger>

      {progress ? (
        <div style={{ marginTop: 12 }}>
          <Typography.Text type="secondary">
            {STAGE_LABEL[progress.stage]}
            {progress.stage === 'upload' || progress.stage === 'hash'
              ? ` · ${formatBytes(progress.loaded)} / ${formatBytes(progress.total)}`
              : ''}
          </Typography.Text>
          <Progress
            percent={progress.stage === 'register' ? 100 : percent}
            status="active"
            showInfo={false}
          />
        </div>
      ) : null}

      {result ? (
        <Descriptions
          size="small"
          column={1}
          bordered
          style={{ marginTop: 12 }}
          title={tr("登记结果")}
          items={[
            {
              key: 'mediaKey',
              label: tr("媒体键"),
              children: (
                <Space size={4}>
                  <Typography.Text code>{result.mediaKey}</Typography.Text>
                  {result.isActive ? <Tag color="green">{tr("已启用")}</Tag> : null}
                  {result.reused ? <Tag>{tr("复用内容")}</Tag> : null}
                </Space>
              ),
            },
            { key: 'version', label: tr("版本"), children: mediaVersionLabel(result.version) },
            { key: 'name', label: tr("名称"), children: result.name },
            { key: 'file', label: tr("文件"), children: result.fileName },
            { key: 'size', label: tr("大小"), children: formatBytes(result.sizeBytes) },
            { key: 'files', label: tr("文件数"), children: result.fileCount },
            {
              key: 'order',
              label: tr("安装顺序"),
              children: (
                <Space size={[0, 4]} wrap>
                  {result.installOrder.map((componentId) => (
                    <Tag key={componentId}>{componentId}</Tag>
                  ))}
                </Space>
              ),
            },
          ]}
        />
      ) : null}

      {result && !result.isActive ? (
        <Button
          type="link"
          style={{ marginTop: 8, padding: 0 }}
          loading={activate.isPending}
          onClick={() => void activate.mutateAsync()}
        >
          {tr("设为启用版本")}</Button>
      ) : null}
    </Modal>
  );
}
