import { FileOutlined, InboxOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Form,
  Input,
  Modal,
  Progress,
  Segmented,
  Select,
  Space,
  Switch,
  Tag,
  Typography,
  Upload,
} from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type {
  ReleaseChannel,
  ReleasePackage,
  ReleasePackageKind,
} from '../../shared/api/types';
import { hashFile } from '../../shared/files';
import { formatBytes } from '../../shared/format';
import {
  buildRegisterRequest,
  guessPackageFormat,
  manifestSourceLabel,
  packageStatusMeta,
  packageTargetError,
} from './packageUi';

interface PackageFormValues {
  channelKey?: string;
  kind: ReleasePackageKind;
  target: string;
  version?: string;
  notes?: string;
  publish: boolean;
}

interface PackageUploadModalProps {
  open: boolean;
  channels: ReleaseChannel[];
  canPublish: boolean;
  onClose: () => void;
  onSaved: () => void;
}

const RUNTIMES = ['win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'];

const LARGE_FILE_BYTES = 256 * 1024 * 1024;

type UploadStage = 'hash' | 'upload' | 'manifest' | 'register';

interface UploadProgress {
  stage: UploadStage;
  loaded: number;
  total: number;
}

const STAGE_LABEL: Record<UploadStage, string> = {
  hash: '计算文件哈希',
  upload: '上传安装包',
  manifest: '处理旁挂清单',
  register: '登记发布包',
};

/**
 * 上传安装包：先经上传票据直传字节，再登记为发布包。
 *
 * 全量包（exe/msi/…）登记后立刻可被 check 下发；增量包登记后进入待摄取。
 * 清单优先取旁挂 JSON，否则由服务端从包内探测（PE 资源 / zip 内 af-package.json）。
 */
