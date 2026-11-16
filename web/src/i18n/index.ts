import dayjs from 'dayjs';
import 'dayjs/locale/zh-cn';
import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import { authEn } from './locales/en-US/auth';
import { commonEn } from './locales/en-US/common';
import { navEn } from './locales/en-US/nav';
import { permissionsEn } from './locales/en-US/permissions';
import { uiEn } from './locales/en-US/ui';
import { authZh } from './locales/zh-CN/auth';
import { commonZh } from './locales/zh-CN/common';
import { navZh } from './locales/zh-CN/nav';
import { permissionsZh } from './locales/zh-CN/permissions';
import { uiZh } from './locales/zh-CN/ui';

export const SUPPORTED_LOCALES = [
  { key: 'zh-CN', label: '中文' },
  { key: 'en-US', label: 'English' },
] as const;

export type Locale = (typeof SUPPORTED_LOCALES)[number]['key'];

export const DEFAULT_LOCALE: Locale = 'zh-CN';

const STORAGE_KEY = 'appframework.locale';

export const NAMESPACES = ['common', 'ui', 'nav', 'auth', 'permissions'] as const;

function flattenResource(source: Record<string, unknown>, prefix = ''): Record<string, string> {
  const flat: Record<string, string> = {};
  for (const [name, value] of Object.entries(source)) {
    const key = prefix ? `${prefix}.${name}` : name;
    if (value !== null && typeof value === 'object') {
      Object.assign(flat, flattenResource(value as Record<string, unknown>, key));
    } else {
      flat[key] = String(value);
    }
  }
  return flat;
}

const nestedResources = {
  'zh-CN': {
    common: commonZh,
    ui: uiZh,
    nav: navZh,
    auth: authZh,
    permissions: permissionsZh,
  },
  'en-US': {
    common: commonEn,
    ui: uiEn,
    nav: navEn,
    auth: authEn,
    permissions: permissionsEn,
  },
} as const;

const resources = Object.fromEntries(
  Object.entries(nestedResources).map(([locale, namespaces]) => [
    locale,
    Object.fromEntries(
      Object.entries(namespaces).map(([namespace, values]) => [
        namespace,
        flattenResource(values as Record<string, unknown>),
      ]),
    ),
  ]),
);

export function isLocale(value: string | null | undefined): value is Locale {
  return SUPPORTED_LOCALES.some((item) => item.key === value);
}

function detectLocale(): Locale {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (isLocale(stored)) {
      return stored;
    }
  } catch {
    // 隐私模式下读不到 localStorage，回退到浏览器语言
  }

  const preferred = typeof navigator === 'undefined' ? '' : navigator.language;
  return preferred.toLowerCase().startsWith('en') ? 'en-US' : DEFAULT_LOCALE;
}

function applyLocale(locale: string): void {
  dayjs.locale(locale === 'en-US' ? 'en' : 'zh-cn');
  if (typeof document !== 'undefined') {
    document.documentElement.lang = locale;
  }
}

void i18n.use(initReactI18next).init({
  resources,
  lng: detectLocale(),
  fallbackLng: DEFAULT_LOCALE,
  supportedLngs: SUPPORTED_LOCALES.map((item) => item.key),
  defaultNS: 'common',
  ns: [...NAMESPACES],
  interpolation: { escapeValue: false },
  keySeparator: false,
  initImmediate: false,
  returnNull: false,
});

applyLocale(i18n.language);
i18n.on('languageChanged', applyLocale);

/** 当前语言（永远落在支持列表内）。 */
export function currentLocale(): Locale {
  return isLocale(i18n.language) ? i18n.language : DEFAULT_LOCALE;
}

/** 切换界面语言；选择写入 localStorage，下次打开沿用。 */
export function setLocale(locale: Locale): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, locale);
  } catch {
    // 写不进去只影响持久化，本次切换照常生效
  }
  void i18n.changeLanguage(locale);
}

export default i18n;
