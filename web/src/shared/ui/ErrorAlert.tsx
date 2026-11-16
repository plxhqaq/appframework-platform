import { Alert, Button } from 'antd';
import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';

interface ErrorAlertProps {
  error: Error;
  /** 列表/详情查询失败时传入 refetch；没有可重试动作的写操作错误不要传。 */
  onRetry?: () => void;
  style?: CSSProperties;
}

/** 统一的错误横幅：消息来自服务端，附带重试按钮，弱网下不必刷新整页。 */
export function ErrorAlert({ error, onRetry, style }: ErrorAlertProps) {
  const { t } = useTranslation('common');
  return (
    <Alert
      type="error"
      showIcon
      style={{ marginBottom: 12, ...style }}
      message={error.message}
      action={
        onRetry ? (
          <Button size="small" onClick={onRetry}>
            {t('actions.retry')}
          </Button>
        ) : undefined
      }
    />
  );
}
