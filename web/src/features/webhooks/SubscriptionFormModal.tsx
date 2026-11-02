import { useMutation } from '@tanstack/react-query';
import { App, Form, Input, Modal, Select, Switch, Typography } from 'antd';
import { useEffect } from 'react';
import { WEBHOOK_EVENT_TYPES, WEBHOOK_MAX_EVENT_TYPES, webhookApi } from '../../shared/api/webhooks';
import type { WebhookSubscription } from '../../shared/api/types';

interface SubscriptionFormValues {
  url: string;
  eventTypes: string[];
  description?: string;
  isEnabled: boolean;
}

interface SubscriptionFormModalProps {
  open: boolean;
  /** 为空表示新建。 */
  subscription: WebhookSubscription | null;
  onClose: () => void;
  /** 新建成功时回传一次性密钥与其接收地址（编辑没有密钥可返回）。 */
  onCreated: (secret: string, url: string) => void;
  onSaved: () => void;
}

/** 订阅新建/编辑：地址与事件白名单由服务端再校验一次，前端只做提示。 */
export function SubscriptionFormModal({
  open,
  subscription,
  onClose,
  onCreated,
  onSaved,
}: SubscriptionFormModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<SubscriptionFormValues>();
  const isEdit = subscription !== null;

  useEffect(() => {
    if (!open) {
      return;
    }

    form.setFieldsValue({
      url: subscription?.url ?? '',
      eventTypes: subscription?.eventTypes ?? [],
      description: subscription?.description ?? '',
      isEnabled: subscription?.isEnabled ?? true,
    });
  }, [open, subscription, form]);

  const mutation = useMutation({
    // 新建要带出一次性密钥与其接收地址；编辑没有密钥可返回，统一成同一个可空形状避免联合类型取值
    mutationFn: async (values: SubscriptionFormValues): Promise<{ secret: string; url: string } | null> => {
      const eventTypes = values.eventTypes ?? [];
      if (subscription) {
        await webhookApi.updateSubscription(subscription.id, {
          url: values.url.trim(),
          eventTypes,
          description: values.description?.trim() ?? null,
          isEnabled: values.isEnabled,
        });
        return null;
      }

      const created = await webhookApi.createSubscription({
        url: values.url.trim(),
        eventTypes,
        description: values.description?.trim() ?? null,
        isEnabled: values.isEnabled,
      });
      return { secret: created.secret, url: created.subscription.url };
    },
    onSuccess: (result) => {
      if (result === null) {
        message.success('订阅已更新');
      } else {
        onCreated(result.secret, result.url);
      }
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
      title={isEdit ? '编辑订阅' : '新建 Webhook 订阅'}
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
      <Form<SubscriptionFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item
          name="url"
          label="接收地址"
          tooltip="必须是 http/https 绝对地址；投递时会带 X-Platform-Signature / Timestamp / Event / Delivery 四个头。"
          rules={[
            { required: true, message: '请输入接收地址' },
            {
              validator: (_, value: string) => {
                const text = value?.trim() ?? '';
                if (!text) {
                  return Promise.reject(new Error('请输入接收地址'));
                }
                const parsed = /^https?:\/\/.+/i.test(text);
                return parsed ? Promise.resolve() : Promise.reject(new Error('仅支持 http/https 绝对地址'));
              },
            },
            { max: 2048, message: '地址不超过 2048 字符' },
          ]}
        >
          <Input placeholder="例如 https://hooks.example.com/platform" autoComplete="off" />
        </Form.Item>

        <Form.Item
          name="eventTypes"
          label={
            <span>
              订阅事件{' '}
              <Typography.Text type="secondary">（留空 = 订阅全部事件）</Typography.Text>
            </span>
          }
          tooltip={`最多 ${WEBHOOK_MAX_EVENT_TYPES} 个；「测试投递」不受白名单限制，任何订阅都能收到 webhook.test。`}
          rules={[
            {
              type: 'array',
              max: WEBHOOK_MAX_EVENT_TYPES,
              message: `最多订阅 ${WEBHOOK_MAX_EVENT_TYPES} 个事件`,
            },
          ]}
        >
          <Select
            mode="multiple"
            allowClear
            placeholder="留空表示全部事件"
            options={WEBHOOK_EVENT_TYPES.map((item) => ({
              value: item.value,
              label: `${item.label}（${item.value}）`,
            }))}
          />
        </Form.Item>

        <Form.Item name="description" label="说明" rules={[{ max: 1024 }]}>
          <Input.TextArea rows={2} placeholder="可选，例如「产线告警群机器人中转」" />
        </Form.Item>

        <Form.Item
          name="isEnabled"
          label="启用"
          valuePropName="checked"
          tooltip="停用后不再投递任何事件；已入队的投递记录保持原状态，台账仍可读。"
        >
          <Switch />
        </Form.Item>
      </Form>
    </Modal>
  );
}
