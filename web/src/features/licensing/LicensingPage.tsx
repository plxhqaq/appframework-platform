import { formatDate as formatSharedDate, formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import {
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
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
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
import { responsivePagination } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';
import { IssueLicenseModal } from './IssueLicenseModal';
import { OfflineCodeModal } from './OfflineCodeModal';
import { ProductFormModal } from './ProductFormModal';
import { RenewLicenseModal } from './RenewLicenseModal';
import { RevokeLicenseModal } from './RevokeLicenseModal';

const KIND_META: Record<LicenseKind, { label: string; color?: string }> = {
  trial: { get label() { return tr("试用"); }, color: 'blue' },
  official: { get label() { return tr("正式"); }, color: 'green' },
};

const STATUS_META: Record<string, { label: string; color?: string }> = {
  active: { get label() { return tr("有效"); }, color: 'green' },
  expired: { get label() { return tr("已过期"); }, color: 'orange' },
  revoked: { get label() { return tr("已吊销"); }, color: 'red' },
  unlicensed: { get label() { return tr("未授权"); } },
  unmanaged: { get label() { return tr("未启用"); } },
};

function formatDate(value?: string | null): string {
  return formatSharedDate(value);
}

/**
 * 授权管理页。
 *
 * 产品、授权与离线码按应用隔离（应用上下文来自令牌）；签名密钥是全局资源：
 * 公钥客户端可匿名发现，私钥只存在部署环境变量里，这里只展示指纹与状态。
 */
export function LicensingPage() {
  const { message } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
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
  const [issueInitial, setIssueInitial] = useState<{ productKey: string; machineCode: string } | null>(null);
  const [renewing, setRenewing] = useState<LicenseEntitlement | null>(null);
  const [revoking, setRevoking] = useState<LicenseEntitlement | null>(null);
  const [offlineOpen, setOfflineOpen] = useState(false);
  const [productFilter, setProductFilter] = useState<string | undefined>();
  const [statusFilter, setStatusFilter] = useState<string | undefined>();
  const [keyword, setKeyword] = useState('');
  const [entPage, setEntPage] = useState(1);
  const [entPageSize, setEntPageSize] = useState(20);
  const [offPage, setOffPage] = useState(1);
  const [offPageSize, setOffPageSize] = useState(20);

  // 上下文在 Header 切换；产品/状态/关键字筛选属于上一个应用，不能沿用
  useEffect(() => {
    setProductFilter(undefined);
    setStatusFilter(undefined);
    setKeyword('');
    setEntPage(1);
    setOffPage(1);
  }, [appKey]);

  /** 关键字同时喂给「授权主体」和「离线码」两个台账，换词时两边都回第一页。 */
  const applyKeyword = (value: string) => {
    setKeyword(value);
    setEntPage(1);
    setOffPage(1);
  };

  const products = useQuery({
    queryKey: ['licensing', 'products', appKey],
    queryFn: ({ signal }) => licensingApi.listProducts(signal),
    enabled: appKey !== null,
  });

  const entitlements = useQuery({
    queryKey: [
      'licensing',
      'entitlements',
      appKey,
      productFilter,
      statusFilter,
      keyword,
      entPage,
      entPageSize,
    ],
    queryFn: ({ signal }) =>
      licensingApi.listEntitlements(
        {
          productKey: productFilter,
          status: statusFilter,
          keyword: keyword || undefined,
          page: entPage,
          pageSize: entPageSize,
        },
        signal,
      ),
    enabled: appKey !== null,
  });

  const offlineCodes = useQuery({
    queryKey: ['licensing', 'offline-codes', appKey, keyword, offPage, offPageSize],
    queryFn: ({ signal }) =>
      licensingApi.listOfflineCodes(
        { keyword: keyword || undefined, page: offPage, pageSize: offPageSize },
        signal,
      ),
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

  /** 产品保存后先把服务端结果写进当前应用的列表缓存，再回源刷新，避免列表短暂回到旧快照。 */
  const handleProductSaved = (product: LicenseProduct) => {
    if (appKey !== null) {
      queryClient.setQueryData<LicenseProduct[]>(
        ['licensing', 'products', appKey],
        (current) => {
          const rest = (current ?? []).filter((item) => item.id !== product.id);
          return [...rest, product].sort((a, b) => a.key.localeCompare(b.key));
        },
      );
    }
    void refresh();
  };

  const mutateKey = async (action: 'sync' | 'activate' | 'retire', row?: LicenseSigningKey) => {
    try {
      if (action === 'sync') {
        await licensingApi.syncSigningKeys();
        message.success(tr("密钥登记表已同步"));
      } else if (row) {
        if (action === 'activate') {
          await licensingApi.activateSigningKey(row.id);
          message.success(tr("已激活 {{arg0}}", { arg0: row.keyId }));
        } else {
          await licensingApi.retireSigningKey(row.id);
          message.success(tr("已退役 {{arg0}}（旧快照仍可验签）", { arg0: row.keyId }));
        }
      }
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("操作失败"));
    }
  };

  const deleteOfflineCode = async (row: OfflineLicenseCode) => {
    try {
      await licensingApi.deleteOfflineCode(row.id);
      message.success(tr("台账已删除（码本身在到期前仍可用，离线码无法吊销）"));
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("删除失败"));
    }
  };

  const deleteProductRow = async (row: LicenseProduct) => {
    try {
      await licensingApi.deleteProduct(row.id);
      message.success(tr("产品 {{arg0}} 已删除", { arg0: row.key }));
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("操作失败"));
    }
  };

  const productColumns: TableColumnsType<LicenseProduct> = [
    {
      title: tr("产品键"),
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (key: string) => <Typography.Text code>{key}</Typography.Text>,
    },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 160 },
    {
      title: tr("席位"),
      dataIndex: 'seatLimit',
      key: 'seatLimit',
      width: 90,
      responsive: ['md'],
      render: (value: number | null) => value ?? tr("不限"),
    },
    {
      title: tr("有效/总授权"),
      key: 'counts',
      width: 120,
      responsive: ['md'],
      render: (_: unknown, row: LicenseProduct) => `${row.activeEntitlementCount} / ${row.entitlementCount}`,
    },
    {
      title: tr("默认宽限"),
      dataIndex: 'defaultGraceDays',
      key: 'defaultGraceDays',
      width: 90,
      responsive: ['md'],
    },
    {
      title: tr("试用"),
      dataIndex: 'trialDays',
      key: 'trialDays',
      width: 90,
      responsive: ['md'],
      render: (value: number) =>
        value > 0 ? tr('{{count}} 天', { count: value }) : tr('关闭'),
    },
    {
      title: tr("状态"),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 150,
      fixed: 'right',
      render: (_: unknown, row: LicenseProduct) =>
        canManageProducts ? (
          <Space size="small">
            <Button
              type="link"
              size="small"
              onClick={() => {
                setEditingProduct(row);
                setProductFormOpen(true);
              }}
            >
              {tr("编辑")}</Button>
            <Popconfirm
              title={tr("删除授权产品 {{arg0}}？", { arg0: row.key })}
              description={tr("产品仍有授权行或离线码台账时会被拒绝，以保留签发留痕。此操作不可恢复。")}
              okText={tr("删除")}
              cancelText={tr("取消")}
              onConfirm={() => void deleteProductRow(row)}
            >
              <Button type="link" size="small" danger>
                {tr("删除")}</Button>
            </Popconfirm>
          </Space>
        ) : null,
    },
  ];

  const entitlementColumns: TableColumnsType<LicenseEntitlement> = [
    { title: tr("产品"), dataIndex: 'productKey', key: 'productKey', width: 130 },
    {
      title: tr("机器码"),
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 180,
      render: (value: string, row: LicenseEntitlement) => (
        <Space direction="vertical" size={0}>
          <Typography.Text code style={{ wordBreak: 'break-all' }}>
            {value}
          </Typography.Text>
          {row.nodeDisplayName ? (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {row.nodeDisplayName}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: tr("类型"),
      dataIndex: 'kind',
      key: 'kind',
      width: 80,
      render: (kind: LicenseKind) => {
        const meta = KIND_META[kind];
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: tr("状态"),
      dataIndex: 'status',
      key: 'status',
      width: 90,
      render: (status: string, row: LicenseEntitlement) => {
        const meta = STATUS_META[status] ?? { label: status };
        return (
          <Space size={4} wrap>
            <Tag color={meta.color}>{meta.label}</Tag>
            {status === 'revoked' && row.revokeScope === 'online' ? (
              <Tooltip title={tr("仅注销在线授权；已签发的离线授权码仍可激活")}>
                <Tag color="orange">{tr("仅在线")}</Tag>
              </Tooltip>
            ) : null}
          </Space>
        );
      },
    },
    {
      title: tr("到期"),
      dataIndex: 'notAfter',
      key: 'notAfter',
      width: 130,
      responsive: ['md'],
      render: (value: string, row: LicenseEntitlement) => (
        <Space direction="vertical" size={0}>
          <span>{formatDate(value)}</span>
          {row.graceDays > 0 ? (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {tr("含宽限至 {{arg0}}", { arg0: formatDate(row.expiresAt) })}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: tr("授权编号"),
      dataIndex: 'licenseNo',
      key: 'licenseNo',
      width: 170,
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("最后校验"),
      dataIndex: 'lastSeenAt',
      key: 'lastSeenAt',
      width: 150,
      responsive: ['md'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 150,
      fixed: 'right',
      render: (_: unknown, row: LicenseEntitlement) => (
        <Space size="small">
          {canIssue && row.status !== 'revoked' && row.kind === 'trial' ? (
            <Button
              type="link"
              size="small"
              onClick={() => {
                setIssueInitial({ productKey: row.productKey, machineCode: row.machineCode });
                setIssueOpen(true);
              }}
            >
              {tr("转正式")}
            </Button>
          ) : null}
          {canIssue && row.status !== 'revoked' && row.kind !== 'trial' ? (
            <Button type="link" size="small" onClick={() => setRenewing(row)}>
              {tr("续期")}</Button>
          ) : null}
          {canRevoke && row.status !== 'revoked' ? (
            <Button type="link" size="small" danger onClick={() => setRevoking(row)}>
              {tr("吊销")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  const offlineColumns: TableColumnsType<OfflineLicenseCode> = [
    { title: tr("产品"), dataIndex: 'productKey', key: 'productKey', width: 130 },
    {
      title: tr("机器码"),
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 170,
      render: (value: string) => (
        <Typography.Text code style={{ wordBreak: 'break-all' }}>
          {value}
        </Typography.Text>
      ),
    },
    {
      title: tr("授权编号"),
      dataIndex: 'licenseNo',
      key: 'licenseNo',
      width: 170,
      responsive: ['md'],
    },
    {
      title: tr("授权码"),
      dataIndex: 'formattedCode',
      key: 'formattedCode',
      render: (value: string, row: OfflineLicenseCode) => (
        <Typography.Text copyable={{ text: row.code }} ellipsis style={{ maxWidth: 260 }}>
          {value}
        </Typography.Text>
      ),
    },
    {
      title: tr("到期"),
      dataIndex: 'notAfter',
      key: 'notAfter',
      width: 110,
      responsive: ['md'],
      render: (value: string) => formatDate(value),
    },
    {
      title: tr("签发时间"),
      dataIndex: 'issuedAt',
      key: 'issuedAt',
      width: 150,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("备注"),
      dataIndex: 'remark',
      key: 'remark',
      width: 150,
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 100,
      fixed: 'right',
      render: (_: unknown, row: OfflineLicenseCode) =>
        canIssueOffline ? (
          <Popconfirm
            title={tr("删除台账行？")}
            description={tr("仅已过有效期（含宽限期）的记录可删；已发出的码本身无法吊销，删除只影响这里的记录。")}
            okText={tr("删除")}
            cancelText={tr("取消")}
            onConfirm={() => void deleteOfflineCode(row)}
          >
            <Button type="link" size="small" danger>
              {tr("删除")}</Button>
          </Popconfirm>
        ) : null,
    },
  ];

  const signingKeyColumns: TableColumnsType<LicenseSigningKey> = [
    {
      title: tr("密钥 ID"),
      dataIndex: 'keyId',
      key: 'keyId',
      width: 170,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    { title: tr("算法"), dataIndex: 'algorithm', key: 'algorithm', width: 160 },
    {
      title: tr("指纹"),
      dataIndex: 'fingerprint',
      key: 'fingerprint',
      width: 260,
      render: (value: string) => <Typography.Text copyable ellipsis style={{ maxWidth: 240 }}>{value}</Typography.Text>,
    },
    {
      title: tr("状态"),
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (status: string, row: LicenseSigningKey) =>
        status === 'active' ? (
          <Tag color="green">{row.isCurrent ? tr("当前签发") : tr("启用")}</Tag>
        ) : (
          <Tag>{tr("已退役")}</Tag>
        ),
    },
    {
      title: tr("启用时间"),
      dataIndex: 'activatedAt',
      key: 'activatedAt',
      width: 150,
      responsive: ['md'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
    {
      title: tr("退役时间"),
      dataIndex: 'retiredAt',
      key: 'retiredAt',
      width: 150,
      responsive: ['md'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 140,
      fixed: 'right',
      render: (_: unknown, row: LicenseSigningKey) => (
        <Space size="small">
          {canManageKeys && row.status === 'retired' ? (
            <Button type="link" size="small" onClick={() => void mutateKey('activate', row)}>
              {tr("激活")}</Button>
          ) : null}
          {canManageKeys && row.status === 'active' && !row.isCurrent ? (
            <Button type="link" size="small" danger onClick={() => void mutateKey('retire', row)}>
              {tr("退役")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title={tr("授权")} size="small">
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有可用的应用上下文")}
          description={tr("授权产品、授权主体与离线码按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。")}
        />
      </Card>
    );
  }

  const signingStatus = signingKeys.data as LicenseSigningKeyStatus | undefined;

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Tabs
        items={[
          {
            key: 'products',
            label: tr("授权产品"),
            children: (
              <Card
                size="small"
                title={
                  <Space size={6}>
                    {tr("产品")}
                    <Tag color="blue">{appKey}</Tag>
                  </Space>
                }
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
                      {tr("新建产品")}</Button>
                  ) : null
                }
              >
                {products.isError ? (
                  <Alert
                    type="error"
                    showIcon
                    style={{ marginBottom: 16 }}
                    message={(products.error as Error).message}
                    action={
                      <Button size="small" onClick={() => void products.refetch()}>
                        {tr("重试")}</Button>
                    }
                  />
                ) : null}
                <Table<LicenseProduct>
                  scroll={{ x: 'max-content' }}
                  rowKey="id"
                  size="small"
                  loading={products.isPending}
                  dataSource={products.data ?? []}
                  columns={productColumns}
                  pagination={false}
                  locale={{ emptyText: tr("尚未创建授权产品（如 erp-pro）") }}
                />
              </Card>
            ),
          },
          {
            key: 'entitlements',
            label: tr("授权主体"),
            children: (
              <Card
                size="small"
                title={tr("授权")}
                extra={
                  canIssue ? (
                    <Button
                      type="primary"
                      icon={<PlusOutlined />}
                      onClick={() => {
                        setIssueInitial(null);
                        setIssueOpen(true);
                      }}
                    >
                      {tr("签发授权")}</Button>
                  ) : null
                }
              >
                <Space wrap style={{ marginBottom: 12 }}>
                  <Select
                    allowClear
                    placeholder={tr("全部产品")}
                    style={{ width: 200, maxWidth: '100%' }}
                    value={productFilter}
                    onChange={(value) => {
                      setProductFilter(value);
                      setEntPage(1);
                    }}
                    options={(products.data ?? []).map((product) => ({
                      value: product.key,
                      label: `${product.name}（${product.key}）`,
                    }))}
                  />
                  <Select
                    allowClear
                    placeholder={tr("全部状态")}
                    style={{ width: 150, maxWidth: '100%' }}
                    value={statusFilter}
                    onChange={(value) => {
                      setStatusFilter(value);
                      setEntPage(1);
                    }}
                    options={[
                      { value: 'active', label: tr("有效") },
                      { value: 'expired', label: tr("已过期") },
                      { value: 'revoked', label: tr("已吊销") },
                    ]}
                  />
                  <Input.Search
                    allowClear
                    placeholder={tr("机器码 / 授权编号")}
                    style={{ width: 240, maxWidth: '100%' }}
                    onSearch={applyKeyword}
                  />
                  <Button icon={<ReloadOutlined />} aria-label={tr("刷新授权列表")} onClick={() => void refresh()}>
                    {tr("刷新")}</Button>
                </Space>

                {entitlements.isError ? (
                  <Alert
                    type="error"
                    showIcon
                    style={{ marginBottom: 16 }}
                    message={(entitlements.error as Error).message}
                    action={
                      <Button size="small" onClick={() => void entitlements.refetch()}>
                        {tr("重试")}</Button>
                    }
                  />
                ) : null}
                <Table<LicenseEntitlement>
                  scroll={{ x: 'max-content' }}
                  rowKey="id"
                  size="small"
                  loading={entitlements.isPending}
                  dataSource={entitlements.data?.items ?? []}
                  columns={entitlementColumns}
                  pagination={responsivePagination(isMobile, {
                    current: entPage,
                    pageSize: entPageSize,
                    total: entitlements.data?.total ?? 0,
                    showTotal: (total) => tr("共 {{arg0}} 条", { arg0: total }),
                    onChange: (page: number, size: number) => {
                      setEntPage(page);
                      setEntPageSize(size);
                    },
                  })}
                  locale={{ emptyText: <Empty description={tr("尚无授权；签发后客户端校验状态即可拿到签名快照")} /> }}
                />
              </Card>
            ),
          },
          {
            key: 'offline',
            label: tr("离线授权码"),
            children: (
              <Card
                size="small"
                title={tr("离线码台账")}
                extra={
                  canIssueOffline ? (
                    <Button type="primary" icon={<PlusOutlined />} onClick={() => setOfflineOpen(true)}>
                      {tr("签发离线码")}</Button>
                  ) : null
                }
              >
                <Space wrap style={{ marginBottom: 12 }}>
                  <Input.Search
                    allowClear
                    placeholder={tr("机器码 / 授权编号")}
                    style={{ width: 260, maxWidth: '100%' }}
                    onSearch={applyKeyword}
                  />
                  <Button icon={<ReloadOutlined />} aria-label={tr("刷新离线码台账")} onClick={() => void refresh()}>
                    {tr("刷新")}</Button>
                  <Typography.Text type="secondary">
                    {tr("离线码绑定机器码与产品键；发出去以后无法吊销，请在台账里保留记录。")}</Typography.Text>
                </Space>

                {offlineCodes.isError ? (
                  <Alert
                    type="error"
                    showIcon
                    style={{ marginBottom: 16 }}
                    message={(offlineCodes.error as Error).message}
                    action={
                      <Button size="small" onClick={() => void offlineCodes.refetch()}>
                        {tr("重试")}</Button>
                    }
                  />
                ) : null}
                <Table<OfflineLicenseCode>
                  scroll={{ x: 'max-content' }}
                  rowKey="id"
                  size="small"
                  loading={offlineCodes.isPending}
                  dataSource={offlineCodes.data?.items ?? []}
                  columns={offlineColumns}
                  pagination={responsivePagination(isMobile, {
                    current: offPage,
                    pageSize: offPageSize,
                    total: offlineCodes.data?.total ?? 0,
                    showTotal: (total) => tr("共 {{arg0}} 条", { arg0: total }),
                    onChange: (page: number, size: number) => {
                      setOffPage(page);
                      setOffPageSize(size);
                    },
                  })}
                  locale={{ emptyText: tr("尚未签发离线授权码") }}
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
                      {tr("签名密钥")}</Space>
                  ),
                  children: (
                    <Card
                      size="small"
                      title={tr("签名密钥")}
                      extra={
                        <Space>
                          <Button icon={<ReloadOutlined />} onClick={() => void mutateKey('sync')}>
                            {tr("同步配置")}</Button>
                        </Space>
                      }
                    >
                      {signingKeys.isError ? (
                        <Alert
                          type="error"
                          showIcon
                          style={{ marginBottom: 16 }}
                          message={(signingKeys.error as Error).message}
                          action={
                            <Button size="small" onClick={() => void signingKeys.refetch()}>
                              {tr("重试")}</Button>
                          }
                        />
                      ) : null}

                      {signingStatus ? (
                        <Alert
                          type={signingStatus.isManaged ? 'success' : 'warning'}
                          showIcon
                          style={{ marginBottom: 16 }}
                          message={
                            signingStatus.isManaged
                              ? tr("当前签发密钥：{{arg0}}", { arg0: signingStatus.currentKeyId ?? '-' })
                              : tr("部署未配置签发私钥（Licensing__SigningKeyPkcs8），授权校验不生效")
                          }
                          description={
                            signingStatus.isManaged ? (
                              <Space direction="vertical" size={2}>
                                <Typography.Text type="secondary">
                                  {tr("公钥指纹：")}{signingStatus.fingerprint}
                                </Typography.Text>
                                <Typography.Text type="secondary">
                                  {tr("退役密钥继续参与验签；私钥只存在于部署环境变量，管理台永远拿不到。")}</Typography.Text>
                              </Space>
                            ) : (
                              <Typography.Text type="secondary">
                                {tr("未配置私钥时客户端一律按 unmanaged 处理，也不会签发离线码。")}</Typography.Text>
                            )
                          }
                        />
                      ) : null}

                      <Descriptions size="small" column={1} style={{ marginBottom: 12 }}>
                        <Descriptions.Item label={tr("轮换做法")}>
                          {tr("部署新私钥 → 重启服务 → 点「同步配置」把新密钥登记为当前； 旧密钥自动转为退役状态，客户端已拿到的旧快照仍能验签。")}</Descriptions.Item>
                      </Descriptions>

                      <Table<LicenseSigningKey>
                        scroll={{ x: 'max-content' }}
                        rowKey="id"
                        size="small"
                        loading={signingKeys.isPending}
                        dataSource={signingStatus?.keys ?? []}
                        columns={signingKeyColumns}
                        pagination={false}
                        locale={{ emptyText: tr("尚未登记签名密钥") }}
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
        onSaved={handleProductSaved}
      />

      <IssueLicenseModal
        open={issueOpen}
        products={products.data ?? []}
        initial={issueInitial}
        onClose={() => {
          setIssueOpen(false);
          setIssueInitial(null);
        }}
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
              {tr("签名密钥由平台管理员维护；当前账号只能查看授权数据。")}</Typography.Text>
          </Space>
        </Card>
      )}
    </Space>
  );
}
