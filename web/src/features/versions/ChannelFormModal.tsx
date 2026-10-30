import { useMutation } from '@tanstack/react-query';
import { App, Form, Input, Modal, Switch } from 'antd';
import { useEffect } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { CreateReleaseChannelRequest, ReleaseChannel, UpdateReleaseChannelRequest } from '../../shared/api/types';

interface ChannelFormValues {
  key: string;
  name: string;
  description?: string;
  isEnabled: boolean;
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
    });
  }, [open, channel, form]);

  const mutation = useMutation({
    mutationFn: async (values: ChannelFormValues) => {
      if (channel) {
        const body: UpdateReleaseChannelRequest = {
          name: values.name,
          description: values.description ?? null,
          isEnabled: values.isEnabled,
        };
        return versionsApi.updateChannel(channel.id, body);
      }

      const body: CreateReleaseChannelRequest = {
        key: values.key,
        name: values.name,
        description: values.description ?? null,
      };
      return versionsApi.createChannel(body);
    },
    onSuccess: () => {
      message.success(isEdit ? '渠道已更新' : '渠道已创建');
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
      title={isEdit ? `编辑渠道 · ${channel.key}` : '新建渠道'}
      okText="保存"
      cancelText="取消"
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
          label="渠道键"
          tooltip="客户端检查更新时按渠道键引用；创建后不可修改。"
          rules={[
            { required: true, message: '请输入渠道键' },
            {
              pattern: /^[a-z][a-z0-9-]{1,31}$/,
              message: '格式：小写字母开头，2–32 位小写字母/数字/连字符',
            },
          ]}
        >
          <Input placeholder="例如 stable" disabled={isEdit} autoComplete="off" />
        </Form.Item>

        <Form.Item
          name="name"
          label="名称"
          rules={[{ required: true, message: '请输入渠道名称' }, { max: 256 }]}
        >
          <Input placeholder="例如 稳定版" autoComplete="off" />
        </Form.Item>

        <Form.Item name="description" label="说明" rules={[{ max: 1024 }]}>
          <Input.TextArea rows={3} placeholder="可选，给运维/客户端同学看的用途说明" />
        </Form.Item>

        {isEdit ? (
          <Form.Item
            name="isEnabled"
            label="启用"
            valuePropName="checked"
            tooltip="停用后不能再创建新版本；已发布版本的下载不受影响。"
          >
            <Switch />
          </Form.Item>
        ) : null}
      </Form>
    </Modal>
  );
}
