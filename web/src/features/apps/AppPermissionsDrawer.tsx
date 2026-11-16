import { tr } from '../../i18n/translate';
import { LockOutlined, UnlockOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Drawer, Popconfirm, Space, Table, Tag, Tooltip, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { PERMISSIONS } from '../../shared/permissions';
import { appsApi } from '../../shared/api/apps';
import type { AppDetail, AppPermission } from '../../shared/api/types';
import { responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';

interface AppPermissionsDrawerProps {
  open: boolean;
  app: AppDetail | null;
  onClose: () => void;
}

/**
 * 应用权限清单查看与解锁。
 *
 * 清单由客户端应用自助上传，首次成功后自动锁定；只有管理端在这里人工解锁后，
 * 应用才能再次上传，避免权限完全不同的应用误连后覆盖既有清单。
 */
export function AppPermissionsDrawer({ open, app, onClose }: AppPermissionsDrawerProps) {
  const { message } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.appsPermissionsManage));

  const manifest = useQuery({
    queryKey: ['apps', app?.id, 'permissions'],
    queryFn: ({ signal }) => appsApi.getPermissions(app!.key, signal),
    enabled: open && app !== null,
  });

  const lock = useMutation({
    mutationFn: () => appsApi.lockPermissions(app!.id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['apps', app?.id, 'permissions'] });
      message.success(tr("权限清单已锁定，应用再次上传会被拒绝"));
    },
    onError: (error: Error) => message.error(error.message),
  });

  const unlock = useMutation({
    mutationFn: () => appsApi.unlockPermissions(app!.id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['apps', app?.id, 'permissions'] });
      message.success(tr("权限清单已解锁，应用可以再次上传"));
    },
    onError: (error: Error) => message.error(error.message),
  });

  const columns: TableColumnsType<AppPermission> = [
    {
      title: tr("权限码"),
      dataIndex: 'code',
      key: 'code',
      width: 220,
      render: (code: string) => (
        <Typography.Text code style={{ whiteSpace: 'nowrap' }}>
          {code}
        </Typography.Text>
      ),
    },
    { title: tr("显示名"), dataIndex: 'displayName', key: 'displayName', width: 140 },
    {
      title: tr("说明"),
      dataIndex: 'description',
      key: 'description',
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
  ];

  const presets = manifest.data?.presets ?? [];

  return (
    <Drawer
      open={open}
      width={responsiveWidth(isMobile, 720)}
      title={app ? tr("权限清单 · {{arg0}}", { arg0: app.key }) : tr("权限清单")}
      onClose={onClose}
      styles={{ body: { padding: isMobile ? 12 : 24 } }}
      destroyOnClose
    >
      {manifest.isError ? (
        <Alert
          type="error"
          showIcon
          message={(manifest.error as Error).message}
          action={
            <Button size="small" onClick={() => void manifest.refetch()}>
              {tr("重试")}</Button>
          }
        />
      ) : null}

      {manifest.data?.locked ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 16 }}
          message={tr("清单已锁定")}
          description={tr("应用不能再次上传覆盖。确认要更新权限时先解锁；下一次上传成功后会自动重新锁定。")}
          action={
            canManage ? (
            <Popconfirm
              title={tr("解锁权限清单？")}
              description={tr("解锁后，应用下一次上传会整表替换现有权限。")}
              okText={tr("解锁")}
              cancelText={tr("取消")}
              onConfirm={() => unlock.mutateAsync()}
            >
              <Button size="small" icon={<UnlockOutlined />} loading={unlock.isPending}>
                {tr("解锁")}</Button>
            </Popconfirm>
            ) : undefined
          }
        />
      ) : manifest.isSuccess ? (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 16 }}
          icon={<LockOutlined />}
          message={tr("清单未锁定")}
          description={tr("应用下一次上传成功后会自动锁定；也可以在这里手动锁定。")}
          action={
            canManage ? (
              <Popconfirm
                title={tr("立即锁定权限清单？")}
                description={tr("锁定后应用再次上传会被拒绝，需人工解锁。")}
                okText={tr("锁定")}
                cancelText={tr("取消")}
                onConfirm={() => lock.mutateAsync()}
              >
                <Button size="small" icon={<LockOutlined />} loading={lock.isPending}>
                  {tr("锁定")}</Button>
              </Popconfirm>
            ) : undefined
          }
        />
      ) : null}

      {presets.length > 0 ? (
        <div style={{ marginBottom: 16 }}>
          <Typography.Text type="secondary">{tr("常用组合：")}</Typography.Text>
          <div style={{ marginTop: 4 }}>
            <Space size={4} wrap>
              {presets.map((preset) => (
                <Tooltip key={preset.key} title={preset.description ?? preset.permissions.join('、')}>
                  <Tag>{preset.label}</Tag>
                </Tooltip>
              ))}
            </Space>
          </div>
        </div>
      ) : null}

      <Table<AppPermission>
        rowKey="code"
        size="small"
        scroll={{ x: 'max-content' }}
        loading={manifest.isPending}
        dataSource={manifest.data?.permissions ?? []}
        columns={columns}
        pagination={false}
        locale={{ emptyText: tr("应用尚未上传权限清单") }}
      />
    </Drawer>
  );
}
