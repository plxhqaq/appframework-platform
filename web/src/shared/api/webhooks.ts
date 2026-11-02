import { request } from './client';
import type {
  CreateWebhookSubscriptionRequest,
  CreatedWebhookSubscription,
  PageResult,
  RotatedWebhookSecret,
  UpdateWebhookSubscriptionRequest,
  WebhookDelivery,
  WebhookSubscription,
  WebhookTestResult,
} from './types';

/**
 * Webhook 订阅与投递台账（`/api/v1/admin/webhooks`）。
 *
 * 应用隔离轴来自令牌上下文，前端不传 appKey；订阅密钥只在创建/轮换的响应里出现一次。
 * `WEBHOOK_EVENT_TYPES` 手写镜像 `WebhookConventions.EventTypes`（契约未稳定，与管理台其余类型同一口径），
 * 服务端仍以自身白名单为准：不在名单里的事件在创建时就返回校验失败。
 */
export const WEBHOOK_EVENT_TYPES = [
  { value: 'version.released', label: '版本发布' },
  { value: 'license.revoked', label: '授权吊销' },
  { value: 'backup.pushed', label: '备份下发' },
  { value: 'document.uploaded', label: '文档上传' },
  { value: 'document.deleted', label: '文档删除' },
  { value: 'webhook.test', label: '订阅测试（手动触发）' },
] as const;

/** 单条订阅的事件数上限（与 `WebhookConventions.MaxEventTypes` 一致）。 */
export const WEBHOOK_MAX_EVENT_TYPES = 20;

function idempotencyKey(): string {
  return crypto.randomUUID();
}

function toQueryString(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  });
  const text = search.toString();
  return text ? `?${text}` : '';
}

export interface WebhookDeliveryQuery {
  page?: number;
  pageSize?: number;
  /** 留空表示当前应用的全部订阅；台账只支持这一个过滤维度。 */
  subscriptionId?: string;
}

export const webhookApi = {
  listSubscriptions: (signal?: AbortSignal) =>
    request<WebhookSubscription[]>('/api/v1/admin/webhooks', { signal }),

  createSubscription: (body: CreateWebhookSubscriptionRequest) =>
    request<CreatedWebhookSubscription>('/api/v1/admin/webhooks', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateSubscription: (id: string, body: UpdateWebhookSubscriptionRequest) =>
    request<WebhookSubscription>(`/api/v1/admin/webhooks/${id}`, {
      method: 'PATCH',
      body,
    }),

  deleteSubscription: (id: string) =>
    request<{ id: string }>(`/api/v1/admin/webhooks/${id}`, { method: 'DELETE' }),

  rotateSecret: (id: string) =>
    request<RotatedWebhookSecret>(`/api/v1/admin/webhooks/${id}/secret/rotate`, {
      method: 'POST',
    }),

  sendTest: (id: string) =>
    request<WebhookTestResult>(`/api/v1/admin/webhooks/${id}/test`, { method: 'POST' }),

  listDeliveries: (query: WebhookDeliveryQuery, signal?: AbortSignal) =>
    request<PageResult<WebhookDelivery>>(
      `/api/v1/admin/webhooks/deliveries${toQueryString({ ...query })}`,
      { signal },
    ),

  redeliver: (deliveryId: string) =>
    request<WebhookDelivery>(`/api/v1/admin/webhooks/deliveries/${deliveryId}/redeliver`, {
      method: 'POST',
    }),
};
