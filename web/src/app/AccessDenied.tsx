import { Button, Result } from 'antd';
import { useNavigate } from 'react-router-dom';
import { HOME_PATH } from './navigation';

/**
 * 权限不够时的落点。
 *
 * 不做重定向：直接跳回总览会让人以为「链接坏了」，而手输地址与扫码直达
 * 都需要一句明确的解释——是权限不够，不是页面不存在。
 */
export function AccessDenied({ description }: { description?: string }) {
  const navigate = useNavigate();

  return (
    <Result
      status="403"
      title="没有访问该页面的权限"
      subTitle={
        description ??
        '当前账号的权限里没有这个页面需要的权限码。需要的话请让管理员在「身份与权限 → 角色」里补上。'
      }
      extra={
        <Button type="primary" onClick={() => navigate(HOME_PATH, { replace: true })}>
          返回总览
        </Button>
      }
    />
  );
}
