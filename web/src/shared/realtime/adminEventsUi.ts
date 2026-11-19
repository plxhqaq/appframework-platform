import type { QueryKey } from '@tanstack/react-query';

/**
 * AdminHub 事件 → react-query 失效键的纯映射层。
 *
 * 铁律：所有推送只是提示。这里把事件翻译成「该回拉哪些查询」，
 * 绝不把负载当事实写进缓存；未知事件与负载形状不符一律返回空。
 */
export const ADMIN_HUB_EVENTS = {
  licenseChanged: 'LicenseChanged',
  qrSessionChanged: 'QrSessionChanged',
  userSessionsRevoked: 'UserSessionsRevoked',
  releaseIngestionProgress: 'ReleaseIngestionProgress',
  installationStateChanged: 'InstallationStateChanged',
  nodeStateChanged: 'NodeStateChanged',
} as const;

export interface InstallationStateChangedPayload {
  appKey: string;
  installationId: string;
  nodeId: string;
  onlineStatus: string;
  stationStatus: string;
  alarmLevel: string;
  occurredAt: string;
}

export interface NodeStateChangedPayload {
  nodeId: string;
  onlineStatus: string;
  stationStatus: string;
  alarmLevel: string;
  occurredAt: string;
}

export interface QrSessionChangedPayload {
  appKey: string;
  sessionId: string;
  status: string;
  occurredAt: string;
}

export interface LicenseChangedPayload {
  appKey: string;
  machineCode: string;
  reason: string;
  occurredAt: string;
}

export interface ReleaseIngestionProgressPayload {
  appKey: string;
  packageId: string;
  status: string;
  progress: number;
  occurredAt: string;
}

export interface UserSessionsRevokedPayload {
  /** 被吊销会话记录的应用键；平台上下文会话为 null。 */
  appKey: string | null;
  userId: string;
  count: number;
  occurredAt: string;
}

/** 事件过滤上下文：管理台当前应用上下文（与页面 `session?.appKey ?? appKeys[0]` 口径一致）。 */
export interface AdminEventContext {
  appKey: string | null;
}

const INSTALLATION_KEYS: readonly QueryKey[] = [['installations'], ['platform', 'dashboard']];
const NODE_KEYS: readonly QueryKey[] = [
  ['nodes'],
  ['installations'],
  ['platform', 'dashboard'],
];
const QR_KEYS: readonly QueryKey[] = [
  ['identity', 'qr-sessions'],
  ['identity', 'qr-session'],
];
const SESSIONS_KEYS: readonly QueryKey[] = [
  ['identity', 'sessions'],
  ['identity', 'users'],
];

function asRecord(payload: unknown): Record<string, unknown> | null {
  return typeof payload === 'object' && payload !== null
    ? (payload as Record<string, unknown>)
    : null;
}

function str(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

/** 事件带应用键时只在「与当前上下文同应用」时失效；null 上下文没有应用级查询。 */
function sameApp(record: Record<string, unknown>, ctx: AdminEventContext): boolean {
  const appKey = str(record.appKey);
  return appKey !== null && ctx.appKey !== null && appKey === ctx.appKey;
}

/** 把一条 AdminHub 事件翻译为需要失效的查询键前缀列表（可能为空）。 */
export function planAdminEventInvalidations(
  eventName: string,
  payload: unknown,
  ctx: AdminEventContext,
): QueryKey[] {
  const record = asRecord(payload);
  if (!record) {
    return [];
  }

  switch (eventName) {
    case ADMIN_HUB_EVENTS.installationStateChanged:
      if (str(record.installationId) === null || !sameApp(record, ctx)) {
        return [];
      }
      return [...INSTALLATION_KEYS];

    case ADMIN_HUB_EVENTS.nodeStateChanged:
      // 节点跨应用，不带 appKey：一律放行回拉。
      return str(record.nodeId) === null ? [] : [...NODE_KEYS];

    case ADMIN_HUB_EVENTS.qrSessionChanged:
      if (str(record.sessionId) === null || !sameApp(record, ctx)) {
        return [];
      }
      return [...QR_KEYS];

    case ADMIN_HUB_EVENTS.licenseChanged:
      return str(record.machineCode) === null || !sameApp(record, ctx)
        ? []
        : [['licensing']];

    case ADMIN_HUB_EVENTS.releaseIngestionProgress: {
      const packageId = str(record.packageId);
      if (packageId === null || !sameApp(record, ctx)) {
        return [];
      }
      return [
        ['versions', 'package', packageId],
        ['versions', 'release'],
        ['versions', 'releases'],
      ];
    }

    case ADMIN_HUB_EVENTS.userSessionsRevoked: {
      if (str(record.userId) === null) {
        return [];
      }
      // appKey 为 null 表示平台上下文会话，与当前应用无关，一律放行。
      const appKey = record.appKey === null ? null : str(record.appKey);
      if (appKey !== null && appKey !== ctx.appKey) {
        return [];
      }
      return [...SESSIONS_KEYS];
    }

    default:
      return [];
  }
}

/** 合批窗口：一次状态跃迁风暴只回拉一轮，避免逐事件打接口。 */
export const ADMIN_HUB_INVALIDATE_BATCH_MS = 250;

/** 断线重连退避：先快后慢封顶 60s，之后一直按 60s 重试。 */
export const ADMIN_HUB_BACKOFF_MS = [1_000, 2_000, 5_000, 10_000, 30_000, 60_000] as const;

export function adminHubBackoffDelayMs(attempt: number): number {
  const index = Math.min(Math.max(attempt, 0), ADMIN_HUB_BACKOFF_MS.length - 1);
  return ADMIN_HUB_BACKOFF_MS[index];
}

/** 保活上行周期：必须显式 invoke('Touch')，传输层 keepalive 不会触发服务端令牌守卫。 */
export const ADMIN_HUB_TOUCH_INTERVAL_MS = 30_000;

/** 重连成功后的对账范围：全量失效回拉，与「推送只是提示」语义对齐。 */
export type ReconnectReconcileScope = 'all';

export function resolveReconnectReconcile(): ReconnectReconcileScope {
  return 'all';
}

/**
 * 查询键合批器：窗口内去重，到期一次性交给 flush。
 * 返回的 push 可反复调用；空键列表不启动窗口。
 */
export function createInvalidationBatcher(
  flush: (keys: QueryKey[]) => void,
  options?: { delayMs?: number },
): (keys: readonly QueryKey[]) => void {
  const delayMs = options?.delayMs ?? ADMIN_HUB_INVALIDATE_BATCH_MS;
  let pending: QueryKey[] = [];
  let seen = new Set<string>();
  let timer: ReturnType<typeof setTimeout> | null = null;

  return (keys) => {
    if (keys.length === 0) {
      return;
    }
    for (const key of keys) {
      const signature = JSON.stringify(key);
      if (!seen.has(signature)) {
        seen.add(signature);
        pending.push(key);
      }
    }
    timer ??= setTimeout(() => {
      timer = null;
      const batch = pending;
      pending = [];
      seen = new Set();
      flush(batch);
    }, delayMs);
  };
}
