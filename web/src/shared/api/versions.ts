import { request, uploadWithProgress } from './client';
import { idempotencyKey } from './idempotency';
import type {
  CreatePackageBundleRequest,
  CreateReleaseChannelRequest,
  CreateReleaseRequest,
  CreateUploadTicketRequest,
  DownloadTicket,
  DriverMedia,
  PageResult,
  RegisterDriverMediaRequest,
  RegisterReleasePackageRequest,
  RegisterUpdaterRequest,
  ReleaseChannel,
  ReleaseDetail,
  ReleasePackage,
  ReleaseSummary,
  UpdateReleaseChannelRequest,
  UpdateReleaseRequest,
  UpdaterInfo,
  UploadResult,
  UploadTicket,
} from './types';

/**
 * 版本与渠道（`/api/v1/admin/versions`）。
 *
 * 应用隔离轴来自访问令牌的应用上下文（`/admin/auth/context` 切换），
 * 前端不传 appKey；创建类接口都带 `Idempotency-Key`。
 */
export interface ReleaseListQuery {
  page?: number;
  pageSize?: number;
  channelKey?: string;
  status?: string;
}

export interface ReleasePackageListQuery {
  page?: number;
  pageSize?: number;
  channelKey?: string;
  kind?: string;
  status?: string;
}

export interface UpdaterListQuery {
  page?: number;
  pageSize?: number;
  target?: string;
  status?: string;
}

export interface DriverMediaListQuery {
  page?: number;
  pageSize?: number;
  mediaKey?: string;
  activeOnly?: boolean;
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

  /** 删除渠道；仍登记任何版本（含草稿）时服务端拒绝。 */
  deleteChannel: (id: string) =>
    request<boolean>(`/api/v1/admin/versions/channels/${id}`, { method: 'DELETE' }),

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

  /** 编辑草稿版本的发布说明（发布前确认用）。 */
  updateRelease: (id: string, body: UpdateReleaseRequest) =>
    request<ReleaseDetail>(`/api/v1/admin/versions/releases/${id}`, {
      method: 'PATCH',
      body,
    }),

  archiveRelease: (id: string) =>
    request<ReleaseDetail>(`/api/v1/admin/versions/releases/${id}/archive`, { method: 'POST' }),

  /** 删除版本（草稿丢弃 / 归档彻底删除并回收无引用内容）；已发布版本须先归档。 */
  deleteRelease: (id: string) =>
    request<ReleaseDetail>(`/api/v1/admin/versions/releases/${id}`, { method: 'DELETE' }),

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

  /** 带进度的票据上传；大安装包需要进度反馈。 */
  uploadWithProgress: (
    ticket: UploadTicket,
    file: Blob,
    onProgress?: (loaded: number, total: number) => void,
    signal?: AbortSignal,
  ) =>
    uploadWithProgress<UploadResult>(ticket.uploadUrl, file, { onProgress, signal }),

  listPackages: (query: ReleasePackageListQuery, signal?: AbortSignal) =>
    request<PageResult<ReleasePackage>>(
      `/api/v1/admin/versions/packages${toQueryString({ ...query })}`,
      { signal },
    ),

  getPackage: (id: string, signal?: AbortSignal) =>
    request<ReleasePackage>(`/api/v1/admin/versions/packages/${id}`, { signal }),

