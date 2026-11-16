import { tr } from '../../i18n/translate';
import { FileOutlined, InboxOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Form,
  Input,
  Modal,
  Progress,
  Select,
  Space,
  Tag,
  Typography,
  Upload,
} from 'antd';
import { useEffect, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import { hashFile } from '../../shared/files';
import { formatBytes } from '../../shared/format';

interface UpdaterFormValues {
  target: string;
  version?: string;
  notes?: string;
}

interface UpdaterUploadModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
}

const RUNTIMES = ['win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'];

type UploadStage = 'hash' | 'upload' | 'manifest' | 'register';

interface UploadProgress {
  stage: UploadStage;
  loaded: number;
  total: number;
}

const STAGE_LABEL: Record<UploadStage, string> = {
  get hash() { return tr("计算文件哈希"); },
  get upload() { return tr("上传更新器"); },
  get manifest() { return tr("处理旁挂清单"); },
  get register() { return tr("登记更新器"); },
};

/**
 * 上传更新器分发包。
 *
 * 版本号可留空：服务端按「清单 → 老 updater-info.json → PE FileVersion」顺序推断，
 * 仍取不到时明确要求手填，避免把无名版本写进版本线。
 */
export function UpdaterUploadModal({ open, onClose, onSaved }: UpdaterUploadModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<UpdaterFormValues>();
  const [file, setFile] = useState<File | null>(null);
  const [manifestFile, setManifestFile] = useState<File | null>(null);
  const [progress, setProgress] = useState<UploadProgress | null>(null);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({ target: 'any', version: '', notes: '' });
    }
  }, [open, form]);

  const applyProgress = (stage: UploadStage, loaded: number, total: number) =>
    setProgress({ stage, loaded, total });

  const mutation = useMutation({
    mutationFn: async (values: UpdaterFormValues) => {
      if (!file) {
        throw new Error(tr("请先选择更新器文件"));
      }

      applyProgress('hash', 0, file.size);
      const sha256 = await hashFile(file, (loaded, total) => applyProgress('hash', loaded, total));

      applyProgress('upload', 0, file.size);
      const ticket = await versionsApi.createUploadTicket({ sha256, sizeBytes: file.size });
      await versionsApi.uploadWithProgress(ticket, file, (loaded, total) =>
        applyProgress('upload', loaded, total),
      );

      let manifestSha256: string | null = null;
      if (manifestFile) {
        applyProgress('manifest', 0, manifestFile.size);
        manifestSha256 = await hashFile(manifestFile, (loaded, total) =>
          applyProgress('manifest', loaded, total),
        );
        const manifestTicket = await versionsApi.createUploadTicket({
          sha256: manifestSha256,
          sizeBytes: manifestFile.size,
        });
        await versionsApi.uploadWithProgress(manifestTicket, manifestFile, (loaded, total) =>
          applyProgress('manifest', loaded, total),
        );
      }

      applyProgress('register', 1, 1);
      return versionsApi.registerUpdater({
        target: values.target,
        version: values.version?.trim() || null,
        fileName: file.name,
        sha256,
        sizeBytes: file.size,
        manifestSha256,
        notes: values.notes?.trim() || null,
      });
    },
    onSuccess: (registered) => {
      message.success(tr("更新器 {{arg0}}/{{arg1}} 已登记", { arg0: registered.target, arg1: registered.version }));
      onSaved();
      onClose();
    },
    onError: (error: Error) => message.error(error.message),
    onSettled: () => setProgress(null),
  });

  const handleOk = async () => {
    if (!file) {
      message.error(tr("请先选择更新器文件"));
      return;
    }

    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    try {
      await mutation.mutateAsync(values);
    } catch {
      // 错误已提示；保留已选文件便于重试
    }
  };

  const percent =
    progress && progress.total > 0
      ? Math.min(100, Math.round((progress.loaded / progress.total) * 100))
      : 0;

  return (
    <Modal
      open={open}
      width={720}
      title={tr("上传更新器")}
      okText={tr("上传并登记")}
      cancelText={tr("取消")}
      confirmLoading={mutation.isPending}
      maskClosable={!mutation.isPending}
      closable={!mutation.isPending}
      onCancel={mutation.isPending ? undefined : onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
        setFile(null);
        setManifestFile(null);
        setProgress(null);
      }}
    >
      <Form<UpdaterFormValues> form={form} layout="vertical" requiredMark={false}>
        <Space size="middle" style={{ display: 'flex', alignItems: 'flex-start', flexWrap: 'wrap' }}>
          <Form.Item
            name="target"
            label={tr("目标运行时")}
            style={{ width: 220 }}
            rules={[{ required: true, message: tr("请选择运行时") }]}
            tooltip={tr("any 作为兜底更新器；具体 RID 优先于 any。")}
          >
            <Select
              options={[
                { value: 'any', label: tr("any（全平台兜底）") },
                ...RUNTIMES.map((value) => ({ value, label: value })),
              ]}
            />
          </Form.Item>

          <Form.Item
            name="version"
            label={tr("版本号")}
            style={{ width: 220 }}
            tooltip={tr("留空则依次取包内清单与 PE FileVersion；都读不到时服务端会要求手填。")}
          >
            <Input placeholder="1.0.0" autoComplete="off" />
          </Form.Item>
        </Space>

        <Form.Item name="notes" label={tr("发布说明")} rules={[{ max: 4000 }]}>
          <Input.TextArea rows={2} placeholder={tr("可选")} />
        </Form.Item>
      </Form>

      <Upload.Dragger
        accept=".exe,.msi,.msp,.zip,.7z,.bin"
        maxCount={1}
        showUploadList={false}
        disabled={mutation.isPending}
        beforeUpload={(selected) => {
          setFile(selected);
          return false;
        }}
        style={{ marginBottom: 12 }}
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
            <p className="ant-upload-text">{tr("点击或拖拽更新器到此处")}</p>
            <p className="ant-upload-hint">{tr("支持 exe / msi / msp / zip / 7z / bin，单文件上传")}</p>
          </>
        )}
      </Upload.Dragger>

      <Space wrap style={{ marginBottom: 12 }}>
        <Upload
          accept=".json,application/json"
          maxCount={1}
          showUploadList={false}
          disabled={mutation.isPending}
          beforeUpload={(selected) => {
            setManifestFile(selected);
            return false;
          }}
        >
          <Button icon={<PlusOutlined />}>
            {manifestFile ? tr("更换旁挂清单") : tr("选择旁挂清单（可选）")}
          </Button>
        </Upload>
        {manifestFile ? (
          <Tag closable onClose={() => setManifestFile(null)}>
            {manifestFile.name}
          </Tag>
        ) : null}
        <Typography.Text type="secondary">
          {tr("清单 kind 必须是 updater；不选则由服务端从包内探测。")}</Typography.Text>
      </Space>

      {progress ? (
        <div style={{ marginBottom: 12 }}>
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

      <Alert
        type="info"
        showIcon
        message={tr("更新器独立于应用版本")}
        description={tr("客户端先更新更新器再应用增量；同 target + version 只允许一份内容，重复登记相同内容会复用。")}
      />
    </Modal>
  );
}
