/**
 * 响应式口径的纯函数部分。
 *
 * JS 侧只认 antd `md=768` / `lg=1024` 两个断点，CSS 媒体查询与之对齐；
 * 放成纯函数是为了让宽度选择、分页形态这些决策能在 node 环境单测，不依赖布局引擎。
 */
export interface BreakpointState {
  sm?: boolean;
  md?: boolean;
  lg?: boolean;
  xl?: boolean;
  xxl?: boolean;
}

export interface ResponsiveInfo {
  /** < 768：侧栏转抽屉。 */
  isMobile: boolean;
  /** 768–1023：侧栏折叠成图标栏。 */
  isTablet: boolean;
  /** ≥ 1024：216px 常驻侧栏。 */
  isDesktop: boolean;
}

export type NavLayout = 'drawer' | 'icon' | 'full';

export const SIDER_WIDTH = 216;
export const SIDER_COLLAPSED_WIDTH = 64;
export const CONTENT_MAX_WIDTH = 1320;

export function resolveResponsiveInfo(screens: BreakpointState): ResponsiveInfo {
  const md = screens.md === true;
  const lg = screens.lg === true;
  return { isMobile: !md, isTablet: md && !lg, isDesktop: lg };
}

export function resolveNavLayout(info: ResponsiveInfo): NavLayout {
  if (info.isMobile) {
    return 'drawer';
  }
  return info.isTablet ? 'icon' : 'full';
}

/** 窄屏弹窗/抽屉必须整宽；antd 的 Modal 自带 100vw 兜底，抽屉没有。 */
export function responsiveWidth(isMobile: boolean, width: number): number | string {
  return isMobile ? '100%' : width;
}

export interface ResponsivePaginationOptions {
  showTotal?: (total: number) => string;
  [key: string]: unknown;
}

/**
 * 窄屏用 simple 分页：一页一行放下，且不给触屏用户暴露 size changer。
 * showTotal 在窄屏省略，避免与 simple 模式抢宽度。
 */
export function responsivePagination<T extends ResponsivePaginationOptions>(
  isMobile: boolean,
  options: T,
): T & { simple: boolean; showSizeChanger: boolean; showTotal?: (total: number) => string } {
  return {
    ...options,
    simple: isMobile,
    showSizeChanger: !isMobile,
    showTotal: isMobile ? undefined : options.showTotal,
  };
}
