import { tr } from '../../i18n/translate';
import { useMutation } from '@tanstack/react-query';
import { App, Form, Input, Modal, Select, Switch } from 'antd';
import { useEffect } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { CreateReleaseChannelRequest, ReleaseChannel, UpdateReleaseChannelRequest } from '../../shared/api/types';

interface ChannelFormValues {
  key: string;
  name: string;
  description?: string;
  isEnabled: boolean;
  allowDowngrade: boolean;
  audienceTags?: string[];
}

const audienceTagPattern = /^[a-z0-9][a-z0-9._:-]{0,63}$/;

function normalizeTags(tags: string[] | undefined): string[] {
  return [...new Set((tags ?? []).map((tag) => tag.trim().toLowerCase()).filter(Boolean))].sort();
}

interface ChannelFormModalProps {
  open: boolean;
  /** 为空表示新建。 */
  channel: ReleaseChannel | null;
  onClose: () => void;
  onSaved: () => void;
}

/** 渠道新建/编辑：渠道键创建后不可修改（客户端的渠道配置按它引用）。 */
export function ChannelFormModal({ open, channel, onClose, onSaved }: ChannelFormModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<ChannelFormValues>();
  const isEdit = channel !== null;

  useEffect(() => {
    if (!open) {
      return;
    }

    form.setFieldsValue({
      key: channel?.key ?? '',
      name: channel?.name ?? '',
      description: channel?.description ?? '',
      isEnabled: channel?.isEnabled ?? true,
      allowDowngrade: channel?.allowDowngrade ?? false,
      audienceTags: channel?.audienceTags ?? [],
    });
  }, [open, channel, form]);

  const mutation = useMutation({
    mutationFn: async (values: ChannelFormValues) => {
      if (channel) {
        const body: UpdateReleaseChannelRequest = {
          name: values.name,
          description: values.description ?? null,
          isEnabled: values.isEnabled,
          allowDowngrade: values.allowDowngrade,
          audienceTags: normalizeTags(values.audienceTags),
        };
        return versionsApi.updateChannel(channel.id, body);
      }

      const body: CreateReleaseChannelRequest = {
        key: values.key,
        name: values.name,
        description: values.description ?? null,
        allowDowngrade: values.allowDowngrade,
        audienceTags: normalizeTags(values.audienceTags),
      };
      return versionsApi.createChannel(body);
    },
    onSuccess: () => {
      message.success(isEdit ? tr("渠道已更新") : tr("渠道已创建"));
      onSaved();
      onClose();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleOk = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    try {
      await mutation.mutateAsync(values);
    } catch {
      // 错误已由 onError 提示
    }
  };

  return (
    <Modal
      open={open}
      title={isEdit ? tr("编辑渠道 · {{arg0}}", { arg0: channel.key }) : tr("新建渠道")}
      okText={tr("保存")}
      cancelText={tr("取消")}
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Form<ChannelFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item
          name="key"
          label={tr("渠道键")}
          tooltip={tr("客户端检查更新时按渠道键引用；创建后不可修改。")}
          rules={[
            { required: true, message: tr("请输入渠道键") },
            {
              pattern: /^[a-z][a-z0-9-]{1,31}$/,
              message: tr("格式：小写字母开头，2–32 位小写字母/数字/连字符"),
            },
          ]}
        >
          <Input placeholder={tr("例如 stable")} disabled={isEdit} autoComplete="off" />
        </Form.Item>

        <Form.Item
          name="name"
          label={tr("名称")}
          rules={[{ required: true, message: tr("请输入渠道名称") }, { max: 256 }]}
        >
          <Input placeholder={tr("例如 稳定版")} autoComplete="off" />
        </Form.Item>

        <Form.Item name="description" label={tr("说明")} rules={[{ max: 1024 }]}>
          <Input.TextArea rows={3} placeholder={tr("可选，给运维/客户端同学看的用途说明")} />
        </Form.Item>

        <Form.Item
          name="audienceTags"
          label={tr("受众标签")}
          tooltip={tr("客户端 check 省略渠道键时，请求标签必须包含本渠道的全部标签才会命中；多个渠道命中时取标签最多的一个。留空表示只能按渠道键命中。")}
          rules={[
            { type: 'array', max: 16, message: tr("最多 16 个受众标签") },
            {
              validator: async (_rule, value?: string[]) => {
                const invalid = (value ?? []).filter(
                  (tag) => tag.trim() && !audienceTagPattern.test(tag.trim().toLowerCase()),
                );
                if (invalid.length > 0) {
                  throw new Error(
                    tr("标签 {{arg0}} 非法：小写字母/数字开头，最长 64 位，可含 . _ : -", { arg0: invalid.join('、') }),
                  );
                }
              },
            },
          ]}
        >
          <Select
            mode="tags"
            tokenSeparators={[',', ' ']}
            placeholder={tr("例如 win-x64、hardware:scanner")}
            allowClear
          />
        </Form.Item>

        <Form.Item
          name="allowDowngrade"
          label={tr("允许降级")}
          valuePropName="checked"
          tooltip={tr("归档最新版本后，客户端会收到 isDowngrade=true 并允许回退到上一版本。默认关闭，避免归档操作误让现场降级；仅在强制回退演练时临时打开。")}
        >
          <Switch />
        </Form.Item>

        {isEdit ? (
          <Form.Item
            name="isEnabled"
            label={tr("启用")}
            valuePropName="checked"
            tooltip={tr("停用后不能再创建新版本；已发布版本的下载不受影响。")}
          >
            <Switch />
          </Form.Item>
        ) : null}
      </Form>
    </Modal>
  );
}
