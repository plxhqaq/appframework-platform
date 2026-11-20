import { tr } from '../../i18n/translate';
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
  Table,
  Tag,
  Typography,
  theme as antdTheme,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState, type ReactNode } from 'react';
import { PageHeading } from '../../app/PageHeading';
import { platformApi } from '../../shared/api/platform';
import type { ModuleInfo } from '../../shared/api/types';
import { formatBytes, formatDateTime } from '../../shared/format';
import { AutoRefreshControls } from '../../shared/ui/AutoRefreshControls';
import { useAutoRefresh } from '../../shared/useAutoRefresh';
import { renderTableExpandIcon } from '../../shared/ui/TableExpandIcon';

interface UptimeAnchor {
  sampledAt: number;
  systemUptimeSeconds: number;
  platformUptimeSeconds: number;
}

const moduleColumns: TableColumnsType<ModuleInfo> = [
  { get title() { return tr("模块键"); }, dataIndex: 'key', key: 'key', width: 170 },
  {
    get title() { return tr("显示名键"); },
    dataIndex: 'displayNameKey',
    key: 'displayNameKey',
    width: 220,
    responsive: ['md'],
  },
  {
    get title() { return tr("阶段"); },
    dataIndex: 'stage',
    key: 'stage',
    width: 80,
    render: (stage: string) => <Tag color={stage === 'S0' ? 'default' : 'green'}>{stage}</Tag>,
  },
  {
    get title() { return tr("版本"); },
    dataIndex: 'version',
    key: 'version',
    width: 180,
    responsive: ['lg'],
  },
  {
    get title() { return tr("路由"); },
    dataIndex: 'routes',
    key: 'routes',
    width: 70,
    align: 'center',
    render: (routes: string[]) => routes.length,
  },
  {
    get title() { return tr("权限"); },
    dataIndex: 'requiredPermissions',
    key: 'requiredPermissions',
    width: 70,
    align: 'center',
    render: (codes: string[]) => codes.length,
  },
];

function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const clock = [hours, minutes, seconds % 60]
    .map((part) => String(part).padStart(2, '0'))
    .join(':');
  return days > 0 ? tr('{{days}} 天 {{clock}}', { days, clock }) : clock;
}

function percentText(value: number): string {
  return `${value.toFixed(1)}%`;
}

/** 用主色派生 12% 浅底：token 的 *Bg 由主色推导，湖青等深主色会得到发闷的中灰底。 */
function tileBackground(accent: string): { background: string } {
  return { background: `color-mix(in srgb, ${accent} 12%, transparent)` };
}

function UptimeBlock({
  label,
  seconds,
  hint,
  accent,
}: {
  label: string;
  seconds?: number;
  hint: string;
  accent: string;
}) {
  return (
    <div className="stat-tile" style={tileBackground(accent)}>
      <Typography.Text className="stat-tile__label" style={{ color: accent }}>
        {label}
      </Typography.Text>
      <div>
        <Typography.Text className="dashboard-uptime" strong>
          {seconds != null ? formatDuration(seconds) : '--'}
        </Typography.Text>
      </div>
      <Typography.Text type="secondary" className="dashboard-caption">
        {hint}
      </Typography.Text>
    </div>
  );
}

