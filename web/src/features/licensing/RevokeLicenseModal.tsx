import { useMutation } from '@tanstack/react-query';
import { Alert, App, Form, Input, Modal } from 'antd';
import { licensingApi } from '../../shared/api/licensing';
import type { LicenseEntitlement } from '../../shared/api/types';

interface RevokeFormValues {
  reason?: string;
}

interface RevokeLicenseModalProps {
  open: boolean;
  entitlement: LicenseEntitlement | null;
  onClose: () => void;
  onSaved: () => void;
}

/** 吊销：推送只是提示，客户端下次回拉状态时锁定；已吊销的授权只能重新签发。 */
export function RevokeLicenseModal({ open, entitlement, onClose, onSaved }: RevokeLicenseModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<RevokeFormValues>();

  const mutation = useMutation({
    mutationFn: (values: RevokeFormValues) => {
      if (!entitlement) {
        throw new Error('未选择授权');
      }
      return licensingApi.revokeLicense(entitlement.id, { reason: values.reason?.trim() || null });
    },
    onSuccess: () => {
      message.success('授权已吊销');
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
      title="吊销授权"
      okText="确认吊销"
      okButtonProps={{ danger: true }}
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Alert
        type="warning"
        showIcon
        style={{ marginBottom: 16 }}
        message="吊销后不可恢复"
        description={
          entitlement
            ? `${entitlement.productKey} · ${entitlement.machineCode} 将立即失去授权；需要恢复时只能重新签发。`
            : undefined
        }
      />

      <Form<RevokeFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item name="reason" label="原因" rules={[{ max: 256 }]}>
          <Input.TextArea rows={2} placeholder="例如 客户退订 / 换机" />
        </Form.Item>
      </Form>
    </Modal>
  );
}
