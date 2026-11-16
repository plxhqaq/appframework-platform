import { tr } from '../../i18n/translate';
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
        throw new Error(tr("未选择授权"));
      }
      return licensingApi.revokeLicense(entitlement.id, { reason: values.reason?.trim() || null });
    },
    onSuccess: () => {
      message.success(tr("授权已吊销"));
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
      title={tr("吊销授权")}
      okText={tr("确认吊销")}
      okButtonProps={{ danger: true }}
      cancelText={tr("取消")}
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
        message={tr("吊销后不可恢复")}
        description={
          entitlement
            ? tr('{{productKey}} · {{machineCode}} 将立即失去授权；需要恢复时只能重新签发。', {
                productKey: entitlement.productKey,
                machineCode: entitlement.machineCode,
              })
            : undefined
        }
      />

      <Form<RevokeFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item name="reason" label={tr("原因")} rules={[{ max: 256 }]}>
          <Input.TextArea rows={2} placeholder={tr("例如 客户退订 / 换机")} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