/** 两个已运行时长共用一只本地秒表：轮询只校准锚点，自动刷新开关不影响它们继续走。 */
function UptimeStats({
  anchor,
  systemUptimeSeconds,
  platformUptimeSeconds,
  bootTime,
  startedAt,
}: {
  anchor: UptimeAnchor | null;
  systemUptimeSeconds?: number;
  platformUptimeSeconds?: number;
  bootTime?: string;
  startedAt?: string;
}) {
  const [now, setNow] = useState(() => performance.now());
  const { token } = antdTheme.useToken();

  useEffect(() => {
    const timer = window.setInterval(() => setNow(performance.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  const elapsed = anchor ? Math.max(0, Math.floor((now - anchor.sampledAt) / 1000)) : 0;
  const systemSeconds = anchor
    ? anchor.systemUptimeSeconds + elapsed
    : systemUptimeSeconds;
  const platformSeconds = anchor
    ? anchor.platformUptimeSeconds + elapsed
    : platformUptimeSeconds;

  return (
    <Row gutter={[16, 8]}>
      <Col xs={24} sm={12}>
        <UptimeBlock
          label={tr("服务器开机时间")}
          seconds={systemSeconds}
          hint={bootTime ? tr("开机于 {{arg0}}", { arg0: formatDateTime(bootTime) }) : tr("开机时间未知")}
          accent={token.colorPrimary}
        />
      </Col>
      <Col xs={24} sm={12}>
        <UptimeBlock
          label={tr("系统运行时间")}
          seconds={platformSeconds}
          hint={startedAt ? tr("启动于 {{arg0}}", { arg0: formatDateTime(startedAt) }) : tr("启动时间未知")}
          accent={token.colorSuccess}
        />
      </Col>
    </Row>
  );
}

/** 百分比仪表盘：antd dashboard 形态（底部开口的环形进度），不引入额外依赖。 */
function DashGauge({
  value,
  label,
  color,
  caption,
  segment,
}: {
  value: number;
  label: string;
  color: string;
  caption?: ReactNode;
  /** 环内分段：表示总量中属于某一子项的部分（如平台进程占用）。 */
  segment?: { value: number; color: string };
}) {
  const { token } = antdTheme.useToken();
  const clamped = Math.max(0, Math.min(100, Number(value.toFixed(1))));
  const segmentPercent = segment
    ? Math.max(0, Math.min(clamped, Number(segment.value.toFixed(1))))
    : null;

  return (
    <div className="dash-gauge">
      <Typography.Text type="secondary" className="dash-gauge__label">
        {label}
      </Typography.Text>
      <Progress
        type="dashboard"
        percent={clamped}
        success={
          segmentPercent != null
            ? { percent: segmentPercent, strokeColor: segment?.color }
            : undefined
        }
        gapDegree={100}
        gapPosition="bottom"
        strokeColor={color}
        trailColor={token.colorFillSecondary}
        size={132}
        format={() => percentText(clamped)}
        aria-label={
          segmentPercent != null
            ? tr('{{label}} 总计 {{total}}，其中 {{segment}}', {
                label,
                total: percentText(clamped),
                segment: percentText(segmentPercent),
              })
            : `${label} ${percentText(clamped)}`
        }
      />
      {caption ? (
        <Typography.Text type="secondary" className="dash-gauge__caption">
          {caption}
        </Typography.Text>
      ) : null}
    </div>
  );
}

/** 彩色信息块：数字型指标用底色和强调色区分，避免整页只有灰白两色。 */
function StatTile({
  label,
  value,
  hint,
  accent,
}: {
  label: string;
  value: ReactNode;
  hint?: string;
  accent: string;
}) {
  return (
    <div className="stat-tile" style={tileBackground(accent)}>
      <Typography.Text className="stat-tile__label" style={{ color: accent }}>
        {label}
      </Typography.Text>
      <div className="stat-tile__body">
        <div className="stat-tile__value" style={{ color: accent }}>
          {value}
        </div>
        {hint ? (
          <Typography.Text type="secondary" className="stat-tile__hint">
            {hint}
          </Typography.Text>
        ) : null}
      </div>
    </div>
  );
}

function renderModuleDetails(module: ModuleInfo) {
  return (
    <Flex vertical gap={10}>
      <div>
        <Typography.Text type="secondary">{tr("路由")}</Typography.Text>
        <Flex wrap gap={4} style={{ marginTop: 4 }}>
          {module.routes.length > 0 ? (
            module.routes.map((route) => <Tag key={route}>{route}</Tag>)
          ) : (
            <Typography.Text type="secondary">{tr("未声明")}</Typography.Text>
          )}
        </Flex>
      </div>
      <div>
        <Typography.Text type="secondary">{tr("声明权限")}</Typography.Text>
        <Flex wrap gap={4} style={{ marginTop: 4 }}>
          {module.requiredPermissions.length > 0 ? (
            module.requiredPermissions.map((code) => (
              <Tag key={code} color="blue">
                {code}
              </Tag>
            ))
          ) : (
            <Typography.Text type="secondary">{tr("未声明")}</Typography.Text>
          )}
        </Flex>
      </div>
    </Flex>
  );
}

/**
 * 总览仪表盘：运行状态、进程资源、服务器资源监控、节点/安装实例与平台信息。
 *
 * - 服务器运行时间由本地秒表推进（每秒一次），轮询只校准锚点，和自动刷新开关解耦；
 * - 节点/实例等状态跃迁由 AdminHub 事件失效回拉；自动刷新默认关闭，需手动打开后按间隔兜底（最小 1s），切换路由卸载即暂停；
 * - 百分比一律用 antd dashboard 仪表盘：CPU/内存环内分段同时显示整机总量与平台进程占用，
 *   数量类指标用彩色信息块区分语义，避免整页灰白。
 */
export function OverviewPage() {
  const { token } = antdTheme.useToken();
  const autoRefresh = useAutoRefresh();
  const [uptimeAnchor, setUptimeAnchor] = useState<UptimeAnchor | null>(null);

  const modules = useQuery({
    queryKey: ['platform', 'modules'],
    queryFn: ({ signal }) => platformApi.modules(signal),
  });

  const dashboard = useQuery({
    queryKey: ['platform', 'dashboard'],
    queryFn: ({ signal }) => platformApi.dashboard(signal),
    refetchInterval: autoRefresh.refetchInterval,
  });

  useEffect(() => {
    const board = dashboard.data;
    if (!board) {
      return;
    }

    setUptimeAnchor({
      sampledAt: performance.now(),
      systemUptimeSeconds: board.systemUptimeSeconds,
      platformUptimeSeconds: board.uptimeSeconds,
    });
  }, [dashboard.data]);

  const data = dashboard.data;
  const system = data?.system;
  const nodes = data?.nodes;
  const installations = data?.installations;
  const platform = data?.platform;
  const healthy = dashboard.isSuccess && system?.status === 'ok';

  const usageColor = (value: number, normal: string) =>
    value >= 85 ? token.colorError : value >= 70 ? token.colorWarning : normal;

  const serverCpu = system?.serverCpuUsagePercent ?? null;
  const serverMemory = system?.serverMemoryUsagePercent ?? null;

  const onlineRate = nodes && nodes.total > 0 ? (nodes.online / nodes.total) * 100 : 0;

  const intervalLabel = autoRefresh.label;

  return (
    <Flex vertical gap={16}>
      <PageHeading
        title={tr("仪表盘")}
        description={
          data
            ? tr("服务端时间 {{arg0}} · {{arg1}}", { arg0: formatDateTime(data.serverTime), arg1: intervalLabel })
            : tr("正在读取平台状态…")
        }
        extra={
          <Space size={8} wrap>
            <AutoRefreshControls state={autoRefresh} />
            <Button
              icon={<ReloadOutlined />}
              loading={dashboard.isFetching}
              onClick={() => void dashboard.refetch()}
            >
              {tr("刷新")}</Button>
          </Space>
        }
      />

      {dashboard.isError ? (
        <Alert
          type="error"
          showIcon
          message={(dashboard.error as Error).message}
          action={
            <Button size="small" onClick={() => void dashboard.refetch()}>
              {tr("重试")}</Button>
          }
        />
      ) : null}

      <Row gutter={[16, 16]}>
        <Col span={24}>
          <Card
            size="small"
            title={tr("运行状态")}
            loading={dashboard.isPending}
            extra={
              <Badge
                status={healthy ? 'success' : 'error'}
                text={healthy ? tr("运行正常") : tr("状态异常")}
              />
            }
            style={{ height: '100%' }}
          >
            <UptimeStats
              anchor={uptimeAnchor}
              systemUptimeSeconds={data?.systemUptimeSeconds}
              platformUptimeSeconds={data?.uptimeSeconds}
              bootTime={data?.bootTime}
              startedAt={data?.startedAt}
            />
          </Card>
        </Col>
      </Row>

      <Row gutter={[16, 16]}>
        <Col xs={24} xl={12}>
          <Card
            size="small"
            title={tr("资源占用")}
            loading={dashboard.isPending}
            extra={system ? <Tag color="blue">{system.processorCount} {tr("核")}</Tag> : null}
            style={{ height: '100%' }}
          >
            <Row gutter={[8, 8]}>
              <Col span={12}>
                <DashGauge
                  value={serverCpu ?? system?.cpuUsagePercent ?? 0}
                  label={tr("整机 CPU")}
                  color={usageColor(serverCpu ?? system?.cpuUsagePercent ?? 0, token.colorPrimary)}
                  segment={
                    serverCpu != null
                      ? { value: system?.cpuUsagePercent ?? 0, color: token.colorSuccess }
                      : undefined
                  }
                  caption={
                    serverCpu != null
                      ? tr("平台进程 {{arg0}}", { arg0: percentText(system?.cpuUsagePercent ?? 0) })
                      : tr("整机数据暂不可用")
                  }
                />
              </Col>
              <Col span={12}>
                <DashGauge
                  value={serverMemory ?? system?.memoryUsagePercent ?? 0}
                  label={tr("整机内存")}
                  color={usageColor(serverMemory ?? system?.memoryUsagePercent ?? 0, token.colorPrimary)}
                  segment={
                    serverMemory != null
                      ? { value: system?.memoryUsagePercent ?? 0, color: token.colorSuccess }
                      : undefined
                  }
                  caption={
                    serverMemory != null
                      ? tr("平台进程 {{arg0}}", { arg0: percentText(system?.memoryUsagePercent ?? 0) })
                      : tr("整机数据暂不可用")
                  }
                />
              </Col>
            </Row>
            {/* 与「节点与安装实例」卡同距：色块行独立成行并留 8px，两卡色块顶边才对得齐。 */}
            <Row gutter={[8, 8]} style={{ marginTop: 8 }}>
              <Col span={12}>
                <StatTile
                  label={tr("工作集")}
                  value={system ? formatBytes(system.workingSetBytes) : '-'}
                  hint={system ? tr("总计 {{arg0}}", { arg0: formatBytes(system.totalMemoryBytes) }) : undefined}
                  accent={token.colorPrimary}
                />
              </Col>
              <Col span={12}>
                <StatTile
                  label={tr("托管堆")}
                  value={system ? formatBytes(system.managedMemoryBytes) : '-'}
                  hint={
                    system && system.workingSetBytes > 0
                      ? tr("占工作集 {{arg0}}", { arg0: percentText(
                          (system.managedMemoryBytes / system.workingSetBytes) * 100,
                        ) })
                      : undefined
                  }
                  accent={token.colorSuccess}
                />
              </Col>
            </Row>
          </Card>
        </Col>

        <Col xs={24} xl={12}>
          <Card
            size="small"
            title={tr("节点与安装实例")}
            loading={dashboard.isPending}
            extra={
              installations ? (
                <Tag color={installations.connected > 0 ? 'green' : undefined}>
                  {tr("实时连接")}{installations.connected} {tr("个")}</Tag>
              ) : null
            }
            style={{ height: '100%' }}
          >
            <DashGauge
              value={onlineRate}
              label={tr("节点在线率")}
              color={token.colorSuccess}
              caption={tr("节点在线 {{arg0}} / {{arg1}} · 安装实例在线 {{arg2}} / {{arg3}}", {
                arg0: nodes?.online ?? 0,
                arg1: nodes?.total ?? 0,
                arg2: installations?.online ?? 0,
                arg3: installations?.total ?? 0,
              })}
            />
            <Row gutter={[8, 8]} style={{ marginTop: 8 }}>
              <Col xs={12} sm={6}>
                <StatTile
                  label={tr("节点待机")}
                  value={nodes?.idle ?? '-'}
                  accent={token.colorInfo}
                />
              </Col>
              <Col xs={12} sm={6}>
                <StatTile
                  label={tr("节点运行")}
                  value={nodes?.running ?? '-'}
                  accent={token.colorWarning}
                />
              </Col>
              <Col xs={12} sm={6}>
                <StatTile
                  label={tr("节点离线")}
                  value={nodes?.offline ?? '-'}
                  accent={token.colorTextSecondary}
                />
              </Col>
              <Col xs={12} sm={6}>
                <StatTile
                  label={tr("节点停用")}
                  value={nodes?.disabled ?? '-'}
                  accent={token.colorError}
                />
              </Col>
            </Row>
          </Card>
        </Col>
      </Row>

      <Row gutter={[16, 16]}>
        <Col xs={24} xl={12}>
          <Card
            size="small"
            title={tr("运行平台")}
            loading={dashboard.isPending}
            extra={platform ? <Tag color="geekblue">{platform.osArchitecture}</Tag> : null}
            style={{ height: '100%' }}
          >
            <Descriptions
              column={{ xs: 1, sm: 2 }}
              size="small"
              items={[
                { key: 'os', label: tr("操作系统"), children: platform?.osDescription ?? '-' },
                { key: 'arch', label: tr("系统架构"), children: platform?.osArchitecture ?? '-' },
                { key: 'runtime', label: tr("运行时"), children: platform?.runtimeVersion ?? '-' },
                { key: 'machine', label: tr("主机名"), children: platform?.machineName ?? '-' },
                {
                  key: 'cpuCount',
                  label: tr("逻辑处理器"),
                  children: system ? tr('{{count}} 核', { count: system.processorCount }) : '-',
                },
              ]}
            />
          </Card>
        </Col>

        <Col xs={24} xl={12}>
          <Card
            size="small"
            title={tr("平台信息")}
            loading={dashboard.isPending}
            extra={platform ? <Tag color="purple">v{platform.platformVersion}</Tag> : null}
            style={{ height: '100%' }}
          >
            <Descriptions
              column={{ xs: 1, sm: 2 }}
              size="small"
              items={[
                { key: 'version', label: tr("平台版本"), children: platform?.platformVersion ?? '-' },
                {
                  key: 'modules',
                  label: tr("已装配模块"),
                  children: platform
                    ? tr('{{count}} 个', { count: platform.moduleCount })
                    : '-',
                },
                {
                  key: 'environment',
                  label: tr("运行环境"),
                  children: platform ? <Tag>{platform.environmentName}</Tag> : '-',
                },
                {
                  key: 'storage',
                  label: tr("可用存储"),
                  children:
                    system?.availableStorageBytes != null
                      ? formatBytes(system.availableStorageBytes)
                      : tr("未知"),
                },
                {
                  key: 'serverTime',
                  label: tr("服务器时间"),
                  children: data ? formatDateTime(data.serverTime) : '-',
                },
              ]}
            />
          </Card>
        </Col>
      </Row>

      <Card
        size="small"
        title={tr("模块明细")}
        extra={<Tag color="blue">{modules.data?.length ?? 0} {tr("个")}</Tag>}
      >
        {modules.isError ? (
          <Alert type="error" showIcon message={(modules.error as Error).message} />
        ) : (
          <Table<ModuleInfo>
            rowKey="key"
            size="small"
            pagination={false}
            scroll={{ x: 'max-content' }}
            loading={modules.isPending}
            dataSource={modules.data ?? []}
            columns={moduleColumns}
            expandable={{
              expandIcon: renderTableExpandIcon,
              expandedRowRender: renderModuleDetails,
              rowExpandable: (module) =>
                module.routes.length > 0 || module.requiredPermissions.length > 0,
            }}
          />
        )}
      </Card>
    </Flex>
  );
}
