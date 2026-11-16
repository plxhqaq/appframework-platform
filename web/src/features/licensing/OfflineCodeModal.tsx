import { formatDate } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { CopyOutlined } from '@ant-design/icons';
import { useMutation } from '@tanstack/react-query';
import { App, Button, DatePicker, Form, Input, InputNumber, Modal, Radio, Select, Space, Typography } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useState } from 'react';
import { licensingApi } from '../../shared/api/licensing';
import type {
  IssueOfflineLicenseCodeRequest,
  LicenseProduct,
  OfflineLicenseCode,
} from '../../shared/api/types';

interface OfflineFormValues {
  productKey: string;
  machineCode: string;
  validity: 'days' | 'date';
  addDays?: number;
  notAfter?: Dayjs;
  licenseNo?: string;
  remark?: string;
}

interface OfflineCodeModalProps {
  open: boolean;
  products: LicenseProduct[];
  onClose: () => void;
  onIssued: () => void;
}

/**
 * 签发离线授权码（无联网现场）。码与机器码、产品键绑定，复制到别的机器无效；
 * 台账删除不影响已发出的码（离线码没有吊销通道，到期自然失效）。
 */
export function OfflineCodeModal({ open, products, onClose, onIssued }: OfflineCodeModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<OfflineFormValues>();
  const [issued, setIssued] = useState<OfflineLicenseCode | null>(null);

  const mutation = useMutation({
    mutationFn: (values: OfflineFormValues) => {
      const body: IssueOfflineLicenseCodeRequest = {
        productKey: values.productKey,
        machineCode: values.machineCode.trim(),
        addDays: values.validity === 'days' ? (values.addDays ?? null) : null,
        notAfter:
          values.validity === 'date' && values.notAfter
            ? values.notAfter.endOf('day').toISOString()
            : null,
        licenseNo: values.licenseNo?.trim() || null,
        remark: values.remark ?? null,
      };
      return licensingApi.issueOfflineCode(body);
    },
    onSuccess: (code) => {
      setIssued(code);
      onIssued();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleOk = async () => {
    if (issued) {
      onClose();
      return;
    }

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

  const copy = async () => {
    if (!issued) {
      return;
    }

    try {
      await navigator.clipboard.writeText(issued.code);
      message.success(tr("授权码已复制（不带分隔符，粘贴即可）"));
    } catch {
      message.error(tr("复制失败，请手动选择文本复制"));
    }
  };

  return (
    <Modal
      open={open}
      title={issued ? tr("离线授权码已签发") : tr("签发离线授权码")}
      okText={issued ? tr("完成") : tr("签发")}
      cancelText={issued ? undefined : tr("取消")}
      cancelButtonProps={{ style: issued ? { display: 'none' } : undefined }}
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
        setIssued(null);
      }}
    >
      {issued ? (
        <Space direction="vertical" size={12} style={{ display: 'flex' }}>
          <Typography.Text type="secondary">
            {issued.productKey} · {issued.machineCode} {tr("· 到期")}{formatDate(issued.notAfter)}
          </Typography.Text>
          <Typography.Paragraph
            copyable={{ text: issued.code }}
            code
            style={{ margin: 0, wordBreak: 'break-all', whiteSpace: 'pre-wrap' }}
          >
            {issued.formattedCode}
          </Typography.Paragraph>
          <Space>
            <Button icon={<CopyOutlined />} onClick={() => void copy()}>
              {tr("复制授权码")}</Button>
            <Typography.Text type="secondary">
              {tr("请把整段码交给现场，在客户端「授权」页录入并激活。")}</Typography.Text>
          </Space>
        </Space>
      ) : (
        <Form<OfflineFormValues>
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
            rules={[
              { required: true, message: tr("请输入机器码") },
              {
                pattern: /^[0-9A-Za-z\-_\s]{16,24}$/,
                message: tr("机器码应为 16 位 Crockford Base32（可带分隔符）"),
              },
            ]}
          >
            <Input placeholder={tr("客户端「授权」页展示的 16 位机器码")} autoComplete="off" />
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
                  <DatePicker
                    style={{ width: '100%' }}
                    disabledDate={(date) => date.isBefore(dayjs(), 'day')}
                  />
                </Form.Item>
              )
            }
          </Form.Item>

          <Form.Item
            name="licenseNo"
            label={tr("授权编号")}
            tooltip={tr("留空自动生成；编号会写进授权码，客户端激活后可看到。")}
            rules={[{ max: 32 }]}
          >
            <Input placeholder={tr("例如 LIC-20261030-001")} autoComplete="off" />
          </Form.Item>

          <Form.Item name="remark" label={tr("备注")} rules={[{ max: 512 }]}>
            <Input.TextArea rows={2} placeholder={tr("可选，例如 现场无网/客户名称")} />
          </Form.Item>
        </Form>
      )}
    </Modal>
  );
}
