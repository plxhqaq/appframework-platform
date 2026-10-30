import { request } from './client';
import type {
  ClientDocumentManifest,
  CreateDocumentCollectionRequest,
  DocumentCollection,
  DocumentDetail,
  DocumentSummary,
  PageResult,
  UpdateDocumentCollectionRequest,
  UpdateDocumentRequest,
} from './types';

/**
 * 文档管理（`/api/v1/admin/documents`）。
 *
 * 可见性由服务端强制：客户端清单只返回 visibility=client 且集合启用的文档，
 * 前端隐藏只是体验，不构成安全边界。
 */
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

export interface DocumentListQuery {
  page?: number;
  pageSize?: number;
  collectionKey?: string;
  keyword?: string;
}

export const documentsApi = {
  listCollections: (signal?: AbortSignal) =>
    request<DocumentCollection[]>('/api/v1/admin/documents/collections', { signal }),

  createCollection: (body: CreateDocumentCollectionRequest) =>
    request<DocumentCollection>('/api/v1/admin/documents/collections', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateCollection: (id: string, body: UpdateDocumentCollectionRequest) =>
    request<DocumentCollection>(`/api/v1/admin/documents/collections/${id}`, {
      method: 'PATCH',
      body,
    }),

  listDocuments: (query: DocumentListQuery, signal?: AbortSignal) =>
    request<PageResult<DocumentSummary>>(
      `/api/v1/admin/documents${toQueryString({ ...query })}`,
      { signal },
    ),

  getDocument: (id: string, signal?: AbortSignal) =>
    request<DocumentDetail>(`/api/v1/admin/documents/${id}`, { signal }),

  /** 新文档上传：multipart/form-data，字段 collectionKey/title/visibility + file。 */
  uploadDocument: (collectionKey: string, file: File, title?: string, visibility?: string) => {
    const form = new FormData();
    form.append('collectionKey', collectionKey);
    if (title) {
      form.append('title', title);
    }
    if (visibility) {
      form.append('visibility', visibility);
    }
    form.append('file', file);
    return request<DocumentDetail>('/api/v1/admin/documents', {
      method: 'POST',
      formData: form,
      headers: { 'Idempotency-Key': idempotencyKey() },
    });
  },

  /** 追加修订：旧修订保留可追溯。 */
  addRevision: (id: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<DocumentDetail>(`/api/v1/admin/documents/${id}/revisions`, {
      method: 'POST',
      formData: form,
    });
  },

  updateDocument: (id: string, body: UpdateDocumentRequest) =>
    request<DocumentDetail>(`/api/v1/admin/documents/${id}`, { method: 'PATCH', body }),

  deleteDocument: (id: string) =>
    request<DocumentSummary>(`/api/v1/admin/documents/${id}`, { method: 'DELETE' }),

  getClientManifest: (signal?: AbortSignal) =>
    request<ClientDocumentManifest>('/api/v1/client/documents/manifest', { signal }),
};
