import { formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { PlusOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Card, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { appsApi } from '../../shared/api/apps';
import type { AppDetail, AppSummary } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { ApiKeysDrawer } from './ApiKeysDrawer';
import { AppFormModal } from './AppFormModal';
import { AppPermissionsDrawer } from './AppPermissionsDrawer';

/**
 * 应用接入页。
 *
 * 应用键是全部业务数据的隔离轴，所以这里只有"注册/启用"和"发密钥"两类动作；
 * 删除刻意不提供——停用即可，历史审计与业务数据必须保留。
 */
export function AppsPage() {
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.appsManage));
  const canManageKeys = useAuthStore((state) => state.hasPermission(PERMISSIONS.appsApiKeyManage));

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<AppDetail | null>(null);
  const [keysApp, setKeysApp] = useState<AppDetail | null>(null);
  const [permissionsApp, setPermissionsApp] = useState<AppDetail | null>(null);

  const apps = useQuery({
    queryKey: ['apps', 'list'],
    queryFn: ({ signal }) => appsApi.list(signal),
  });

  const openCreate = () => {
    setEditing(null);
    setFormOpen(true);
  };

  const openEdit = (app: AppSummary) => {
    setEditing({ ...app, settingsJson: null });
    setFormOpen(true);
  };

  const openKeys = (app: AppSummary) => {
    setKeysApp({ ...app, settingsJson: null });
  };

  const openPermissions = (app: AppSummary) => {
    setPermissionsApp({ ...app, settingsJson: null });
  };

  const columns: TableColumnsType<AppSummary> = [
    {
      title: tr("应用键"),
      dataIndex: 'key',
      key: 'key',
      width: 200,
      render: (key: string) => (
        <Typography.Text code style={{ whiteSpace: 'nowrap' }}>
          {key}
        </Typography.Text>
      ),
    },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 200 },
    {
      title: tr("说明"),
      dataIndex: 'description',
      key: 'description',
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: tr("状态"),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 90,
      render: (enabled: boolean) => (enabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>),
    },
    {
      title: tr("创建时间"),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 200,
      fixed: 'right',
      render: (_: unknown, app: AppSummary) => (
        <Space size="small">
          {canManage ? (
            <Button type="link" size="small" onClick={() => openEdit(app)}>
              {tr("编辑")}</Button>
          ) : null}
          {canManage ? (
            <Button type="link" size="small" onClick={() => openPermissions(app)}>
              {tr("权限")}</Button>
          ) : null}
          {canManageKeys ? (
            <Button type="link" size="small" onClick={() => openKeys(app)}>
              {tr("密钥")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Card
      title={tr("应用接入")}
      size="small"
      extra={
        canManage ? (
          <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            {tr("新建应用")}</Button>
        ) : null
      }
    >
      {apps.isError ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={(apps.error as Error).message}
          action={
            <Button size="small" onClick={() => void apps.refetch()}>
              {tr("重试")}</Button>
          }
        />
      ) : null}

      <Table<AppSummary>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={apps.isPending}
        dataSource={apps.data ?? []}
        columns={columns}
        pagination={false}
        locale={{ emptyText: tr("尚未注册应用") }}
      />

      <AppFormModal open={formOpen} app={editing} onClose={() => setFormOpen(false)} />
      <ApiKeysDrawer open={keysApp !== null} app={keysApp} onClose={() => setKeysApp(null)} />
      <AppPermissionsDrawer
        open={permissionsApp !== null}
        app={permissionsApp}
        onClose={() => setPermissionsApp(null)}
      />
    </Card>
  );
}
