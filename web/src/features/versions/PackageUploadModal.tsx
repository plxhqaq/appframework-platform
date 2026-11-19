import { tr } from '../../i18n/translate';
import { FileOutlined, InboxOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Descriptions,
  Modal,
  Progress,
  Space,
  Tag,
  Typography,
  Upload,
} from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { ReleasePackage, ReleasePackageStatus } from '../../shared/api/types';
import { hashFile } from '../../shared/files';
import { formatBytes } from '../../shared/format';
import {
  buildRegisterRequest,
  guessPackageFormat,
  manifestSourceLabel,
  packageKindMeta,
  packageStatusMeta,
} from './packageUi';

interface PackageUploadModalProps {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
  /** 登记成功后打开草稿版本详情，供核对信息并发布。 */
  onOpenRelease: (releaseId: string) => void;
}

const LARGE_FILE_BYTES = 256 * 1024 * 1024;

type UploadStage = 'hash' | 'upload' | 'manifest' | 'register';

interface UploadProgress {
  stage: UploadStage;
  loaded: number;
  total: number;
}

const STAGE_LABEL: Record<UploadStage, string> = {
  get hash() { return tr("计算文件哈希"); },
  get upload() { return tr("上传安装包"); },
  get manifest() { return tr("处理旁挂清单"); },
  get register() { return tr("登记发布包"); },
};

const DONE_STATUSES: ReleasePackageStatus[] = ['ready'];

/**
 * 上传安装包：管理台不提供任何手填元数据，渠道/版本/类型/运行时/说明全部取自包内清单
 * （PE 内嵌清单、zip 内 af-package.json 或旁挂 JSON）。登记结果一律为草稿版本，
 * 由上传者在草稿阶段核对并编辑发布说明，再走发布确认。
 */
