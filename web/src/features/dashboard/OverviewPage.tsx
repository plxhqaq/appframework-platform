import { ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Badge,
  Button,
  Card,
  Col,
  Descriptions,
  Flex,
  Progress,
  Row,
  Space,
  Statistic,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { platformApi } from '../../shared/api/platform';
import type { ModuleInfo } from '../../shared/api/types';
import { formatBytes } from '../../shared/format';
import { useAuthStore } from '../../stores/auth';

const HISTORY_SIZE = 30;
const REFRESH_INTERVAL_MS = 10000;

const moduleColumns: TableColumnsType<ModuleInfo> = [
  { title: '模块键', dataIndex: 'key', key: 'key', width: 140 },
  { title: '显示名键', dataIndex: 'displayNameKey', key: 'displayNameKey', width: 200 },
  {
    title: '阶段',
    dataIndex: 'stage',
    key: 'stage',
    width: 90,
    render: (stage: string) => <Tag color={stage === 'S0' ? 'default' : 'green'}>{stage}</Tag>,
  },
  { title: '版本', dataIndex: 'version', key: 'version', width: 160 },
  {
    title: '路由',
    dataIndex: 'routes',
    key: 'routes',
    render: (routes: string[]) => routes.map((route) => <Tag key={route}>{route}</Tag>),
  },
  {
    title: '声明权限',
    dataIndex: 'requiredPermissions',
    key: 'requiredPermissions',
    render: (codes: string[]) => (
      <Space size={[4, 4]} wrap>
        {codes.map((code) => (
          <Tag key={code} color="blue">
            {code}
          </Tag>
        ))}
      </Space>
    ),
  },
];

function formatUptime(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) {
    return `${days} 天 ${hours} 小时`;
  }
  if (hours > 0) {
    return `${hours} 小时 ${minutes} 分`;
  }
  if (minutes > 0) {
    return `${minutes} 分 ${seconds % 60} 秒`;
  }
  return `${seconds} 秒`;
}

function percentText(value: number): string {
  return `${value.toFixed(1)}%`;
}

/** 迷你趋势线：只画最近若干次采样，不引入图表依赖。 */
function Sparkline({ values, color }: { values: number[]; color: string }) {
  if (values.length < 2) {
    return <div style={{ height: 36 }} />;
  }

  const width = 100;
  const height = 36;
  const points = values
    .map((value, index) => {
      const x = (index / (values.length - 1)) * width;
      const y = height - (Math.min(100, Math.max(0, value)) / 100) * height;
      return `${x.toFixed(2)},${y.toFixed(2)}`;
    })
    .join(' ');

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      role="img"
      aria-label="资源占用趋势"
      style={{ width: '100%', height: 36 }}
    >
      <polyline
        points={points}
        fill="none"
        stroke={color}
        strokeWidth="2"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
}

/**
 * 总览仪表盘：系统状态、运行平台、资源占用、设备统计与平台信息。
 *
 * 数据来自 `GET /api/v1/platform/dashboard`，10 秒轮询一次；
 * 趋势线只保留本页打开期间的采样，不落任何存储。
 */
