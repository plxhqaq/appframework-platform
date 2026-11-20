import { request } from './client';
import { idempotencyKey } from './idempotency';
import type {
  CreateLicenseProductRequest,
  IssueLicenseRequest,
  IssueOfflineLicenseCodeRequest,
  LicenseEntitlement,
  LicenseProduct,
  LicenseSigningKeyStatus,
  OfflineLicenseCode,
  PageResult,
  RenewLicenseRequest,
  RevokeLicenseRequest,
  UpdateLicenseProductRequest,
} from './types';

/**
 * 授权（`/api/v1/admin/licensing`）。
 *
 * 应用隔离轴来自令牌上下文，前端不传 appKey；创建类接口带 `Idempotency-Key`。
 * 签名密钥是全局资源（公钥不是秘密，客户端匿名发现），不受应用上下文限制。
 */
export interface EntitlementListQuery {
  page?: number;
  pageSize?: number;
  productKey?: string;
  status?: string;
  keyword?: string;
}

export interface OfflineCodeListQuery {
  page?: number;
  pageSize?: number;
  keyword?: string;
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

export const licensingApi = {
  listProducts: (signal?: AbortSignal) =>
    request<LicenseProduct[]>('/api/v1/admin/licensing/products', { signal }),

  createProduct: (body: CreateLicenseProductRequest) =>
    request<LicenseProduct>('/api/v1/admin/licensing/products', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateProduct: (id: string, body: UpdateLicenseProductRequest) =>
    request<LicenseProduct>(`/api/v1/admin/licensing/products/${id}`, {
      method: 'PATCH',
      body,
    }),

  /** 删除授权产品；仍有授权行或离线码台账时服务端拒绝。 */
  deleteProduct: (id: string) =>
    request<boolean>(`/api/v1/admin/licensing/products/${id}`, { method: 'DELETE' }),

  listEntitlements: (query: EntitlementListQuery, signal?: AbortSignal) =>
    request<PageResult<LicenseEntitlement>>(
      `/api/v1/admin/licensing/entitlements${toQueryString({ ...query })}`,
      { signal },
    ),

  issueLicense: (body: IssueLicenseRequest) =>
    request<LicenseEntitlement>('/api/v1/admin/licensing/entitlements', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  renewLicense: (id: string, body: RenewLicenseRequest) =>
    request<LicenseEntitlement>(`/api/v1/admin/licensing/entitlements/${id}/renew`, {
      method: 'POST',
      body,
    }),

  revokeLicense: (id: string, body: RevokeLicenseRequest) =>
    request<LicenseEntitlement>(`/api/v1/admin/licensing/entitlements/${id}/revoke`, {
      method: 'POST',
      body,
    }),

  listOfflineCodes: (query: OfflineCodeListQuery, signal?: AbortSignal) =>
    request<PageResult<OfflineLicenseCode>>(
      `/api/v1/admin/licensing/offline-codes${toQueryString({ ...query })}`,
      { signal },
    ),

  issueOfflineCode: (body: IssueOfflineLicenseCodeRequest) =>
    request<OfflineLicenseCode>('/api/v1/admin/licensing/offline-codes', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  deleteOfflineCode: (id: string) =>
    request<null>(`/api/v1/admin/licensing/offline-codes/${id}`, { method: 'DELETE' }),

  getSigningKeys: (signal?: AbortSignal) =>
    request<LicenseSigningKeyStatus>('/api/v1/admin/licensing/signing-keys', { signal }),

  syncSigningKeys: () =>
    request<LicenseSigningKeyStatus>('/api/v1/admin/licensing/signing-keys/sync', {
      method: 'POST',
    }),

  activateSigningKey: (id: string) =>
    request<LicenseSigningKeyStatus>(`/api/v1/admin/licensing/signing-keys/${id}/activate`, {
      method: 'POST',
    }),

  retireSigningKey: (id: string) =>
    request<LicenseSigningKeyStatus>(`/api/v1/admin/licensing/signing-keys/${id}/retire`, {
      method: 'POST',
    }),
};
