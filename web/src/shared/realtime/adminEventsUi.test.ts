import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { QueryKey } from '@tanstack/react-query';
import {
  ADMIN_HUB_EVENTS,
  adminHubBackoffDelayMs,
  createInvalidationBatcher,
  planAdminEventInvalidations,
  resolveReconnectReconcile,
} from './adminEventsUi';

const ctx = { appKey: 'demo' };

describe('planAdminEventInvalidations', () => {
  it('安装实例事件同应用时失效实例列表与总览', () => {
    const keys = planAdminEventInvalidations(
      ADMIN_HUB_EVENTS.installationStateChanged,
      {
        appKey: 'demo',
        installationId: 'i-1',
        nodeId: 'n-1',
        onlineStatus: 'online',
        stationStatus: 'online',
        alarmLevel: 'none',
        occurredAt: '2026-11-19T08:00:00Z',
      },
      ctx,
    );
    expect(keys).toEqual([['installations'], ['platform', 'dashboard']]);
  });

  it('安装实例事件跨应用或与负载形状不符时静默', () => {
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.installationStateChanged,
        { appKey: 'other', installationId: 'i-1', nodeId: 'n-1' },
        ctx,
      ),
    ).toEqual([]);
    expect(
      planAdminEventInvalidations(ADMIN_HUB_EVENTS.installationStateChanged, null, ctx),
    ).toEqual([]);
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.installationStateChanged,
        { appKey: 'demo' },
        ctx,
      ),
    ).toEqual([]);
  });

  it('无应用上下文时应用级事件一律静默', () => {
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.licenseChanged,
        { appKey: 'demo', machineCode: 'm', reason: 'issued' },
        { appKey: null },
      ),
    ).toEqual([]);
  });

  it('节点事件不带应用键一律放行', () => {
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.nodeStateChanged,
        { nodeId: 'n-1', onlineStatus: 'online', stationStatus: 'idle', alarmLevel: 'none' },
        { appKey: null },
      ),
    ).toEqual([['nodes'], ['installations'], ['platform', 'dashboard']]);
  });

  it('扫码会话事件失效两个查询前缀', () => {
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.qrSessionChanged,
        { appKey: 'demo', sessionId: 's-1', status: 'approved' },
        ctx,
      ),
    ).toEqual([['identity', 'qr-sessions'], ['identity', 'qr-session']]);
  });

  it('摄取进度事件精确到包并带上发布视图', () => {
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.releaseIngestionProgress,
        { appKey: 'demo', packageId: 'p-9', status: 'ready', progress: 1 },
        ctx,
      ),
    ).toEqual([
      ['versions', 'package', 'p-9'],
      ['versions', 'release'],
      ['versions', 'releases'],
    ]);
  });

  it('会话吊销事件对平台上下文（appKey 为 null）放行', () => {
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.userSessionsRevoked,
        { appKey: null, userId: 'u-1', count: 2 },
        ctx,
      ),
    ).toEqual([['identity', 'sessions'], ['identity', 'users']]);
    expect(
      planAdminEventInvalidations(
        ADMIN_HUB_EVENTS.userSessionsRevoked,
        { appKey: 'other', userId: 'u-1', count: 1 },
        ctx,
      ),
    ).toEqual([]);
  });

  it('未知事件静默', () => {
    expect(planAdminEventInvalidations('Whatever', { appKey: 'demo' }, ctx)).toEqual([]);
  });
});

describe('createInvalidationBatcher', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('窗口内合批并按 JSON 去重', () => {
    const flush = vi.fn();
    const push = createInvalidationBatcher(flush, { delayMs: 250 });

    push([['installations'], ['platform', 'dashboard']]);
    push([['installations']]);
    push([['nodes']]);
    expect(flush).not.toHaveBeenCalled();

    vi.advanceTimersByTime(250);
    const batches = flush.mock.calls as unknown as [QueryKey[]][];
    expect(batches).toHaveLength(1);
    expect(batches[0][0]).toEqual([
      ['installations'],
      ['platform', 'dashboard'],
      ['nodes'],
    ]);
  });

  it('到期后重开新窗口', () => {
    const flush = vi.fn();
    const push = createInvalidationBatcher(flush);

    push([['nodes']]);
    vi.advanceTimersByTime(250);
    push([['nodes']]);
    vi.advanceTimersByTime(250);
    expect(flush).toHaveBeenCalledTimes(2);
  });

  it('空列表不启动窗口', () => {
    const flush = vi.fn();
    const push = createInvalidationBatcher(flush);
    push([]);
    vi.advanceTimersByTime(1_000);
    expect(flush).not.toHaveBeenCalled();
  });
});

describe('重连策略', () => {
  it('退避封顶 60 秒且单调不减', () => {
    const delays = Array.from({ length: 8 }, (_, attempt) => adminHubBackoffDelayMs(attempt));
    expect(delays).toEqual([1_000, 2_000, 5_000, 10_000, 30_000, 60_000, 60_000, 60_000]);
    expect(adminHubBackoffDelayMs(-3)).toBe(1_000);
  });

  it('重连对账口径为全量', () => {
    expect(resolveReconnectReconcile()).toBe('all');
  });
});
