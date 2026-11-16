import { HomeOutlined } from '@ant-design/icons';
import { Button, Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { HOME_PATH } from './navigation';

/**
 * 权限不够时的落点。
 *
 * 不做重定向：直接跳回总览会让人以为「链接坏了」，而手输地址与扫码直达
 * 都需要一句明确的解释——是权限不够，不是页面不存在。
 */
export function AccessDenied({ description }: { description?: string }) {
  const { t } = useTranslation('auth');
  const navigate = useNavigate();

  return (
    <Result
      status="403"
      title={t('denied.title')}
      subTitle={
        description ?? t('denied.description')
      }
      extra={
        <Button
          type="primary"
          icon={<HomeOutlined />}
          onClick={() => navigate(HOME_PATH, { replace: true })}
        >
          {t('denied.backHome')}
        </Button>
      }
    />
  );
}