export function PackageUploadModal({
  open,
  onClose,
  onSaved,
  onOpenRelease,
}: PackageUploadModalProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [manifestFile, setManifestFile] = useState<File | null>(null);
  const [progress, setProgress] = useState<UploadProgress | null>(null);
  const [result, setResult] = useState<ReleasePackage | null>(null);

  const format = useMemo(() => (file ? guessPackageFormat(file.name) : null), [file]);
  const largeFile = file !== null && file.size > LARGE_FILE_BYTES;

  useEffect(() => {
    if (open) {
      setFile(null);
      setManifestFile(null);
      setProgress(null);
      setResult(null);
    }
  }, [open]);

  const applyProgress = (stage: UploadStage, loaded: number, total: number) =>
    setProgress({ stage, loaded, total });

  const mutation = useMutation({
    mutationFn: async () => {
      if (!file) {
        throw new Error(tr("请先选择安装包文件"));
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
        buildRegisterRequest({ sha256, sizeBytes: file.size, fileName: file.name, manifestSha256 }),
      );
    },
    onSuccess: (registered) => {
      onSaved();

      if (registered.status === 'failed') {
        setResult(registered);
        message.error(registered.errorMessage ?? tr("发布包登记失败"));
        return;
      }

      setResult(registered);
      message.success(
        tr('{{file}} 已登记为草稿（{{version}}，{{kind}}，{{target}}）', {
          file: registered.fileName,
          version: registered.version,
          kind: packageKindMeta(registered.kind).label,
          target: registered.target,
        }),
      );
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
      // 摄取进度事件会推；这里只留 60s 兜底。
      return status === 'pending' || status === 'processing' ? 60_000 : false;
    },
  });
  const current = polling.data ?? result;

  const retry = useMutation({
    mutationFn: () => versionsApi.retryPackage(current!.id),
    onSuccess: (queued) => {
      setResult(queued);
      void queryClient.invalidateQueries({ queryKey: ['versions', 'package', queued.id] });
      message.success(tr("已重新排队摄取"));
      onSaved();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleOk = async () => {
    if (!file) {
      message.error(tr("请先选择安装包文件"));
      return;
    }

    try {
      await mutation.mutateAsync();
    } catch {
      // 错误已提示；保留已选文件便于重试
    }
  };

  const percent =
    progress && progress.total > 0
      ? Math.min(100, Math.round((progress.loaded / progress.total) * 100))
      : 0;

  const settled = current !== null;
  const succeeded = current?.status === 'ready';

  return (
    <Modal
      open={open}
      width={640}
      title={tr("上传安装包")}
      okText={settled ? (succeeded ? tr("查看草稿版本") : tr("关闭")) : tr("上传并登记")}
      cancelText={tr("关闭")}
      confirmLoading={mutation.isPending}
      maskClosable={!mutation.isPending}
      closable={!mutation.isPending}
      onCancel={mutation.isPending ? undefined : onClose}
      onOk={() => {
        if (settled) {
          if (succeeded) {
            onOpenRelease(current.releaseId);
          }
          onClose();
          return;
        }
        void handleOk();
      }}
      afterClose={() => {
        mutation.reset();
        retry.reset();
        setFile(null);
        setManifestFile(null);
        setProgress(null);
        setResult(null);
      }}
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 12 }}
        message={tr("信息全部取自安装包内清单")}
        description={tr("渠道、版本、包类型、运行时与说明由构建脚本写入包内清单；登记后进入草稿，核对信息并补充发布说明后再发布。")}
      />

      <Upload.Dragger
        accept=".exe,.msi,.msp,.zip,.7z,.bin"
        maxCount={1}
        showUploadList={false}
        disabled={mutation.isPending || succeeded}
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
              {format ? ` · ${format.toUpperCase()}` : tr(" · 无法从扩展名推断格式")}
            </Typography.Text>
            <Typography.Text type="secondary">{tr("点击或拖拽可重新选择")}</Typography.Text>
          </Space>
        ) : (
          <>
            <p className="ant-upload-drag-icon">
              <InboxOutlined />
            </p>
            <p className="ant-upload-text">{tr("点击或拖拽安装包到此处")}</p>
            <p className="ant-upload-hint">{tr("支持 exe / msi / msp / zip / 7z / bin，单文件上传")}</p>
          </>
        )}
      </Upload.Dragger>

      <Space wrap style={{ marginBottom: 12 }}>
        <Upload
          accept=".json,application/json"
          maxCount={1}
          showUploadList={false}
          disabled={mutation.isPending || succeeded}
          beforeUpload={(selected) => {
            setManifestFile(selected);
            return false;
          }}
        >
          <Button>{manifestFile ? tr("更换旁挂清单") : tr("选择旁挂清单（可选）")}</Button>
        </Upload>
        {manifestFile ? (
          <Tag closable onClose={() => setManifestFile(null)}>
            {manifestFile.name}
          </Tag>
        ) : null}
        <Typography.Text type="secondary">
          {tr("不选则由服务端从包内探测清单；旁挂清单适用于 exe 未内嵌清单的场景。")}</Typography.Text>
      </Space>

      {largeFile ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message={tr("安装包超过 256MB")}
          description={tr("浏览器端会分块计算 SHA-256 并以流式上传，耗时取决于磁盘与网络；超大产物建议用发行脚本/CI 上传。")}
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
          message={tr("发布包 {{arg0}} 处理失败：{{arg1}}", { arg0: current.fileName, arg1: current.errorCode ?? tr("未知错误") })}
          description={current.errorMessage ?? tr("请检查清单内容与文件名约定后重试。")}
          action={
            <Button
              size="small"
              icon={<ReloadOutlined />}
              loading={retry.isPending}
              onClick={() => void retry.mutateAsync()}
            >
              {tr("重试摄取")}</Button>
          }
        />
      ) : null}

      {current && (current.status === 'pending' || current.status === 'processing') ? (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message={`${current.fileName} ${packageStatusMeta(current.status).label}`}
          description={tr("服务端正在异步解析文件清单；完成后这里会显示文件数与失败原因。")}
        />
      ) : null}

      {current && DONE_STATUSES.includes(current.status) ? (
        <Descriptions
          size="small"
          column={1}
          bordered
          title={tr("登记结果（草稿）")}
          items={[
            {
              key: 'channel',
              label: tr("渠道"),
              children: <Tag color="blue">{current.channelKey}</Tag>,
            },
            {
              key: 'version',
              label: tr("版本"),
              children: <Typography.Text strong>{current.version}</Typography.Text>,
            },
            {
              key: 'kind',
              label: tr("包类型"),
              children: (
                <Space size={4}>
                  <Tag color={packageKindMeta(current.kind).color}>
                    {packageKindMeta(current.kind).label}
                  </Tag>
                  <Tag>{current.format.toUpperCase()}</Tag>
                </Space>
              ),
            },
            {
              key: 'target',
              label: tr("目标运行时"),
              children: <Typography.Text code>{current.target}</Typography.Text>,
            },
            {
              key: 'size',
              label: tr("大小"),
              children: formatBytes(current.sizeBytes),
            },
            {
              key: 'manifest',
              label: tr("清单来源"),
              children: manifestSourceLabel(current.manifestSource),
            },
          ]}
        />
      ) : null}

      {succeeded ? (
        <Alert
          type="success"
          showIcon
          style={{ marginTop: 12 }}
          message={tr("已登记为草稿版本，尚未发布")}
          description={tr("点击「查看草稿版本」核对信息并编辑发布说明；发布前还会再确认一次。")}
        />
      ) : null}
    </Modal>
  );
}