  /** 登记发布包；内容须先经上传票据落盘，清单为空时服务端尝试从包内探测。 */
  registerPackage: (body: RegisterReleasePackageRequest) =>
    request<ReleasePackage>('/api/v1/admin/versions/packages', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  /** 删除包登记（仅草稿版本）；已发布版本须先归档。 */
  deletePackage: (id: string) =>
    request<ReleasePackage>(`/api/v1/admin/versions/packages/${id}`, { method: 'DELETE' }),

  /** 重试失败的增量包摄取（failed → pending；其他状态由服务端拒绝）。 */
  retryPackage: (id: string) =>
    request<ReleasePackage>(`/api/v1/admin/versions/packages/${id}/retry`, { method: 'POST' }),

  activatePackage: (id: string) =>
    request<ReleasePackage>(`/api/v1/admin/versions/packages/${id}/activate`, { method: 'POST' }),

  listUpdaters: (query: UpdaterListQuery, signal?: AbortSignal) =>
    request<PageResult<UpdaterInfo>>(
      `/api/v1/admin/versions/updaters${toQueryString({ ...query })}`,
      { signal },
    ),

  /** 登记更新器；内容须先经上传票据落盘。 */
  registerUpdater: (body: RegisterUpdaterRequest) =>
    request<UpdaterInfo>('/api/v1/admin/versions/updaters', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  archiveUpdater: (id: string) =>
    request<UpdaterInfo>(`/api/v1/admin/versions/updaters/${id}/archive`, { method: 'POST' }),

  /** 彻底删除已归档更新器（已发布拒绝）。 */
  deleteUpdater: (id: string) =>
    request<UpdaterInfo>(`/api/v1/admin/versions/updaters/${id}`, { method: 'DELETE' }),

  /** 某运行时下客户端实际会拿到的更新器（同 RID 优先、再回退 any）。 */
  latestUpdater: (runtimeIdentifier: string, signal?: AbortSignal) =>
    request<UpdaterInfo>(
      `/api/v1/client/versions/updater${toQueryString({ runtimeIdentifier })}`,
      { signal },
    ),

  /** 管理台下载安装包：签发限时票据，之后由浏览器原生下载（非 zip 自动打包）。 */
  createPackageDownloadTicket: (id: string) =>
    request<DownloadTicket>(`/api/v1/client/versions/packages/${id}/download-ticket`, {
      method: 'POST',
    }),

  /** 管理台下载更新器：同上。 */
  createUpdaterDownloadTicket: (id: string) =>
    request<DownloadTicket>(`/api/v1/client/versions/updaters/${id}/download-ticket`, {
      method: 'POST',
    }),

  listMedia: (query: DriverMediaListQuery, signal?: AbortSignal) =>
    request<PageResult<DriverMedia>>(
      `/api/v1/admin/versions/media${toQueryString({
        page: query.page,
        pageSize: query.pageSize,
        mediaKey: query.mediaKey,
        activeOnly: query.activeOnly === undefined ? undefined : String(query.activeOnly),
      })}`,
      { signal },
    ),

  getMedia: (id: string, signal?: AbortSignal) =>
    request<DriverMedia>(`/api/v1/admin/versions/media/${id}`, { signal }),

  /** 登记驱动媒体包；内容须先经上传票据落盘。key/版本留空由服务端从包内 af-media.json 取。 */
  registerMedia: (body: RegisterDriverMediaRequest) =>
    request<DriverMedia>('/api/v1/admin/versions/media', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  /** 启用该媒体版本（同 mediaKey 其余版本自动停用）。 */
  activateMedia: (id: string) =>
    request<DriverMedia>(`/api/v1/admin/versions/media/${id}/activate`, { method: 'POST' }),

  /** 停用该媒体版本；不提升同键其它版本，该媒体键可无启用版本。 */
  deactivateMedia: (id: string) =>
    request<DriverMedia>(`/api/v1/admin/versions/media/${id}/deactivate`, { method: 'POST' }),

  deleteMedia: (id: string) =>
    request<DriverMedia>(`/api/v1/admin/versions/media/${id}`, { method: 'DELETE' }),

  /** 管理台下载单个媒体包：限时票据，浏览器原生下载。 */
  createMediaDownloadTicket: (id: string) =>
    request<DownloadTicket>(`/api/v1/client/versions/media/${id}/download-ticket`, {
      method: 'POST',
    }),

  /** 完整包组合下载票据：全量包 + 所选媒体合成确定性 zip；首次组合服务端同步打包。 */
  createBundleTicket: (packageId: string, body: CreatePackageBundleRequest) =>
    request<DownloadTicket>(`/api/v1/client/versions/packages/${packageId}/bundle-ticket`, {
      method: 'POST',
      body,
    }),
};
