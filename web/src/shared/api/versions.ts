import { request } from './client';
import type {
  CreateReleaseChannelRequest,
  CreateReleaseRequest,
  CreateUploadTicketRequest,
  PageResult,
  ReleaseChannel,
  ReleaseDetail,
  ReleaseSummary,
  UpdateReleaseChannelRequest,
  UploadResult,
  UploadTicket,
} from './types';

/**
 * 版本与渠道（`/api/v1/admin/versions`）。
 *
 * 应用隔离轴来自访问令牌的应用上下文（`/admin/auth/context` 切换），
 * 前端不传 appKey；创建类接口都带 `Idempotency-Key`。
 */
function idempotencyKey(): string {
  return crypto.randomUUID();
}

export interface ReleaseListQuery {
  page?: number;
  pageSize?: number;
  channelKey?: string;
  status?: string;
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

export const versionsApi = {
  listChannels: (signal?: AbortSignal) =>
    request<ReleaseChannel[]>('/api/v1/admin/versions/channels', { signal }),

  createChannel: (body: CreateReleaseChannelRequest) =>
    request<ReleaseChannel>('/api/v1/admin/versions/channels', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateChannel: (id: string, body: UpdateReleaseChannelRequest) =>
    request<ReleaseChannel>(`/api/v1/admin/versions/channels/${id}`, {
      method: 'PATCH',
      body,
    }),

  listReleases: (query: ReleaseListQuery, signal?: AbortSignal) =>
    request<PageResult<ReleaseSummary>>(
      `/api/v1/admin/versions/releases${toQueryString({ ...query })}`,
      { signal },
    ),

  getRelease: (id: string, signal?: AbortSignal) =>
    request<ReleaseDetail>(`/api/v1/admin/versions/releases/${id}`, { signal }),

  createRelease: (body: CreateReleaseRequest) =>
    request<ReleaseDetail>('/api/v1/admin/versions/releases', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  publishRelease: (id: string) =>
    request<ReleaseDetail>(`/api/v1/admin/versions/releases/${id}/publish`, { method: 'POST' }),

  archiveRelease: (id: string) =>
    request<ReleaseDetail>(`/api/v1/admin/versions/releases/${id}/archive`, { method: 'POST' }),

  /** 申请一次性上传票据；先声明哈希与长度，再 PUT 字节。 */
  createUploadTicket: (body: CreateUploadTicketRequest) =>
    request<UploadTicket>('/api/v1/admin/versions/uploads', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  /** 凭票据上传文件内容（一次性；内容寻址去重）。 */
  upload: (ticket: UploadTicket, file: Blob) =>
    request<UploadResult>(ticket.uploadUrl, { method: 'PUT', rawBody: file }),
};