export function PackageUploadModal({
  open,
  channels,
  canPublish,
  onClose,
  onSaved,
}: PackageUploadModalProps) {
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<PackageFormValues>();
  const [file, setFile] = useState<File | null>(null);
  const [manifestFile, setManifestFile] = useState<File | null>(null);
  const [progress, setProgress] = useState<UploadProgress | null>(null);
  const [result, setResult] = useState<ReleasePackage | null>(null);
  const kind = Form.useWatch('kind', form) ?? 'full';

  const format = useMemo(() => (file ? guessPackageFormat(file.name) : null), [file]);
  const largeFile = file !== null && file.size > LARGE_FILE_BYTES;

  useEffect(() => {
    if (open) {
      form.setFieldsValue({
        channelKey: channels[0]?.key,
        kind: 'full',
        target: 'any',
        version: '',
        notes: '',
        publish: canPublish,
      });
    }
  }, [open, channels, canPublish, form]);

  const applyProgress = (stage: UploadStage, loaded: number, total: number) =>
    setProgress({ stage, loaded, total });

  const mutation = useMutation({
    mutationFn: async (values: PackageFormValues) => {
      if (!file) {
        throw new Error('请先选择安装包文件');
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
      return versionsApi.registerPackage(
        buildRegisterRequest({
          sha256,
          sizeBytes: file.size,
          fileName: file.name,
          kind: values.kind,
          target: values.target,
          channelKey: values.channelKey,
          version: values.version,
          notes: values.notes,
          manifestSha256,
          publish: values.publish,
          canPublish,
        }),
      );
    },
    onSuccess: (registered) => {
      onSaved();

      if (registered.status === 'failed') {
        setResult(registered);
        message.error(registered.errorMessage ?? '发布包登记失败');
        return;
      }

      const status = packageStatusMeta(registered.status).label;
      message.success(
        `${registered.fileName} 已登记（${formatBytes(registered.sizeBytes)}，${status}，` +
          `清单来源 ${manifestSourceLabel(registered.manifestSource)}）` +
          `${registered.reused ? '，复用已有内容' : ''}`,
      );

      if (registered.status === 'pending') {
        setResult(registered);
        return;
      }

      onClose();
    },
    onError: (error: Error) => message.error(error.message),
    onSettled: () => setProgress(null),
  });

  // 增量包登记后由后台 Worker 摄取；轮询到 ready/failed 即停，避免长连接。
  const polling = useQuery({
    queryKey: ['versions', 'package', result?.id],
    queryFn: ({ signal }) => versionsApi.getPackage(result!.id, signal),
    enabled: result !== null && (result.status === 'pending' || result.status === 'processing'),
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === 'pending' || status === 'processing' ? 3000 : false;
    },
  });
  const current = polling.data ?? result;

  const retry = useMutation({
    mutationFn: () => versionsApi.retryPackage(current!.id),
    onSuccess: (queued) => {
      setResult(queued);
      void queryClient.invalidateQueries({ queryKey: ['versions', 'package', queued.id] });
      message.success('已重新排队摄取');
      onSaved();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleOk = async () => {
    if (!file) {
      message.error('请先选择安装包文件');
      return;
    }

    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    const targetError = packageTargetError(values.kind, values.target);
    if (targetError) {
      message.error(targetError);
      return;
    }

    if (values.kind === 'full' && !manifestFile && format !== 'zip' && format !== '7z') {
      const confirmed = await new Promise<boolean>((resolve) => {
        modal.confirm({
          title: '确认按全量安装包登记',
          content:
            '未提供旁挂清单。若安装包内没有内嵌清单，服务端会按人工登记处理' +
            '（清单来源 manual；渠道/版本取自表单，留空则登记失败）。',
          okText: '确认登记',
          cancelText: '返回检查',
          onOk: () => resolve(true),
          onCancel: () => resolve(false),
        });
      });
      if (!confirmed) {
        return;
      }
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
      width={760}
      title="上传安装包"
      okText={current ? '关闭' : '上传并登记'}
      cancelText="取消"
      confirmLoading={mutation.isPending}
      maskClosable={!mutation.isPending}
      closable={!mutation.isPending}
      onCancel={mutation.isPending ? undefined : onClose}
      onOk={() => {
        if (current) {
          onClose();
          return;
        }
        void handleOk();
      }}
      afterClose={() => {
        mutation.reset();
        retry.reset();
        form.resetFields();
        setFile(null);
        setManifestFile(null);
        setProgress(null);
        setResult(null);
      }}
    >
      <Form<PackageFormValues> form={form} layout="vertical" requiredMark={false}>
        <Space size="middle" style={{ display: 'flex', alignItems: 'flex-start', flexWrap: 'wrap' }}>
          <Form.Item name="kind" label="包类型" style={{ width: 260 }}>
            <Segmented
              options={[
                { value: 'full', label: '全量安装包' },
                { value: 'incremental', label: '增量包' },
              ]}
            />
          </Form.Item>

          <Form.Item
            name="target"
            label="目标运行时"
            style={{ width: 200 }}
            rules={[{ required: true, message: '请选择运行时' }]}
            tooltip="全量包可登记为 any（对所有平台可用）；增量包必须对应具体运行时。"
          >
            <Select
              options={[
                { value: 'any', label: 'any（全平台）' },
                ...RUNTIMES.map((value) => ({ value, label: value })),
              ]}
            />
          </Form.Item>

          <Form.Item name="channelKey" label="渠道" style={{ width: 220 }}>
            <Select
              allowClear
              placeholder="留空则取包内清单"
              options={channels.map((channel) => ({
                value: channel.key,
                label: `${channel.name}（${channel.key}）`,
              }))}
            />
          </Form.Item>

          <Form.Item
            name="version"
            label="版本号"
            style={{ width: 200 }}
            tooltip="留空则取包内清单；人工登记的全量包必须填写。"
          >
            <Input placeholder="1.2.3" autoComplete="off" />
          </Form.Item>
        </Space>

        <Form.Item name="notes" label="发布说明" rules={[{ max: 4000 }]}>
          <Input.TextArea rows={2} placeholder="可选；仅在自动创建草稿版本时写入" />
        </Form.Item>

        <Form.Item
          name="publish"
          label="登记后立即发布"
          valuePropName="checked"
          tooltip={
            canPublish
              ? '草稿版本的包对客户端不可见；发布后 check 才会下发。'
              : '当前账号没有版本发布权限，包会保留在草稿版本中。'
          }
        >
          <Switch disabled={!canPublish} />
        </Form.Item>
      </Form>

      <Upload.Dragger
        accept=".exe,.msi,.msp,.zip,.7z,.bin"
        maxCount={1}
        showUploadList={false}
        disabled={mutation.isPending}
        beforeUpload={(selected) => {
          setFile(selected);
          setResult(null);
          return false;
        }}
        style={{ marginBottom: 12 }}
      >
        {file ? (
          <Space direction="vertical" size={2}>
            <Typography.Text strong>
              <FileOutlined /> {file.name}
            </Typography.Text>
            <Typography.Text type="secondary">
              {formatBytes(file.size)}
              {format ? ` · ${format.toUpperCase()}` : ' · 无法从扩展名推断格式'}
            </Typography.Text>
            <Typography.Text type="secondary">点击或拖拽可重新选择</Typography.Text>
          </Space>
        ) : (
          <>
            <p className="ant-upload-drag-icon">
              <InboxOutlined />
            </p>
            <p className="ant-upload-text">点击或拖拽安装包到此处</p>
            <p className="ant-upload-hint">支持 exe / msi / msp / zip / 7z / bin，单文件上传</p>
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
            {manifestFile ? '更换旁挂清单' : '选择旁挂清单（可选）'}
          </Button>
        </Upload>
        {manifestFile ? (
          <Tag closable onClose={() => setManifestFile(null)}>
            {manifestFile.name}
          </Tag>
        ) : null}
        <Typography.Text type="secondary">
          不选则由服务端从包内探测清单；sidecar 适用于 exe 未内嵌清单的场景。
        </Typography.Text>
      </Space>

      {largeFile ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message="安装包超过 256MB"
          description="浏览器端会分块计算 SHA-256 并以流式上传，耗时取决于磁盘与网络；超大产物建议用发行脚本/CI 上传。"
        />
      ) : null}

      {kind === 'incremental' ? (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message="增量包登记后进入待摄取"
          description="服务端会异步解析文件清单；摄取完成前 check 不会下发该增量包。"
        />
      ) : null}

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

      {current?.status === 'failed' ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={`发布包 ${current.fileName} 处理失败：${current.errorCode ?? '未知错误'}`}
          description={current.errorMessage ?? '请检查清单内容与文件名约定后重试。'}
          action={
            <Button
              size="small"
              icon={<ReloadOutlined />}
              loading={retry.isPending}
              onClick={() => void retry.mutateAsync()}
            >
              重试摄取
            </Button>
          }
        />
      ) : null}

      {current && (current.status === 'pending' || current.status === 'processing') ? (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message={`${current.fileName} ${packageStatusMeta(current.status).label}`}
          description="服务端正在异步解析文件清单；完成后这里会显示文件数与失败原因。"
        />
      ) : null}

      {current?.status === 'ready' && current.fileCount != null ? (
        <Alert
          type="success"
          showIcon
          style={{ marginBottom: 12 }}
          message={`${current.fileName} 已可用`}
          description={`清单 ${current.fileCount} 个文件 · ${formatBytes(current.sizeBytes)}，来源 ${manifestSourceLabel(current.manifestSource)}。`}
        />
      ) : null}
    </Modal>
  );
}
