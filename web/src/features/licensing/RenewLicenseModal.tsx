import { useMutation } from '@tanstack/react-query';
import { App, DatePicker, Descriptions, Form, Input, InputNumber, Modal, Radio } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { licensingApi } from '../../shared/api/licensing';
import type { LicenseEntitlement, RenewLicenseRequest } from '../../shared/api/types';

interface RenewFormValues {
  mode: 'days' | 'date';
  addDays?: number;
  notAfter?: Dayjs;
  graceDays?: number | null;
  notes?: string;
}

interface RenewLicenseModalProps {
  open: boolean;
  entitlement: LicenseEntitlement | null;
  onClose: () => void;
  onSaved: () => void;
}

/** 续期：按天数从「当前到期时间与现在取较晚者」追加，避免过期很久后一次性补足历史时间。 */
export function RenewLicenseModal({ open, entitlement, onClose, onSaved }: RenewLicenseModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<RenewFormValues>();

  const mutation = useMutation({
    mutationFn: (values: RenewFormValues) => {
      if (!entitlement) {
        throw new Error('未选择授权');
      }

      const body: RenewLicenseRequest = {
        addDays: values.mode === 'days' ? (values.addDays ?? null) : null,
        notAfter:
          values.mode === 'date' && values.notAfter
            ? values.notAfter.endOf('day').toISOString()
            : null,
        graceDays: values.graceDays ?? null,
        notes: values.notes ?? null,
      };
      return licensingApi.renewLicense(entitlement.id, body);
    },
    onSuccess: () => {
      message.success('授权已续期');
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
      title="续期授权"
      okText="续期"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      {entitlement ? (
        <Descriptions size="small" column={1} style={{ marginBottom: 16 }}>
          <Descriptions.Item label="产品">{entitlement.productKey}</Descriptions.Item>
          <Descriptions.Item label="机器码">{entitlement.machineCode}</Descriptions.Item>
          <Descriptions.Item label="当前到期">
            {new Date(entitlement.notAfter).toLocaleDateString()}
          </Descriptions.Item>
        </Descriptions>
      ) : null}

      <Form<RenewFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ mode: 'days', addDays: 365 }}
      >
        <Form.Item name="mode" label="续期方式">
          <Radio.Group
            optionType="button"
            buttonStyle="solid"
            options={[
              { value: 'days', label: '追加天数' },
              { value: 'date', label: '指定新到期日' },
            ]}
          />
        </Form.Item>

        <Form.Item noStyle shouldUpdate={(prev, next) => prev.mode !== next.mode}>
          {({ getFieldValue }) =>
            getFieldValue('mode') === 'days' ? (
              <Form.Item
                name="addDays"
                label="追加天数"
                rules={[{ required: true, message: '请输入追加天数' }]}
              >
                <InputNumber min={1} max={3650} style={{ width: '100%' }} />
              </Form.Item>
            ) : (
              <Form.Item
                name="notAfter"
                label="新到期日"
                rules={[{ required: true, message: '请选择新到期日' }]}
              >
                <DatePicker
                  style={{ width: '100%' }}
                  disabledDate={(date) => date.isBefore(dayjs(), 'day')}
                />
              </Form.Item>
            )
          }
        </Form.Item>

        <Form.Item name="graceDays" label="宽限天数" tooltip="留空沿用原值。">
          <InputNumber min={0} max={30} style={{ width: '100%' }} placeholder="保持不变" />
        </Form.Item>

        <Form.Item name="notes" label="备注" rules={[{ max: 512 }]}>
          <Input.TextArea rows={2} placeholder="可选" />
        </Form.Item>
      </Form>
    </Modal>
  );
}
