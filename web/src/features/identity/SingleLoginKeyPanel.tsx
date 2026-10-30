import { useMutation } from '@tanstack/react-query';
import { Alert, App, Button, Form, Input, InputNumber, Modal, Space, Typography } from 'antd';
import { useState } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { IssueSingleLoginKeyRequest, SingleLoginKey } from '../../shared/api/types';

/**
 * 单次密钥：给无法走扫码的现场人员发一个 6 位数字口令。
 * 明文只在签发响应里出现一次，关闭弹窗后服务端也无法回查。
 */
export function SingleLoginKeyPanel() {
  const { message } = App.useApp();
  const [form] = Form.useForm<IssueSingleLoginKeyRequest>();
  const [issued, setIssued] = useState<SingleLoginKey | null>(null);

  const issueMutation = useMutation({
    mutationFn: (body: IssueSingleLoginKeyRequest) => identityApi.issueSingleLoginKey(body),
    onSuccess: (result) => setIssued(result),
    onError: (error: Error) => message.error(error.message),
  });

  const handleIssue = async () => {
    const values = await form.validateFields();
    issueMutation.mutate({
      durationMinutes: values.durationMinutes ?? undefined,
      deviceFingerprint: values.deviceFingerprint?.trim() || undefined,
    });
  };

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message="单次密钥用于临时登录客户端"
        description="6 位数字、只能兑换一次；兑换成功后签发临时令牌，可用业务功能但不能维护用户与角色。"
      />

      <Form form={form} layout="inline" initialValues={{ durationMinutes: 30 }}>
        <Form.Item
          name="durationMinutes"
          label="有效期（分钟）"
          rules={[{ required: true, message: '请输入有效期' }]}
        >
          <InputNumber min={1} max={60} precision={0} />
        </Form.Item>
        <Form.Item name="deviceFingerprint" label="绑定设备指纹">
          <Input allowClear placeholder="留空则不限制设备" style={{ width: 260 }} />
        </Form.Item>
        <Form.Item>
          <Button type="primary" loading={issueMutation.isPending} onClick={() => void handleIssue()}>
            签发密钥
          </Button>
        </Form.Item>
      </Form>

      <Modal
        open={issued !== null}
        title="单次密钥已签发"
        onCancel={() => setIssued(null)}
        footer={
          <Button type="primary" onClick={() => setIssued(null)}>
            我已记录
          </Button>
        }
      >
        {issued ? (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Alert type="warning" showIcon message="关闭后无法再次查看，请立即告知使用人。" />
            <Typography.Title level={2} copyable style={{ margin: 0, letterSpacing: 8 }}>
              {issued.key}
            </Typography.Title>
            <Typography.Text type="secondary">
              有效期 {issued.durationMinutes} 分钟，{new Date(issued.expiresAt).toLocaleString()} 到期
            </Typography.Text>
          </Space>
        ) : null}
      </Modal>
    </Space>
  );
}
