import {
  AppstoreOutlined,
  KeyOutlined,
  PlusOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Empty,
  Input,
  Popconfirm,
  Select,
  Space,
  Table,
  Tabs,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import { useState } from 'react';
import { licensingApi } from '../../shared/api/licensing';
import type {
  LicenseEntitlement,
  LicenseKind,
  LicenseProduct,
  LicenseSigningKey,
  LicenseSigningKeyStatus,
  OfflineLicenseCode,
} from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { IssueLicenseModal } from './IssueLicenseModal';
import { OfflineCodeModal } from './OfflineCodeModal';
import { ProductFormModal } from './ProductFormModal';
import { RenewLicenseModal } from './RenewLicenseModal';
import { RevokeLicenseModal } from './RevokeLicenseModal';

const KIND_META: Record<LicenseKind, { label: string; color?: string }> = {
  trial: { label: '试用', color: 'blue' },
  official: { label: '正式', color: 'green' },
};

const STATUS_META: Record<string, { label: string; color?: string }> = {
  active: { label: '有效', color: 'green' },
  expired: { label: '已过期', color: 'orange' },
  revoked: { label: '已吊销', color: 'red' },
  unlicensed: { label: '未授权' },
  unmanaged: { label: '未启用' },
};

function formatDate(value?: string | null): string {
  return value ? new Date(value).toLocaleDateString() : '-';
}

/**
 * 授权管理页。
 *
 * 产品、授权与离线码按应用隔离（应用上下文来自令牌）；签名密钥是全局资源：
 * 公钥客户端可匿名发现，私钥只存在部署环境变量里，这里只展示指纹与状态。
 */
export function LicensingPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const switchAppContext = useAuthStore((state) => state.switchAppContext);
  const switching = useAuthStore((state) => state.pending);
  const canManageProducts = useAuthStore((state) => state.hasPermission(PERMISSIONS.licenseProductManage));
  const canIssue = useAuthStore((state) => state.hasPermission(PERMISSIONS.licenseIssue));
  const canRevoke = useAuthStore((state) => state.hasPermission(PERMISSIONS.licenseRevoke));
  const canIssueOffline = useAuthStore((state) => state.hasPermission(PERMISSIONS.licenseOfflineCodeIssue));
  const canManageKeys = useAuthStore((state) => state.hasPermission(PERMISSIONS.licenseSigningKeyManage));

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [productFormOpen, setProductFormOpen] = useState(false);
  const [editingProduct, setEditingProduct] = useState<LicenseProduct | null>(null);
  const [issueOpen, setIssueOpen] = useState(false);
  const [renewing, setRenewing] = useState<LicenseEntitlement | null>(null);
  const [revoking, setRevoking] = useState<LicenseEntitlement | null>(null);
  const [offlineOpen, setOfflineOpen] = useState(false);
  const [productFilter, setProductFilter] = useState<string | undefined>();
  const [statusFilter, setStatusFilter] = useState<string | undefined>();
  const [keyword, setKeyword] = useState('');

  const products = useQuery({
    queryKey: ['licensing', 'products', appKey],
    queryFn: ({ signal }) => licensingApi.listProducts(signal),
    enabled: appKey !== null,
  });

  const entitlements = useQuery({
    queryKey: ['licensing', 'entitlements', appKey, productFilter, statusFilter, keyword],
    queryFn: ({ signal }) =>
      licensingApi.listEntitlements(
        {
          productKey: productFilter,
          status: statusFilter,
          keyword: keyword || undefined,
          pageSize: 100,
        },
        signal,
      ),
    enabled: appKey !== null,
  });

  const offlineCodes = useQuery({
    queryKey: ['licensing', 'offline-codes', appKey, keyword],
    queryFn: ({ signal }) =>
      licensingApi.listOfflineCodes({ keyword: keyword || undefined, pageSize: 100 }, signal),
    enabled: appKey !== null,
  });

  const signingKeys = useQuery({
    queryKey: ['licensing', 'signing-keys'],
    queryFn: ({ signal }) => licensingApi.getSigningKeys(signal),
    enabled: canManageKeys,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['licensing'] });
  };

  const handleSwitchContext = async (next: string) => {
    if (next === appKey) {
      return;
    }

    try {
      const updated = await switchAppContext(next);
      message.success(`已进入应用 ${updated.appKey ?? next}`);
      setProductFilter(undefined);
      setStatusFilter(undefined);
      setKeyword('');
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '切换应用失败');
    }
  };

  const mutateKey = async (action: 'sync' | 'activate' | 'retire', row?: LicenseSigningKey) => {
    try {
      if (action === 'sync') {
        await licensingApi.syncSigningKeys();
        message.success('密钥登记表已同步');
      } else if (row) {
        if (action === 'activate') {
          await licensingApi.activateSigningKey(row.id);
          message.success(`已激活 ${row.keyId}`);
        } else {
          await licensingApi.retireSigningKey(row.id);
          message.success(`已退役 ${row.keyId}（旧快照仍可验签）`);
        }
      }
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '操作失败');
    }
  };

  const deleteOfflineCode = async (row: OfflineLicenseCode) => {
    try {
      await licensingApi.deleteOfflineCode(row.id);
      message.success('台账已删除（已发出的码到期前仍可用）');
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '删除失败');
    }
  };

  const productColumns = [
    {
      title: '产品键',
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (key: string) => <Typography.Text code>{key}</Typography.Text>,
    },
    { title: '名称', dataIndex: 'name', key: 'name', width: 160 },
    {
      title: '席位',
      dataIndex: 'seatLimit',
      key: 'seatLimit',
      width: 90,
      render: (value: number | null) => value ?? '不限',
    },
    {
      title: '有效/总授权',
      key: 'counts',
      width: 120,
      render: (_: unknown, row: LicenseProduct) => `${row.activeEntitlementCount} / ${row.entitlementCount}`,
    },
    { title: '宽限', dataIndex: 'defaultGraceDays', key: 'defaultGraceDays', width: 70 },
    {
      title: '试用',
      dataIndex: 'trialDays',
      key: 'trialDays',
      width: 90,
      render: (value: number) => (value > 0 ? `${value} 天` : '关闭'),
    },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 100,
      render: (_: unknown, row: LicenseProduct) =>
        canManageProducts ? (
          <Button
            type="link"
            size="small"
            onClick={() => {
              setEditingProduct(row);
              setProductFormOpen(true);
            }}
          >
            编辑
          </Button>
        ) : null,
    },
  ];

  const entitlementColumns = [
    { title: '产品', dataIndex: 'productKey', key: 'productKey', width: 130 },
    {
      title: '机器码',
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 180,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: '类型',
      dataIndex: 'kind',
      key: 'kind',
      width: 80,
      render: (kind: LicenseKind) => {
        const meta = KIND_META[kind];
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 90,
      render: (status: string) => {
        const meta = STATUS_META[status] ?? { label: status };
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: '到期',
      dataIndex: 'notAfter',
      key: 'notAfter',
      width: 110,
      render: (value: string) => formatDate(value),
    },
    {
      title: '授权编号',
      dataIndex: 'licenseNo',
      key: 'licenseNo',
      width: 170,
      render: (value: string | null) => value ?? '-',
    },
    {
      title: '最后校验',
      dataIndex: 'lastSeenAt',
      key: 'lastSeenAt',
      width: 150,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '操作',
      key: 'actions',
      width: 150,
      render: (_: unknown, row: LicenseEntitlement) => (
        <Space size="small">
          {canIssue && row.status !== 'revoked' ? (
            <Button type="link" size="small" onClick={() => setRenewing(row)}>
              续期
            </Button>
          ) : null}
          {canRevoke && row.status !== 'revoked' ? (
            <Button type="link" size="small" danger onClick={() => setRevoking(row)}>
              吊销
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  const offlineColumns = [
    { title: '产品', dataIndex: 'productKey', key: 'productKey', width: 130 },
    {
      title: '机器码',
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 170,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    { title: '授权编号', dataIndex: 'licenseNo', key: 'licenseNo', width: 170 },
    {
      title: '授权码',
      dataIndex: 'formattedCode',
      key: 'formattedCode',
      render: (value: string, row: OfflineLicenseCode) => (
        <Typography.Text copyable={{ text: row.code }} ellipsis style={{ maxWidth: 260 }}>
          {value}
        </Typography.Text>
      ),
    },
    {
      title: '到期',
      dataIndex: 'notAfter',
      key: 'notAfter',
      width: 110,
      render: (value: string) => formatDate(value),
    },
    {
      title: '签发时间',
      dataIndex: 'issuedAt',
      key: 'issuedAt',
      width: 150,
      render: (value: string) => new Date(value).toLocaleString(),
    },
    { title: '备注', dataIndex: 'remark', key: 'remark', width: 150, render: (value: string | null) => value ?? '-' },
    {
      title: '操作',
      key: 'actions',
      width: 100,
      render: (_: unknown, row: OfflineLicenseCode) =>
        canIssueOffline ? (
          <Popconfirm
            title="删除台账行？"
            description="已发出的码到期前仍然可用，删除只影响这里的记录。"
            okText="删除"
            cancelText="取消"
            onConfirm={() => void deleteOfflineCode(row)}
          >
            <Button type="link" size="small" danger>
              删除
            </Button>
          </Popconfirm>
        ) : null,
    },
  ];

  const signingKeyColumns = [
    {
      title: '密钥 ID',
      dataIndex: 'keyId',
      key: 'keyId',
      width: 170,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    { title: '算法', dataIndex: 'algorithm', key: 'algorithm', width: 160 },
    {
      title: '指纹',
      dataIndex: 'fingerprint',
      key: 'fingerprint',
      width: 260,
      render: (value: string) => <Typography.Text copyable ellipsis style={{ maxWidth: 240 }}>{value}</Typography.Text>,
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (status: string, row: LicenseSigningKey) =>
        status === 'active' ? (
          <Tag color="green">{row.isCurrent ? '当前签发' : '启用'}</Tag>
        ) : (
          <Tag>已退役</Tag>
        ),
    },
    {
      title: '启用时间',
      dataIndex: 'activatedAt',
      key: 'activatedAt',
      width: 150,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '退役时间',
      dataIndex: 'retiredAt',
      key: 'retiredAt',
      width: 150,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '操作',
      key: 'actions',
      width: 140,
      render: (_: unknown, row: LicenseSigningKey) => (
        <Space size="small">
          {canManageKeys && row.status === 'retired' ? (
            <Button type="link" size="small" onClick={() => void mutateKey('activate', row)}>
              激活
            </Button>
          ) : null}
          {canManageKeys && row.status === 'active' && !row.isCurrent ? (
            <Button type="link" size="small" danger onClick={() => void mutateKey('retire', row)}>
              退役
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title="授权" size="small">
        <Alert
          type="warning"
          showIcon
          message="当前账号没有可用的应用上下文"
          description="授权产品、授权主体与离线码按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。"
        />
      </Card>
    );
  }

  const signingStatus = signingKeys.data as LicenseSigningKeyStatus | undefined;

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card size="small">
        <Space wrap>
          <AppstoreOutlined />
          <Typography.Text>当前应用</Typography.Text>
          <Select
            value={appKey}
            loading={switching}
            style={{ minWidth: 200 }}
            options={appKeys.map((key) => ({ value: key, label: key }))}
            onChange={(value) => void handleSwitchContext(value)}
            disabled={switching || appKeys.length <= 1}
          />
          <Tooltip title="应用上下文来自令牌；切换后授权产品、授权与离线码的数据范围随之切换。">
            <Typography.Text type="secondary">授权数据按应用隔离，前端不传 appKey。</Typography.Text>
          </Tooltip>
        </Space>
      </Card>

      <Tabs
        items={[
          {
            key: 'products',
            label: '授权产品',
            children: (
              <Card
                size="small"
                title="产品"
                extra={
                  canManageProducts ? (
                    <Button
                      type="primary"
                      icon={<PlusOutlined />}
                      onClick={() => {
                        setEditingProduct(null);
                        setProductFormOpen(true);
                      }}
                    >
                      新建产品
                    </Button>
                  ) : null
                }
              >
                {products.isError ? (
                  <Alert type="error" showIcon style={{ marginBottom: 16 }} message={(products.error as Error).message} />
                ) : null}
                <Table<LicenseProduct>
                  rowKey="id"
                  size="small"
                  loading={products.isPending}
                  dataSource={products.data ?? []}
                  columns={productColumns}
                  pagination={false}
                  locale={{ emptyText: '尚未创建授权产品（如 erp-pro）' }}
                />
              </Card>
            ),
          },
          {
            key: 'entitlements',
            label: '授权主体',
            children: (
              <Card
                size="small"
                title="授权"
                extra={
                  canIssue ? (
                    <Button type="primary" icon={<PlusOutlined />} onClick={() => setIssueOpen(true)}>
                      签发授权
                    </Button>
                  ) : null
                }
              >
                <Space wrap style={{ marginBottom: 12 }}>
                  <Select
                    allowClear
                    placeholder="全部产品"
                    style={{ width: 200 }}
                    value={productFilter}
                    onChange={setProductFilter}
                    options={(products.data ?? []).map((product) => ({
                      value: product.key,
                      label: `${product.name}（${product.key}）`,
                    }))}
                  />
                  <Select
                    allowClear
                    placeholder="全部状态"
                    style={{ width: 150 }}
                    value={statusFilter}
                    onChange={setStatusFilter}
                    options={[
                      { value: 'active', label: '有效' },
                      { value: 'expired', label: '已过期' },
                      { value: 'revoked', label: '已吊销' },
                    ]}
                  />
                  <Input.Search
                    allowClear
                    placeholder="机器码 / 授权编号"
                    style={{ width: 240 }}
                    onSearch={setKeyword}
                  />
                  <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
                    刷新
                  </Button>
                </Space>

                {entitlements.isError ? (
                  <Alert
                    type="error"
                    showIcon
                    style={{ marginBottom: 16 }}
                    message={(entitlements.error as Error).message}
                  />
                ) : null}
                <Table<LicenseEntitlement>
                  rowKey="id"
                  size="small"
                  loading={entitlements.isPending}
                  dataSource={entitlements.data?.items ?? []}
                  columns={entitlementColumns}
                  pagination={false}
                  locale={{ emptyText: <Empty description="尚无授权；签发后客户端校验状态即可拿到签名快照" /> }}
                />
              </Card>
            ),
          },
          {
            key: 'offline',
            label: '离线授权码',
            children: (
              <Card
                size="small"
                title="离线码台账"
                extra={
                  canIssueOffline ? (
                    <Button type="primary" icon={<PlusOutlined />} onClick={() => setOfflineOpen(true)}>
                      签发离线码
                    </Button>
                  ) : null
                }
              >
                <Space wrap style={{ marginBottom: 12 }}>
                  <Input.Search
                    allowClear
                    placeholder="机器码 / 授权编号"
                    style={{ width: 260 }}
                    onSearch={setKeyword}
                  />
                  <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
                    刷新
                  </Button>
                  <Typography.Text type="secondary">
                    离线码绑定机器码与产品键；发出去以后无法吊销，请在台账里保留记录。
                  </Typography.Text>
                </Space>

                {offlineCodes.isError ? (
                  <Alert
                    type="error"
                    showIcon
                    style={{ marginBottom: 16 }}
                    message={(offlineCodes.error as Error).message}
                  />
                ) : null}
                <Table<OfflineLicenseCode>
                  rowKey="id"
                  size="small"
                  loading={offlineCodes.isPending}
                  dataSource={offlineCodes.data?.items ?? []}
                  columns={offlineColumns}
                  pagination={false}
                  locale={{ emptyText: '尚未签发离线授权码' }}
                />
              </Card>
            ),
          },
          ...(canManageKeys
            ? [
                {
                  key: 'keys',
                  label: (
                    <Space size={6}>
                      <SafetyCertificateOutlined />
                      签名密钥
                    </Space>
                  ),
                  children: (
                    <Card
                      size="small"
                      title="签名密钥"
                      extra={
                        <Space>
                          <Button icon={<ReloadOutlined />} onClick={() => void mutateKey('sync')}>
                            同步配置
                          </Button>
                        </Space>
                      }
                    >
                      {signingKeys.isError ? (
                        <Alert
                          type="error"
                          showIcon
                          style={{ marginBottom: 16 }}
                          message={(signingKeys.error as Error).message}
                        />
                      ) : null}

                      {signingStatus ? (
                        <Alert
                          type={signingStatus.isManaged ? 'success' : 'warning'}
                          showIcon
                          style={{ marginBottom: 16 }}
                          message={
                            signingStatus.isManaged
                              ? `当前签发密钥：${signingStatus.currentKeyId ?? '-'}`
                              : '部署未配置签发私钥（Licensing__SigningKeyPkcs8），授权校验不生效'
                          }
                          description={
                            signingStatus.isManaged ? (
                              <Space direction="vertical" size={2}>
                                <Typography.Text type="secondary">
                                  公钥指纹：{signingStatus.fingerprint}
                                </Typography.Text>
                                <Typography.Text type="secondary">
                                  退役密钥继续参与验签；私钥只存在于部署环境变量，管理台永远拿不到。
                                </Typography.Text>
                              </Space>
                            ) : (
                              <Typography.Text type="secondary">
                                未配置私钥时客户端一律按 unmanaged 处理，也不会签发离线码。
                              </Typography.Text>
                            )
                          }
                        />
                      ) : null}

                      <Descriptions size="small" column={1} style={{ marginBottom: 12 }}>
                        <Descriptions.Item label="轮换做法">
                          部署新私钥 → 重启服务 → 点「同步配置」把新密钥登记为当前；
                          旧密钥自动转为退役状态，客户端已拿到的旧快照仍能验签。
                        </Descriptions.Item>
                      </Descriptions>

                      <Table<LicenseSigningKey>
                        rowKey="id"
                        size="small"
                        loading={signingKeys.isPending}
                        dataSource={signingStatus?.keys ?? []}
                        columns={signingKeyColumns}
                        pagination={false}
                        locale={{ emptyText: '尚未登记签名密钥' }}
                      />
                    </Card>
                  ),
                },
              ]
            : []),
        ]}
      />

      <ProductFormModal
        open={productFormOpen}
        product={editingProduct}
        onClose={() => setProductFormOpen(false)}
        onSaved={() => void refresh()}
      />

      <IssueLicenseModal
        open={issueOpen}
        products={products.data ?? []}
        onClose={() => setIssueOpen(false)}
        onSaved={() => void refresh()}
      />

      <RenewLicenseModal
        open={renewing !== null}
        entitlement={renewing}
        onClose={() => setRenewing(null)}
        onSaved={() => void refresh()}
      />

      <RevokeLicenseModal
        open={revoking !== null}
        entitlement={revoking}
        onClose={() => setRevoking(null)}
        onSaved={() => void refresh()}
      />

      <OfflineCodeModal
        open={offlineOpen}
        products={products.data ?? []}
        onClose={() => setOfflineOpen(false)}
        onIssued={() => void refresh()}
      />

      {canManageKeys ? null : (
        <Card size="small">
          <Space>
            <KeyOutlined />
            <Typography.Text type="secondary">
              签名密钥由平台管理员维护；当前账号只能查看授权数据。
            </Typography.Text>
          </Space>
        </Card>
      )}
    </Space>
  );
}
