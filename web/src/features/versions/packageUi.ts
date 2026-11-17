import { tr } from '../../i18n/translate';
import type {
  PackageManifestSource,
  RegisterReleasePackageRequest,
  ReleasePackageKind,
  ReleasePackageStatus,
} from '../../shared/api/types';

/** 包类型展示口径；颜色沿用 Ant Design Tag 预设。 */
export const PACKAGE_KIND_META: Record<ReleasePackageKind, { label: string; color?: string }> = {
  full: { get label() { return tr("全量安装包"); }, color: 'blue' },
  incremental: { get label() { return tr("增量包"); }, color: 'purple' },
};

/** 摄取状态；pending 只有增量包会出现（等待 B9 摄取），全量包登记即可用。 */
export const PACKAGE_STATUS_META: Record<ReleasePackageStatus, { label: string; color?: string }> = {
  pending: { get label() { return tr("待摄取"); } },
  processing: { get label() { return tr("摄取中"); }, color: 'processing' },
  ready: { get label() { return tr("可用"); }, color: 'green' },
  failed: { get label() { return tr("失败"); }, color: 'red' },
};

/** 清单来源：便于核对服务端探测到的是哪一种协议。 */
export const MANIFEST_SOURCE_LABEL: Record<PackageManifestSource, string> = {
  get embedded() { return tr("PE 内嵌"); },
  get zip() { return tr("包内 JSON"); },
  get sidecar() { return tr("旁挂 JSON"); },
  get manual() { return tr("人工登记"); },
  get legacy() { return tr("旧版协议"); },
};

export function packageKindMeta(kind: string): { label: string; color?: string } {
  return PACKAGE_KIND_META[kind as ReleasePackageKind] ?? { label: kind };
}

export function packageStatusMeta(status: string): { label: string; color?: string } {
  return PACKAGE_STATUS_META[status as ReleasePackageStatus] ?? { label: status };
}

export function manifestSourceLabel(source: string): string {
  return MANIFEST_SOURCE_LABEL[source as PackageManifestSource] ?? source;
}

const KNOWN_FORMATS = ['exe', 'msi', 'msp', 'zip', '7z', 'bin'] as const;

/** 按扩展名推断包格式；服务端仍会独立校验，这里只用于表单预览。 */
export function guessPackageFormat(fileName: string): string | null {
  const dot = fileName.lastIndexOf('.');
  if (dot < 0) {
    return null;
  }

  const extension = fileName.slice(dot + 1).toLowerCase();
  return (KNOWN_FORMATS as readonly string[]).includes(extension) ? extension : null;
}

/** 增量包必须绑定具体运行时；全量包可登记为 any。 */
export function packageTargetError(kind: ReleasePackageKind, target: string): string | null {
  return kind === 'incremental' && target === 'any'
    ? tr("增量包必须指定具体的运行时标识")
    : null;
}

export interface PackageRegistrationInput {
  sha256: string;
  sizeBytes: number;
  fileName: string;
  kind: ReleasePackageKind;
  target: string;
  channelKey?: string | null;
  version?: string | null;
  notes?: string | null;
  manifestSha256?: string | null;
  publish: boolean;
  canPublish: boolean;
}

/** 组装登记请求：空白字符串转 null，发布开关以账号权限为上限。 */
export function buildRegisterRequest(
  input: PackageRegistrationInput,
): RegisterReleasePackageRequest {
  return {
    sha256: input.sha256,
    sizeBytes: input.sizeBytes,
    fileName: input.fileName,
    kind: input.kind,
    target: input.target,
    channelKey: input.channelKey?.trim() || null,
    version: input.version?.trim() || null,
    manifestSha256: input.manifestSha256 ?? null,
    notes: input.notes?.trim() || null,
    publish: input.publish && input.canPublish,
  };
}
