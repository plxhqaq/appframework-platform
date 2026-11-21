import { request } from './client';
import { idempotencyKey } from './idempotency';
import type {
  ApproveQrSessionRequest,
  CreateRoleRequest,
  CreateUserRequest,
  IssueSingleLoginKeyRequest,
  PageResult,
  PermissionDescriptor,
  QrSessionSummary,
  ResetUserPasswordRequest,
  RoleSummary,
  SessionSummary,
  SingleLoginKey,
  UpdateRoleRequest,
  UpdateUserRequest,
  UserDetail,
  UserSummary,
} from './types';

/**
 * 身份管理（`/api/v1/admin/identity`）。
 *
 * 用户/角色创建都带 `Idempotency-Key`：网络重试不能造出两个同名账号或角色。
 */
export interface UserListQuery {
  page?: number;
  pageSize?: number;
  keyword?: string;
  enabled?: boolean;
}

export interface SessionListQuery {
  page?: number;
  pageSize?: number;
  /** 用户名（模糊匹配）。 */
  keyword?: string;
  /** 仅有效（true）/ 仅已失效（false）；缺省不过滤。 */
  isActive?: boolean;
}

export interface QrSessionListQuery {
  page?: number;
  pageSize?: number;
  /** pending / expired / approved / rejected；空表示全部。 */
  status?: string;
}

function toQueryString(params: Record<string, string | number | boolean | undefined>): string {
  const search = new URLSearchParams();
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  });
  const text = search.toString();
  return text ? `?${text}` : '';
}

export const identityApi = {
  listUsers: (query: UserListQuery, signal?: AbortSignal) =>
    request<PageResult<UserSummary>>(`/api/v1/admin/identity/users${toQueryString({ ...query })}`, {
      signal,
    }),

  getUser: (id: string) => request<UserDetail>(`/api/v1/admin/identity/users/${id}`),

  createUser: (body: CreateUserRequest) =>
    request<UserDetail>('/api/v1/admin/identity/users', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateUser: (id: string, body: UpdateUserRequest) =>
    request<UserDetail>(`/api/v1/admin/identity/users/${id}`, { method: 'PATCH', body }),

  deleteUser: (id: string) =>
    request<void>(`/api/v1/admin/identity/users/${id}`, { method: 'DELETE' }),

  resetPassword: (id: string, body: ResetUserPasswordRequest) =>
    request<{ revokedSessions: number }>(`/api/v1/admin/identity/users/${id}/password/reset`, {
      method: 'POST',
      body,
    }),

  listRoles: (signal?: AbortSignal) =>
    request<RoleSummary[]>('/api/v1/admin/identity/roles', { signal }),

  createRole: (body: CreateRoleRequest) =>
    request<RoleSummary>('/api/v1/admin/identity/roles', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateRole: (id: string, body: UpdateRoleRequest) =>
    request<RoleSummary>(`/api/v1/admin/identity/roles/${id}`, { method: 'PATCH', body }),

  deleteRole: (id: string) =>
    request<void>(`/api/v1/admin/identity/roles/${id}`, { method: 'DELETE' }),

  listSessions: (query: SessionListQuery, signal?: AbortSignal) =>
    request<PageResult<SessionSummary>>(
      `/api/v1/admin/identity/sessions${toQueryString({ ...query })}`,
      { signal },
    ),

  listUserSessions: (userId: string, signal?: AbortSignal) =>
    request<SessionSummary[]>(`/api/v1/admin/identity/users/${userId}/sessions`, { signal }),

  revokeSession: (id: string) =>
    request<SessionSummary>(`/api/v1/admin/identity/sessions/${id}/revoke`, { method: 'POST' }),

  revokeUserSessions: (userId: string) =>
    request<{ revokedSessions: number }>(`/api/v1/admin/identity/users/${userId}/sessions/revoke`, {
      method: 'POST',
    }),

  /** 签发 6 位单次密钥；响应里的明文只出现这一次。 */
  issueSingleLoginKey: (body: IssueSingleLoginKeyRequest) =>
    request<SingleLoginKey>('/api/v1/admin/identity/singlekeys', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  listQrSessions: (query: QrSessionListQuery, signal?: AbortSignal) =>
    request<PageResult<QrSessionSummary>>(
      `/api/v1/admin/identity/qr/sessions${toQueryString({ ...query })}`,
      { signal },
    ),

  /** 手机扫码直达审批页时按 id 取单个会话，不用先拉整页列表。 */
  getQrSession: (requestId: string, signal?: AbortSignal) =>
    request<QrSessionSummary>(`/api/v1/admin/identity/qr/sessions/${requestId}`, { signal }),

  approveQrSession: (requestId: string, body: ApproveQrSessionRequest) =>
    request<QrSessionSummary>(`/api/v1/admin/identity/qr/sessions/${requestId}/approve`, {
      method: 'POST',
      body,
    }),

  rejectQrSession: (requestId: string) =>
    request<QrSessionSummary>(`/api/v1/admin/identity/qr/sessions/${requestId}/reject`, {
      method: 'POST',
    }),

  permissions: (signal?: AbortSignal, appKey?: string | null) =>
    request<PermissionDescriptor[]>(
      appKey
        ? `/api/v1/platform/permissions?appKey=${encodeURIComponent(appKey)}`
        : '/api/v1/platform/permissions',
      { anonymous: true, signal },
    ),
};