export function OverviewPage() {
  const session = useAuthStore((state) => state.session);
  const [cpuHistory, setCpuHistory] = useState<number[]>([]);
  const [memoryHistory, setMemoryHistory] = useState<number[]>([]);

  const modules = useQuery({
    queryKey: ['platform', 'modules'],
    queryFn: ({ signal }) => platformApi.modules(signal),
  });

  const dashboard = useQuery({
    queryKey: ['platform', 'dashboard'],
    queryFn: ({ signal }) => platformApi.dashboard(signal),
    refetchInterval: REFRESH_INTERVAL_MS,
  });

  useEffect(() => {
    if (!dashboard.data) {
      return;
    }

    const { system } = dashboard.data;
    setCpuHistory((previous) =>
      [...previous, system.cpuUsagePercent].slice(-HISTORY_SIZE),
    );
    setMemoryHistory((previous) =>
      [...previous, system.memoryUsagePercent].slice(-HISTORY_SIZE),
    );
  }, [dashboard.data]);

  const data = dashboard.data;
  const system = data?.system;
  const devices = data?.devices;
  const platform = data?.platform;
  const healthy = dashboard.isSuccess && system?.status === 'ok';

  return (
    <Flex vertical gap={16}>
      <Flex wrap justify="space-between" align="center" gap={8}>
        <div>
          <Typography.Title level={4} style={{ margin: 0 }}>
            仪表盘
          </Typography.Title>
          <Typography.Text type="secondary">
            {data
              ? `数据时间 ${new Date(data.serverTime).toLocaleString()}`
              : '正在读取平台状态…'}
          </Typography.Text>
        </div>
        <Button
          icon={<ReloadOutlined />}
          loading={dashboard.isFetching}
          onClick={() => void dashboard.refetch()}
        >
          刷新
        </Button>
      </Flex>

      {dashboard.isError ? (
        <Alert
          type="error"
          showIcon
          message={(dashboard.error as Error).message}
          action={
            <Button size="small" onClick={() => void dashboard.refetch()}>
              重试
            </Button>
          }
        />
      ) : null}

      <Row gutter={[16, 16]}>
        <Col xs={24} md={12} xl={8}>
          <Card size="small" title="系统状态" loading={dashboard.isPending} style={{ height: '100%' }}>
            <Flex vertical gap={12}>
              <Badge
                status={healthy ? 'success' : 'error'}
                text={healthy ? '运行正常' : '状态异常'}
              />
              <Statistic
                title="已运行"
                value={data ? formatUptime(data.uptimeSeconds) : '-'}
                valueStyle={{ fontSize: 26 }}
              />
              <Descriptions
                column={1}
                size="small"
                items={[
                  {
                    key: 'startedAt',
                    label: '启动时间',
                    children: data ? new Date(data.startedAt).toLocaleString() : '-',
                  },
                  {
                    key: 'environment',
                    label: '运行环境',
                    children: platform ? <Tag>{platform.environmentName}</Tag> : '-',
                  },
                  {
                    key: 'trace',
                    label: '探测端点',
                    children: <Typography.Text code>/healthz</Typography.Text>,
                  },
                ]}
              />
            </Flex>
          </Card>
        </Col>

        <Col xs={24} md={12} xl={8}>
          <Card size="small" title="运行平台" loading={dashboard.isPending} style={{ height: '100%' }}>
            {platform ? (
              <Descriptions
                column={1}
                size="small"
                items={[
                  { key: 'os', label: '操作系统', children: platform.osDescription },
                  { key: 'arch', label: '系统架构', children: platform.osArchitecture },
                  { key: 'runtime', label: '运行时', children: platform.runtimeVersion },
                  { key: 'machine', label: '主机名', children: platform.machineName },
                  {
                    key: 'cpuCount',
                    label: '逻辑处理器',
                    children: `${system?.processorCount ?? '-'} 核`,
                  },
                ]}
              />
            ) : (
              <Typography.Text type="secondary">暂无数据</Typography.Text>
            )}
          </Card>
        </Col>

        <Col xs={24} md={12} xl={8}>
          <Card size="small" title="资源占用" loading={dashboard.isPending} style={{ height: '100%' }}>
            {system ? (
              <Flex vertical gap={14}>
                <div>
                  <Flex justify="space-between" align="center">
                    <Typography.Text>CPU</Typography.Text>
                    <Typography.Text strong>{percentText(system.cpuUsagePercent)}</Typography.Text>
                  </Flex>
                  <Progress
                    percent={Math.round(system.cpuUsagePercent)}
                    showInfo={false}
                    size="small"
                    status={system.cpuUsagePercent >= 85 ? 'exception' : 'normal'}
                  />
                  <Sparkline
                    values={cpuHistory}
                    color={system.cpuUsagePercent >= 85 ? '#ff4d4f' : '#1677ff'}
                  />
                </div>
                <div>
                  <Flex justify="space-between" align="center">
                    <Typography.Text>内存</Typography.Text>
                    <Typography.Text strong>
                      {percentText(system.memoryUsagePercent)}
                    </Typography.Text>
                  </Flex>
                  <Progress
                    percent={Math.round(system.memoryUsagePercent)}
                    showInfo={false}
                    size="small"
                    status={system.memoryUsagePercent >= 85 ? 'exception' : 'normal'}
                  />
                  <Sparkline
                    values={memoryHistory}
                    color={system.memoryUsagePercent >= 85 ? '#ff4d4f' : '#52c41a'}
                  />
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {formatBytes(system.workingSetBytes)} / {formatBytes(system.totalMemoryBytes)}
                    （托管堆 {formatBytes(system.managedMemoryBytes)}）
                  </Typography.Text>
                </div>
                <Descriptions
                  column={1}
                  size="small"
                  items={[
                    {
                      key: 'storage',
                      label: '可用存储',
                      children:
                        system.availableStorageBytes != null
                          ? formatBytes(system.availableStorageBytes)
                          : '未知',
                    },
                  ]}
                />
              </Flex>
            ) : (
              <Typography.Text type="secondary">暂无数据</Typography.Text>
            )}
          </Card>
        </Col>

        <Col xs={24} md={12} xl={8}>
          <Card size="small" title="已连接设备" loading={dashboard.isPending} style={{ height: '100%' }}>
            {devices ? (
              <Flex vertical gap={10}>
                <Statistic title="实时连接" value={devices.connected} suffix="台" />
                <Flex wrap gap={8}>
                  <Tooltip title="数据库在线标记（含待机与运行）">
                    <Tag color="green">在线 {devices.online}</Tag>
                  </Tooltip>
                  <Tag color="blue">待机 {devices.idle}</Tag>
                  <Tag color="orange">运行 {devices.running}</Tag>
                  <Tag>离线 {devices.offline}</Tag>
                  <Tag color="red">停用 {devices.disabled}</Tag>
                  <Tag color="purple">总计 {devices.total}</Tag>
                </Flex>
                <Progress
                  percent={
                    devices.total > 0 ? Math.round((devices.online / devices.total) * 100) : 0
                  }
                  size="small"
                  format={(percent) => `在线率 ${percent}%`}
                />
              </Flex>
            ) : (
              <Typography.Text type="secondary">暂无数据</Typography.Text>
            )}
          </Card>
        </Col>

        <Col xs={24} md={12} xl={8}>
          <Card size="small" title="平台信息" loading={dashboard.isPending} style={{ height: '100%' }}>
            {platform ? (
              <Descriptions
                column={1}
                size="small"
                items={[
                  { key: 'version', label: '平台版本', children: platform.platformVersion },
                  {
                    key: 'modules',
                    label: '已装配模块',
                    children: `${platform.moduleCount} 个`,
                  },
                  {
                    key: 'environment',
                    label: '运行环境',
                    children: <Tag>{platform.environmentName}</Tag>,
                  },
                  {
                    key: 'storage',
                    label: '可用存储',
                    children:
                      system?.availableStorageBytes != null
                        ? formatBytes(system.availableStorageBytes)
                        : '未知',
                  },
                  {
                    key: 'serverTime',
                    label: '服务器时间',
                    children: data ? new Date(data.serverTime).toLocaleString() : '-',
                  },
                ]}
              />
            ) : (
              <Typography.Text type="secondary">暂无数据</Typography.Text>
            )}
          </Card>
        </Col>

        <Col xs={24} md={12} xl={8}>
          <Card
            size="small"
            title="平台模块"
            extra={<Tag>{modules.data?.length ?? 0} 个</Tag>}
            style={{ height: '100%' }}
          >
            {modules.isError ? (
              <Alert type="error" showIcon message={(modules.error as Error).message} />
            ) : (
              <Flex wrap gap={8}>
                {(modules.data ?? []).map((module) => (
                  <Tag key={module.key} color="geekblue">
                    {module.key} · {module.version}
                  </Tag>
                ))}
                {modules.isPending ? <Typography.Text type="secondary">加载中…</Typography.Text> : null}
              </Flex>
            )}
          </Card>
        </Col>

        <Col xs={24}>
          <Card size="small" title="当前主体">
            {session ? (
              <Descriptions
                column={{ xs: 1, sm: 2 }}
                size="small"
                items={[
                  { key: 'userName', label: '用户名', children: session.userName },
                  {
                    key: 'displayName',
                    label: '显示名',
                    children: session.displayName ?? '-',
                  },
                  { key: 'userId', label: '用户 ID', children: session.userId },
                  {
                    key: 'appKeys',
                    label: '可访问应用',
                    children: session.appKeys.join(', ') || '-',
                  },
                  {
                    key: 'roles',
                    label: '角色',
                    children: session.roles.join(', ') || '-',
                  },
                  {
                    key: 'permissions',
                    label: '权限',
                    children:
                      session.permissions.length > 6
                        ? `${session.permissions.slice(0, 6).join(', ')} 等 ${session.permissions.length} 项`
                        : session.permissions.join(', ') || '-',
                  },
                ]}
              />
            ) : (
              <Typography.Text type="secondary">尚未获取主体信息</Typography.Text>
            )}
          </Card>
        </Col>

      </Row>

      <Card
        size="small"
        title="模块明细"
        extra={<Typography.Text type="secondary">GET /api/v1/platform/modules</Typography.Text>}
      >
        <Table<ModuleInfo>
          rowKey="key"
          size="small"
          pagination={false}
          scroll={{ x: 'max-content' }}
          loading={modules.isPending}
          dataSource={modules.data ?? []}
          columns={moduleColumns}
        />
      </Card>
    </Flex>
  );
}
