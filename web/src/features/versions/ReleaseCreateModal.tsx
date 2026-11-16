import { tr } from '../../i18n/translate';
import { DeleteOutlined, FileOutlined, FolderOpenOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Flex,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Typography,
  Upload,
} from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type {
  CreateReleaseRequest,
  ReleaseChannel,
  ReleaseFileInput,
} from '../../shared/api/types';
import { hashFile, normalizeReleasePath } from '../../shared/files';
import { formatBytes } from '../../shared/format';
import { responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';

interface ReleaseFormValues {
  channelKey: string;
  version: string;
  notes?: string;
  publish: boolean;
  runtimeIdentifier: string;
}

interface SelectedFile {
  path: string;
  size: number;
  file: File;
}

interface ReleaseCreateModalProps {
  open: boolean;
  channels: ReleaseChannel[];
  onClose: () => void;
  onSaved: () => void;
}

const RUNTIMES = ['win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'];

const LARGE_FILE_BYTES = 256 * 1024 * 1024;

/**
 * 新建版本：先选文件，再逐文件申请一次性上传票据并直传，最后按清单创建版本。
 *
 * 哈希在浏览器端计算（整文件读入内存）；大产物建议用发行脚本/CI 上传。
 */
export function ReleaseCreateModal({ open, channels, onClose, onSaved }: ReleaseCreateModalProps) {
  const { message } = App.useApp();
  const { isMobile } = useResponsive();
  const [form] = Form.useForm<ReleaseFormValues>();
  const [files, setFiles] = useState<SelectedFile[]>([]);
  const [progress, setProgress] = useState<{ done: number; total: number; current: string } | null>(null);
  const publishNow = Form.useWatch('publish', form) ?? true;

  const totalBytes = useMemo(
    () => files.reduce((sum, item) => sum + item.size, 0),
    [files],
  );
  const hasLargeFile = files.some((item) => item.size > LARGE_FILE_BYTES);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({
        channelKey: channels[0]?.key,
        version: '',
        notes: '',
        publish: true,
        runtimeIdentifier: RUNTIMES[0],
      });
    }
  }, [open, channels, form]);

  const mutation = useMutation({
    mutationFn: async (values: ReleaseFormValues) => {
      const inputs: ReleaseFileInput[] = [];
      setProgress({ done: 0, total: files.length, current: '' });

      for (const [index, item] of files.entries()) {
        setProgress({ done: index, total: files.length, current: item.path });
        const sha256 = await hashFile(item.file);
        const ticket = await versionsApi.createUploadTicket({ sha256, sizeBytes: item.size });
        await versionsApi.upload(ticket, item.file);
        inputs.push({ path: item.path, sha256, sizeBytes: item.size });
      }

      setProgress({ done: files.length, total: files.length, current: '' });
      const body: CreateReleaseRequest = {
        channelKey: values.channelKey,
        version: values.version,
        notes: values.notes?.trim() ? values.notes : null,
        publish: values.publish,
        artifacts: [{ runtimeIdentifier: values.runtimeIdentifier, files: inputs }],
      };
      return versionsApi.createRelease(body);
    },
    onSuccess: (release) => {
      message.success(
        tr("版本 {{arg0}} 已{{arg1}}", { arg0: release.version, arg1: release.status === 'published' ? tr("创建并发布") : tr("保存为草稿") }),
      );
      onSaved();
      onClose();
    },
    onError: (error: Error) => message.error(error.message),
    onSettled: () => setProgress(null),
  });

  const addFiles = (incoming: File[], fromDirectory: boolean) => {
    const additions: SelectedFile[] = [];
    let rejected = 0;

    incoming.forEach((raw) => {
      const relative = raw.webkitRelativePath || raw.name;
      const path = normalizeReleasePath(relative, fromDirectory);
      if (
        !path ||
        path.length > 512 ||
        files.some((existing) => existing.path === path) ||
        additions.some((existing) => existing.path === path)
      ) {
        rejected += 1;
        return;
      }

      additions.push({ path, size: raw.size, file: raw });
    });

    if (additions.length > 0) {
      setFiles((current) => [...current, ...additions]);
    }

    if (rejected > 0) {
      message.warning(
        tr('{{count}} 个文件路径重复或非法，已跳过', { count: rejected }),
      );
    }
  };

  const handleOk = async () => {
    if (files.length === 0) {
      message.error(tr("请先选择要发布的文件"));
      return;
    }

    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    try {
      await mutation.mutateAsync(values);
    } catch {
      // 错误已由 onError 提示；保留已选文件以便重试
    }
  };

  return (
    <Modal
      open={open}
      width={responsiveWidth(isMobile, 840)}
      title={tr("新建版本")}
      okText={publishNow ? tr("创建并发布") : tr("保存草稿")}
      cancelText={tr("取消")}
      confirmLoading={mutation.isPending}
      maskClosable={!mutation.isPending}
      closable={!mutation.isPending}
      onCancel={mutation.isPending ? undefined : onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
        setFiles([]);
        setProgress(null);
      }}
    >
      <Form<ReleaseFormValues> form={form} layout="vertical" requiredMark={false}>
        <Flex wrap gap={12} align="flex-start">
          <Form.Item
            name="channelKey"
            label={tr("渠道")}
            style={{ flex: '1 1 220px', minWidth: 0 }}
            rules={[{ required: true, message: tr("请选择渠道") }]}
          >
            <Select
              placeholder={tr("选择渠道")}
              options={channels.map((channel) => ({
                value: channel.key,
                label: `${channel.name}（${channel.key}）`,
              }))}
            />
          </Form.Item>

          <Form.Item
            name="runtimeIdentifier"
            label={tr("运行时标识")}
            style={{ flex: '1 1 200px', minWidth: 0 }}
            rules={[{ required: true }]}
          >
            <Select options={RUNTIMES.map((value) => ({ value, label: value }))} />
          </Form.Item>

          <Form.Item
            name="version"
            label={tr("版本号")}
            style={{ flex: '1 1 220px', minWidth: 0 }}
            rules={[
              { required: true, message: tr("请输入版本号") },
              {
                pattern:
                  /^\d{1,5}\.\d{1,5}\.\d{1,5}(\.\d{1,5})?(-[0-9A-Za-z.-]{1,32})?(\+[0-9A-Za-z.-]{1,32})?$/,
                message: tr("例如 1.2.3、1.2.3.0 或 1.2.3-rc.1"),
              },
            ]}
          >
            <Input placeholder="1.2.3" autoComplete="off" />
          </Form.Item>
        </Flex>

        <Form.Item name="notes" label={tr("发布说明")} rules={[{ max: 4000 }]}>
          <Input.TextArea rows={2} placeholder={tr("可选；客户端更新提示可直接展示")} />
        </Form.Item>

        <Form.Item
          name="publish"
          label={tr("创建后立即发布")}
          valuePropName="checked"
          tooltip={tr("草稿对客户端不可见；发布后 check 才会下发给该版本。")}
        >
          <Switch />
        </Form.Item>
      </Form>

      <Space wrap style={{ marginBottom: 12 }}>
        <Upload
          multiple
          showUploadList={false}
          beforeUpload={(file) => {
            addFiles([file], false);
            return false;
          }}
        >
          <Button icon={<PlusOutlined />}>{tr("选择文件")}</Button>
        </Upload>
        <Upload
          directory
          showUploadList={false}
          beforeUpload={(file) => {
            addFiles([file], true);
            return false;
          }}
        >
          <Button icon={<FolderOpenOutlined />}>{tr("选择文件夹")}</Button>
        </Upload>
        <Typography.Text type="secondary">
          {files.length} {tr("个文件 · 共")}{formatBytes(totalBytes)}
        </Typography.Text>
        {files.length > 0 ? (
          <Button size="small" onClick={() => setFiles([])} disabled={mutation.isPending}>
            {tr("清空")}</Button>
        ) : null}
      </Space>

      {hasLargeFile ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message={tr("存在超过 256MB 的文件")}
          description={tr("浏览器端计算 SHA-256 需要整文件读入内存；超大产物请改用发行脚本/CI 通过上传票据上传。")}
        />
      ) : null}

      {progress ? (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message={tr("上传中 {{arg0}}/{{arg1}}{{arg2}}", { arg0: progress.done, arg1: progress.total, arg2: progress.current ? ` · ${progress.current}` : '' })}
        />
      ) : null}

      <Table<SelectedFile>
        rowKey="path"
        size="small"
        dataSource={files}
        pagination={false}
        scroll={{ x: 'max-content', y: 240 }}
        locale={{ emptyText: tr("选择文件或文件夹后在此确认清单（路径相对安装目录）") }}
        columns={[
          {
            title: tr("清单路径"),
            dataIndex: 'path',
            key: 'path',
            render: (path: string) => (
              <Space size={4}>
                <FileOutlined />
                <Typography.Text code>{path}</Typography.Text>
              </Space>
            ),
          },
          {
            title: tr("大小"),
            dataIndex: 'size',
            key: 'size',
            width: 110,
            render: (size: number) => formatBytes(size),
          },
          {
            title: '',
            key: 'actions',
            width: 60,
            render: (_: unknown, row: SelectedFile) => (
              <Button
                type="text"
                danger
                size="small"
                icon={<DeleteOutlined />}
                aria-label={tr("移除 {{arg0}}", { arg0: row.path })}
                disabled={mutation.isPending}
                onClick={() => setFiles((current) => current.filter((item) => item.path !== row.path))}
              />
            ),
          },
        ]}
      />

      <Typography.Paragraph type="secondary" style={{ marginTop: 12, marginBottom: 0, fontSize: 12 }}>
        {tr("目录选择的根目录会被去掉；文件内容按 SHA-256 去重存储，重复内容不会占用额外空间。")}<Tag style={{ marginLeft: 8 }}>{tr("路径分隔符统一为 /")}</Tag>
      </Typography.Paragraph>
    </Modal>
  );
}
