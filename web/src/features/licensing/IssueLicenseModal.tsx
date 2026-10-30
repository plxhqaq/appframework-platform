import { useMutation } from '@tanstack/react-query';
import { App, DatePicker, Form, Input, InputNumber, Modal, Radio, Select } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { licensingApi } from '../../shared/api/licensing';
import type { IssueLicenseRequest, LicenseProduct } from '../../shared/api/types';

interface IssueFormValues {
  productKey: string;
  machineCode: string;
  validity: 'days' | 'date';
  addDays?: number;
  notAfter?: Dayjs;
  licenseNo?: string;
  graceDays?: number | null;
  notes?: string;
}

interface IssueLicenseModalProps {
  open: boolean;
  products: LicenseProduct[];
  onClose: () => void;
  onSaved: () => void;
}

/**
 * 签发正式授权。机器码是授权主体：客户端上报 16 字符机器码，
 * 授权复制到另一台机器后验签依旧通过但机器码不匹配，服务端直接拒绝。
 */
export function IssueLicenseModal({ open, products, onClose, onSaved }: IssueLicenseModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<IssueFormValues>();

  const mutation = useMutation({
    mutationFn: (values: IssueFormValues) => {
      const body: IssueLicenseRequest = {
        productKey: values.productKey,
        machineCode: values.machineCode.trim(),
        licenseNo: values.licenseNo?.trim() || null,
        addDays: values.validity === 'days' ? (values.addDays ?? null) : null,
        notAfter:
          values.validity === 'date' && values.notAfter
            ? values.notAfter.endOf('day').toISOString()
            : null,
        graceDays: values.graceDays ?? null,
        notes: values.notes ?? null,
      };
      return licensingApi.issueLicense(body);
    },
    onSuccess: (license) => {
      message.success(`已签发 ${license.productKey} · ${license.machineCode}`);
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
      title="签发授权"
      okText="签发"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Form<IssueFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ validity: 'days', addDays: 365 }}
      >
        <Form.Item
          name="productKey"
          label="产品"
          rules={[{ required: true, message: '请选择产品' }]}
        >
          <Select
            placeholder="选择授权产品"
            options={products
              .filter((product) => product.isEnabled)
              .map((product) => ({ value: product.key, label: `${product.name}（${product.key}）` }))}
          />
        </Form.Item>

        <Form.Item
          name="machineCode"
          label="机器码"
          tooltip="客户端「关于/授权」页展示的 16 字符机器码；手抄的 O/I/L 会被自动纠正。"
          rules={[
            { required: true, message: '请输入机器码' },
            {
              pattern: /^[0-9A-Za-z\-_\s]{16,24}$/,
              message: '机器码应为 16 位 Crockford Base32（可带分隔符）',
            },
          ]}
        >
          <Input placeholder="例如 008J-4CT4 ANK7-F24S" autoComplete="off" />
        </Form.Item>

        <Form.Item name="validity" label="有效期">
          <Radio.Group
            optionType="button"
            buttonStyle="solid"
            options={[
              { value: 'days', label: '按天数' },
              { value: 'date', label: '指定日期' },
            ]}
          />
        </Form.Item>

        <Form.Item noStyle shouldUpdate={(prev, next) => prev.validity !== next.validity}>
          {({ getFieldValue }) =>
            getFieldValue('validity') === 'days' ? (
              <Form.Item
                name="addDays"
                label="有效天数"
                rules={[{ required: true, message: '请输入有效天数' }]}
              >
                <InputNumber min={1} max={3650} style={{ width: '100%' }} />
              </Form.Item>
            ) : (
              <Form.Item
                name="notAfter"
                label="到期日"
                rules={[{ required: true, message: '请选择到期日' }]}
              >
                <DatePicker style={{ width: '100%' }} disabledDate={(date) => date.isBefore(dayjs(), 'day')} />
              </Form.Item>
            )
          }
        </Form.Item>

        <Form.Item
          name="graceDays"
          label="宽限天数"
          tooltip="留空使用产品默认值；到期后客户端仍可离线运行的天数。"
        >
          <InputNumber min={0} max={30} style={{ width: '100%' }} placeholder="产品默认" />
        </Form.Item>

        <Form.Item
          name="licenseNo"
          label="授权编号"
          tooltip="留空自动生成；建议与销售合同的编号一致，便于现场定位。"
          rules={[{ max: 32 }]}
        >
          <Input placeholder="例如 LIC-20261030-001" autoComplete="off" />
        </Form.Item>

        <Form.Item name="notes" label="备注" rules={[{ max: 512 }]}>
          <Input.TextArea rows={2} placeholder="可选，仅管理端可见" />
        </Form.Item>
      </Form>
    </Modal>
  );
}
