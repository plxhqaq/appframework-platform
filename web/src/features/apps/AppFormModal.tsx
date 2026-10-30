import { useMutation, useQueryClient } from '@tanstack/react-query';
import { App, Form, Input, Modal, Switch } from 'antd';
import { useEffect } from 'react';
import { appsApi } from '../../shared/api/apps';
import type { AppDetail, CreateAppRequest, UpdateAppRequest } from '../../shared/api/types';

interface AppFormValues {
  key: string;
  name: string;
  description?: string;
  isEnabled: boolean;
}

interface AppFormModalProps {
  open: boolean;
  /** 为空表示新建。 */
  app: AppDetail | null;
  onClose: () => void;
}

/** 应用新建/编辑弹窗。应用键创建后不可改：它是业务数据的隔离轴。 */
export function AppFormModal({ open, app, onClose }: AppFormModalProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<AppFormValues>();
  const isEdit = app !== null;

  useEffect(() => {
    if (!open) {
      return;
    }

    form.setFieldsValue({
      key: app?.key ?? '',
      name: app?.name ?? '',
      description: app?.description ?? '',
      isEnabled: app?.isEnabled ?? true,
    });
  }, [open, app, form]);

  const mutation = useMutation({
    mutationFn: async (values: AppFormValues) => {
      if (app) {
        const body: UpdateAppRequest = {
          name: values.name,
          description: values.description ?? null,
          isEnabled: values.isEnabled,
        };
        return appsApi.update(app.id, body);
      }

      const body: CreateAppRequest = {
        key: values.key,
        name: values.name,
        description: values.description ?? null,
      };
      return appsApi.create(body);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['apps'] });
      message.success(isEdit ? '应用已更新' : '应用已创建');
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
      title={isEdit ? `编辑应用 · ${app.key}` : '新建应用'}
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
      <Form<AppFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ key: '', name: '', description: '', isEnabled: true }}
      >
        <Form.Item
          name="key"
          label="应用键"
          tooltip="小写字母开头，2–64 位小写字母/数字/连字符；创建后不可修改。"
          rules={[
            { required: true, message: '请输入应用键' },
            {
              pattern: /^[a-z][a-z0-9-]{1,63}$/,
              message: '格式：小写字母开头，2–64 位小写字母/数字/连字符',
            },
          ]}
        >
          <Input placeholder="例如 demo" disabled={isEdit} autoComplete="off" />
        </Form.Item>

        <Form.Item name="name" label="名称" rules={[{ required: true, message: '请输入应用名称' }]}>
          <Input placeholder="例如 演示应用" autoComplete="off" />
        </Form.Item>

        <Form.Item name="description" label="说明">
          <Input.TextArea rows={3} placeholder="可选：接入方与用途" />
        </Form.Item>

        {isEdit ? (
          <Form.Item name="isEnabled" label="启用" valuePropName="checked">
            <Switch />
          </Form.Item>
        ) : null}
      </Form>
    </Modal>
  );
}
