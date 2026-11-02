import { Alert, Modal, Typography } from 'antd';

interface SecretOnceModalProps {
  open: boolean;
  /** 明文密钥；为空表示关闭状态，不渲染内容。 */
  secret: string;
  /** 展示上下文：新建或轮换，以及对应的接收地址。 */
  caption: string;
  onClose: () => void;
}

/**
 * 一次性密钥展示。
 *
 * 服务端只存明文密钥用于签名，但读取接口永远只返回提示（前 14 字符 + 省略号），
 * 所以这个弹窗是接入方拿到完整密钥的唯一时机——关掉后只能靠轮换重新获取。
 */
export function SecretOnceModal({ open, secret, caption, onClose }: SecretOnceModalProps) {
  return (
    <Modal
      open={open}
      title="签名密钥（只显示一次）"
      okText="我已保存"
      cancelText="稍后处理"
      onCancel={onClose}
      onOk={onClose}
      okButtonProps={{ disabled: secret.length === 0 }}
    >
      <Alert
        type="warning"
        showIcon
        style={{ marginBottom: 12 }}
        message="关闭后无法再次查看"
        description={
          <span>
            列表里只会保留 <Typography.Text code>secretHint</Typography.Text>（前 14 个字符）。
            请立即写入接收方的配置或密钥系统；遗失只能轮换出新密钥。
          </span>
        }
      />
      <Typography.Paragraph type="secondary" style={{ marginBottom: 8 }}>
        {caption}
      </Typography.Paragraph>
      <Typography.Paragraph copyable={{ text: secret }} style={{ marginBottom: 8 }}>
        <Typography.Text code>{secret}</Typography.Text>
      </Typography.Paragraph>
      <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
        接收方按{' '}
        <Typography.Text code>{'sha256=hex(HMAC-SHA256(secret, "{timestamp}.{rawBody}"))'}</Typography.Text>{' '}
        校验 <Typography.Text code>X-Platform-Signature</Typography.Text>；
        签名用的是原始请求体字节，先解析 JSON 再重新序列化会校验不过。
      </Typography.Paragraph>
    </Modal>
  );
}
