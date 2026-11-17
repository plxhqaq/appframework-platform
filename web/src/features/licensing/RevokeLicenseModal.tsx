import { tr } from '../../i18n/translate';
import { useMutation } from '@tanstack/react-query';
import { Alert, App, Form, Input, Modal, Radio } from 'antd';
import { licensingApi } from '../../shared/api/licensing';
import type { LicenseEntitlement, LicenseRevokeScope } from '../../shared/api/types';

interface RevokeFormValues {
  scope: LicenseRevokeScope;
  reason?: string;
}

interface RevokeLicenseModalProps {
  open: boolean;
  entitlement: LicenseEntitlement | null;
  onClose: () => void;
  onSaved: () => void;
}

/**
 * 吊销：all 连离线授权码一起失效（客户端直接锁定）；online 只注销在线授权，
 * 服务端按未授权下发，老客户端会清掉在线快照并继续认已签发的离线码。
 */
export function RevokeLicenseModal({ open, entitlement, onClose, onSaved }: RevokeLicenseModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<RevokeFormValues>();
  const scope = Form.useWatch('scope', form) ?? 'all';

  const mutation = useMutation({
    mutationFn: (values: RevokeFormValues) => {
      if (!entitlement) {
        throw new Error(tr("未选择授权"));
      }
      return licensingApi.revokeLicense(entitlement.id, {
        scope: values.scope,
        reason: values.reason?.trim() || null,
      });
    },
    onSuccess: (revoked) => {
      message.success(
        revoked.revokeScope === 'online'
          ? tr("已注销在线授权（离线授权码仍有效）")
          : tr("授权已吊销"),
      );
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
        message={scope === 'online' ? tr("仅注销在线授权") : tr("吊销后不可恢复")}
        description={
          entitlement
            ? scope === 'online'
              ? tr('{{productKey}} · {{machineCode}} 的在线授权立即失效；已签发的离线授权码不受影响，仍可激活。', {
                  productKey: entitlement.productKey,
                  machineCode: entitlement.machineCode,
                })
              : tr('{{productKey}} · {{machineCode}} 将立即失去授权（含离线授权码）；需要恢复时只能重新签发。', {
                  productKey: entitlement.productKey,
                  machineCode: entitlement.machineCode,
                })
            : undefined
        }
      />

      <Form<RevokeFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ scope: 'all' }}
      >
        <Form.Item name="scope" label={tr("吊销范围")}>
          <Radio.Group
            options={[
              { value: 'all', label: tr("完全吊销（在线 + 离线授权码）") },
              { value: 'online', label: tr("仅注销在线授权（离线授权码仍有效）") },
            ]}
          />
        </Form.Item>
        <Form.Item name="reason" label={tr("原因")} rules={[{ max: 256 }]}>
          <Input.TextArea rows={2} placeholder={tr("例如 客户退订 / 换机")} />
        </Form.Item>
      </Form>
    </Modal>
  );
}