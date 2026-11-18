import { tr } from '../../i18n/translate';
import { useMutation } from '@tanstack/react-query';
import { App, Form, Input, InputNumber, Modal, Switch } from 'antd';
import { useEffect } from 'react';
import { licensingApi } from '../../shared/api/licensing';
import type {
  CreateLicenseProductRequest,
  LicenseProduct,
  UpdateLicenseProductRequest,
} from '../../shared/api/types';

interface ProductFormValues {
  key: string;
  name: string;
  description?: string;
  seatLimit?: number | null;
  defaultGraceDays: number;
  trialDays: number;
  isEnabled: boolean;
}

interface ProductFormModalProps {
  open: boolean;
  /** 为空表示新建。 */
  product: LicenseProduct | null;
  onClose: () => void;
  /** 保存成功后回传服务端结果，供列表立刻落位。 */
  onSaved: (product: LicenseProduct) => void;
}

/** 授权产品新建/编辑：产品键创建后不可修改（客户端与离线码按它引用）。 */
export function ProductFormModal({ open, product, onClose, onSaved }: ProductFormModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<ProductFormValues>();
  const isEdit = product !== null;

  useEffect(() => {
    if (!open) {
      return;
    }

    form.setFieldsValue({
      key: product?.key ?? '',
      name: product?.name ?? '',
      description: product?.description ?? '',
      seatLimit: product?.seatLimit ?? null,
      defaultGraceDays: product?.defaultGraceDays ?? 7,
      trialDays: product?.trialDays ?? 0,
      isEnabled: product?.isEnabled ?? true,
    });
  }, [open, product, form]);

  const mutation = useMutation({
    mutationFn: async (values: ProductFormValues) => {
      if (product) {
        const body: UpdateLicenseProductRequest = {
          name: values.name,
          description: values.description ?? null,
          isEnabled: values.isEnabled,
          seatLimit: values.seatLimit ?? null,
          defaultGraceDays: values.defaultGraceDays,
          trialDays: values.trialDays,
        };
        return licensingApi.updateProduct(product.id, body);
      }

      const body: CreateLicenseProductRequest = {
        key: values.key,
        name: values.name,
        description: values.description ?? null,
        seatLimit: values.seatLimit ?? null,
        defaultGraceDays: values.defaultGraceDays,
        trialDays: values.trialDays,
      };
      return licensingApi.createProduct(body);
    },
    onSuccess: (saved) => {
      message.success(isEdit ? tr("产品已更新") : tr("产品已创建"));
      onSaved(saved);
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
      title={isEdit ? tr("编辑产品 · {{arg0}}", { arg0: product.key }) : tr("新建授权产品")}
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
      <Form<ProductFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item
          name="key"
          label={tr("产品键")}
          tooltip={tr("客户端校验授权时按产品键引用；创建后不可修改。")}
          rules={[
            { required: true, message: tr("请输入产品键") },
            {
              pattern: /^[a-z][a-z0-9-]{1,31}$/,
              message: tr("格式：小写字母开头，2–32 位小写字母/数字/连字符"),
            },
          ]}
        >
          <Input placeholder={tr("例如 erp-pro")} disabled={isEdit} autoComplete="off" />
        </Form.Item>

        <Form.Item
          name="name"
          label={tr("名称")}
          rules={[{ required: true, message: tr("请输入产品名称") }, { max: 256 }]}
        >
          <Input placeholder={tr("例如 旗舰版")} autoComplete="off" />
        </Form.Item>

        <Form.Item name="description" label={tr("说明")} rules={[{ max: 1024 }]}>
          <Input.TextArea rows={2} placeholder={tr("可选，对销售/运维可见")} />
        </Form.Item>

        <Form.Item
          name="seatLimit"
          label={tr("席位上限")}
          tooltip={tr("留空表示不限；有效授权（含试用）计入席位。")}
        >
          <InputNumber min={1} max={100000} style={{ width: '100%' }} placeholder={tr("不限")} />
        </Form.Item>

        <Form.Item
          name="defaultGraceDays"
          label={tr("默认宽限天数")}
          tooltip={tr("仅作新签发授权与自动试用时的默认值；已签发授权各自保存快照，改这里不影响它们。")}
          rules={[{ required: true, message: tr("请输入宽限天数") }]}
        >
          <InputNumber min={0} max={30} style={{ width: '100%' }} />
        </Form.Item>

        <Form.Item
          name="trialDays"
          label={tr("自动试用天数")}
          tooltip={tr("大于 0 时，无授权的机器首次在线校验状态会自动获得试用；客户转正时签发正式授权就地升级。")}
          rules={[{ required: true, message: tr("请输入试用天数") }]}
        >
          <InputNumber min={0} max={365} style={{ width: '100%' }} />
        </Form.Item>

        {isEdit ? (
          <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
            <Switch />
          </Form.Item>
        ) : null}
      </Form>
    </Modal>
  );
}
