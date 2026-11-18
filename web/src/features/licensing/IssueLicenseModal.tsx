import { tr } from '../../i18n/translate';
import { UnorderedListOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import { App, Button, DatePicker, Form, Input, InputNumber, Modal, Radio, Select } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useEffect, useMemo, useState } from 'react';
import { licensingApi } from '../../shared/api/licensing';
import type { IssueLicenseRequest, LicenseProduct, NodeSummary } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { NodePickerModal } from '../../shared/ui/NodePickerModal';
import { useAuthStore } from '../../stores/auth';

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
  /** 从试用行「转正式」时预填，避免重新找机器码。 */
  initial?: { productKey: string; machineCode: string } | null;
  onClose: () => void;
  onSaved: () => void;
}

/**
 * 签发正式授权。机器码是授权主体：客户端上报 16 字符机器码，
 * 授权复制到另一台机器后验签依旧通过但机器码不匹配，服务端直接拒绝。
 * 有 node.read 时可直接从节点目录里选（含离线节点），选中即回填机器码并带上 nodeId。
 */
export function IssueLicenseModal({ open, products, initial, onClose, onSaved }: IssueLicenseModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<IssueFormValues>();
  const canPickNode = useAuthStore((state) => state.hasPermission(PERMISSIONS.nodeRead));
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickedNode, setPickedNode] = useState<NodeSummary | null>(null);
  const machineCodeValue = Form.useWatch('machineCode', form);

  const pickerSelection = useMemo(
    () => new Map<string, NodeSummary>(pickedNode ? [[pickedNode.id, pickedNode]] : []),
    [pickedNode],
  );

  const matchedPicked =
    pickedNode !== null &&
    typeof machineCodeValue === 'string' &&
    pickedNode.machineCode === machineCodeValue.replace(/[\s_-]/g, '').toUpperCase()
      ? pickedNode
      : null;

  useEffect(() => {
    if (!open) {
      setPickerOpen(false);
      setPickedNode(null);
      return;
    }

    if (initial) {
      form.setFieldsValue({ productKey: initial.productKey, machineCode: initial.machineCode });
    }
  }, [open, initial, form]);

  const mutation = useMutation({
    mutationFn: (values: IssueFormValues) => {
      const body: IssueLicenseRequest = {
        productKey: values.productKey,
        machineCode: values.machineCode.trim(),
        nodeId: matchedPicked?.id ?? null,
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
      message.success(tr("已签发 {{arg0}} · {{arg1}}", { arg0: license.productKey, arg1: license.machineCode }));
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
      title={tr("签发授权")}
      okText={tr("签发")}
      cancelText={tr("取消")}
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
          label={tr("产品")}
          rules={[{ required: true, message: tr("请选择产品") }]}
        >
          <Select
            placeholder={tr("选择授权产品")}
            options={products
              .filter((product) => product.isEnabled)
              .map((product) => ({ value: product.key, label: `${product.name}（${product.key}）` }))}
          />
        </Form.Item>

        <Form.Item
          name="machineCode"
          label={tr("机器码")}
          tooltip={tr("客户端「关于/授权」页展示的 16 字符机器码；手抄的 O/I/L 会被自动纠正。")}
          extra={
            matchedPicked
              ? tr("已匹配节点：{{arg0}}（{{arg1}}）", {
                  arg0: matchedPicked.displayName,
                  arg1: matchedPicked.ipAddress ?? '-',
                })
              : undefined
          }
          rules={[
            { required: true, message: tr("请输入机器码") },
            {
              pattern: /^[0-9A-Za-z\-_\s]{16,24}$/,
              message: tr("机器码应为 16 位 Crockford Base32（可带分隔符）"),
            },
          ]}
        >
          <Input
            placeholder={tr("例如 008J-4CT4 ANK7-F24S")}
            autoComplete="off"
            addonAfter={
              canPickNode ? (
                <Button
                  type="text"
                  size="small"
                  icon={<UnorderedListOutlined />}
                  onClick={() => setPickerOpen(true)}
                >
                  {tr("选择节点")}
                </Button>
              ) : undefined
            }
          />
        </Form.Item>

        <Form.Item name="validity" label={tr("有效期")}>
          <Radio.Group
            optionType="button"
            buttonStyle="solid"
            options={[
              { value: 'days', label: tr("按天数") },
              { value: 'date', label: tr("指定日期") },
            ]}
          />
        </Form.Item>

        <Form.Item noStyle shouldUpdate={(prev, next) => prev.validity !== next.validity}>
          {({ getFieldValue }) =>
            getFieldValue('validity') === 'days' ? (
              <Form.Item
                name="addDays"
                label={tr("有效天数")}
                rules={[{ required: true, message: tr("请输入有效天数") }]}
              >
                <InputNumber min={1} max={3650} style={{ width: '100%' }} />
              </Form.Item>
            ) : (
              <Form.Item
                name="notAfter"
                label={tr("到期日")}
                rules={[{ required: true, message: tr("请选择到期日") }]}
              >
                <DatePicker style={{ width: '100%' }} disabledDate={(date) => date.isBefore(dayjs(), 'day')} />
              </Form.Item>
            )
          }
        </Form.Item>

        <Form.Item
          name="graceDays"
          label={tr("宽限天数")}
          tooltip={tr("留空取产品默认值；宽限写入本条授权，状态校验只看它，之后改产品默认值不影响已签发授权。")}
        >
          <InputNumber min={0} max={30} style={{ width: '100%' }} placeholder={tr("产品默认")} />
        </Form.Item>

        <Form.Item
          name="licenseNo"
          label={tr("授权编号")}
          tooltip={tr("留空自动生成；建议与销售合同的编号一致，便于现场定位。")}
          rules={[{ max: 32 }]}
        >
          <Input placeholder={tr("例如 LIC-20261030-001")} autoComplete="off" />
        </Form.Item>

        <Form.Item name="notes" label={tr("备注")} rules={[{ max: 512 }]}>
          <Input.TextArea rows={2} placeholder={tr("可选，仅管理端可见")} />
        </Form.Item>
      </Form>

      <NodePickerModal
        open={pickerOpen}
        selected={pickerSelection}
        onConfirm={(next) => {
          const node = [...next.values()][0];
          if (node) {
            setPickedNode(node);
            form.setFieldValue('machineCode', node.machineCode);
          }
          setPickerOpen(false);
        }}
        onCancel={() => setPickerOpen(false)}
      />
    </Modal>
  );
}
