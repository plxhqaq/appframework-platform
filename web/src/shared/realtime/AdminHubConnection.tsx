import { useQueryClient } from '@tanstack/react-query';
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Tooltip, Tag } from 'antd';
import { resolveApiUrl } from '../api/client';
import { tr } from '../../i18n/translate';
import { useAuthStore } from '../../stores/auth';
import { AdminHubStatusContext, useAdminHubStatus, type AdminHubStatus } from './adminHubStatus';
import {
  ADMIN_HUB_EVENTS,
  adminHubBackoffDelayMs,
  createInvalidationBatcher,
  planAdminEventInvalidations,
  resolveReconnectReconcile,
} from './adminEventsUi';

/** 与服务端 AdminHubPaths.Root / AdminHubMethods.Touch 对齐。 */
const ADMIN_HUB_PATH = '/hubs/admin';
const ADMIN_HUB_TOUCH = 'Touch';

type TimerHandle = ReturnType<typeof setTimeout>;

/**
 * 登录后连 /hubs/admin：事件→失效回拉（见 adminEventsUi 的铁律注释）、
 * 30s 显式 Touch 承载服务端令牌守卫、断开按退避重连且重连前静默续期、
 * 重连成功全量失效对账。首连失败也走同一重连循环（withAutomaticReconnect 覆盖不到首连）。
 */
export function AdminHubProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const authStatus = useAuthStore((state) => state.status);
  const session = useAuthStore((state) => state.session);
  const [hubStatus, setHubStatus] = useState<AdminHubStatus>('disconnected');

  // 页面用 session?.appKey ?? appKeys[0] 定位当前应用上下文，事件过滤沿用同一口径。
  const appKey = session?.appKey ?? session?.appKeys?.[0] ?? null;
  const appKeyRef = useRef(appKey);
  appKeyRef.current = appKey;

  useEffect(() => {
    if (authStatus !== 'authenticated') {
      setHubStatus('disconnected');
      return;
    }

    let disposed = false;
    let attempt = 0;
    let retryTimer: TimerHandle | null = null;
    let touchTimer: TimerHandle | null = null;

    const batcher = createInvalidationBatcher((keys) => {
      for (const key of keys) {
        void queryClient.invalidateQueries({ queryKey: key });
      }
    });

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(resolveApiUrl(ADMIN_HUB_PATH), {
        accessTokenFactory: () => useAuthStore.getState().accessToken ?? '',
      })
      .configureLogging(LogLevel.Error)
      .build();

    const plan = (eventName: string) => (payload: unknown) => {
      batcher(planAdminEventInvalidations(eventName, payload, { appKey: appKeyRef.current }));
    };
    connection.on(ADMIN_HUB_EVENTS.installationStateChanged, plan(ADMIN_HUB_EVENTS.installationStateChanged));
    connection.on(ADMIN_HUB_EVENTS.nodeStateChanged, plan(ADMIN_HUB_EVENTS.nodeStateChanged));
    connection.on(ADMIN_HUB_EVENTS.qrSessionChanged, plan(ADMIN_HUB_EVENTS.qrSessionChanged));
    connection.on(ADMIN_HUB_EVENTS.licenseChanged, plan(ADMIN_HUB_EVENTS.licenseChanged));
    connection.on(ADMIN_HUB_EVENTS.releaseIngestionProgress, plan(ADMIN_HUB_EVENTS.releaseIngestionProgress));
    connection.on(ADMIN_HUB_EVENTS.userSessionsRevoked, plan(ADMIN_HUB_EVENTS.userSessionsRevoked));

    const stopTouch = () => {
      if (touchTimer !== null) {
        clearInterval(touchTimer);
        touchTimer = null;
      }
    };
    // 传输层 keepalive 不经过 Hub 方法过滤器，必须显式 invoke 才能被过期/epoch 守卫踢线。
    const startTouch = () => {
      stopTouch();
      touchTimer = setInterval(() => {
        if (connection.state === HubConnectionState.Connected) {
          connection.invoke(ADMIN_HUB_TOUCH).catch(() => {
            // 服务端踢线会以 onclose 收尾，这里不重复处理
          });
        }
      }, 30_000);
    };

    const scheduleRetry = () => {
      if (disposed || retryTimer !== null) {
        return;
      }
      const delay = adminHubBackoffDelayMs(attempt);
      attempt += 1;
      retryTimer = setTimeout(() => {
        retryTimer = null;
        void (async () => {
          if (disposed) {
            return;
          }
          // 断线常因令牌过期/被吊销：先静默续期，续期失败即回匿名态不再空转。
          await useAuthStore.getState().refreshAccessToken().catch(() => null);
          if (disposed) {
            return;
          }
          if (useAuthStore.getState().status !== 'authenticated') {
            setHubStatus('disconnected');
            return;
          }
          try {
            await connection.start();
            attempt = 0;
            setHubStatus('connected');
            startTouch();
            if (resolveReconnectReconcile() === 'all') {
              void queryClient.invalidateQueries();
            }
          } catch {
            if (!disposed) {
              setHubStatus('reconnecting');
              scheduleRetry();
            }
          }
        })();
      }, delay);
    };

    connection.onclose(() => {
      stopTouch();
      if (disposed) {
        return;
      }
      setHubStatus('reconnecting');
      scheduleRetry();
    });

    setHubStatus('connecting');
    void (async () => {
      try {
        await connection.start();
        if (disposed) {
          return;
        }
        attempt = 0;
        setHubStatus('connected');
        startTouch();
      } catch {
        if (!disposed) {
          setHubStatus('reconnecting');
          scheduleRetry();
        }
      }
    })();

    return () => {
      disposed = true;
      if (retryTimer !== null) {
        clearTimeout(retryTimer);
      }
      stopTouch();
      void connection.stop().catch(() => undefined);
    };
  }, [authStatus, queryClient]);

  return (
    <AdminHubStatusContext.Provider value={hubStatus}>{children}</AdminHubStatusContext.Provider>
  );
}

const STATUS_LABELS: Record<AdminHubStatus, string> = {
  connected: '已连接',
  connecting: '连接中',
  reconnecting: '重连中',
  disconnected: '已断开',
};

const STATUS_COLORS: Record<AdminHubStatus, string> = {
  connected: 'success',
  connecting: 'processing',
  reconnecting: 'warning',
  disconnected: 'default',
};

/** 顶栏实时连接徽标：连接态是运维可见性，不代表数据新鲜度（数据靠回拉）。 */
export function AdminHubStatusBadge() {
  const status = useAdminHubStatus();
  return (
    <Tooltip title={tr('实时连接')}>
      <Tag color={STATUS_COLORS[status]}>{tr(STATUS_LABELS[status])}</Tag>
    </Tooltip>
  );
}
