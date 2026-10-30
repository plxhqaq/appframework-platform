#!/usr/bin/env node
/**
 * 设计令牌生成器：design/tokens.json → web/src/styles/antd-theme.ts
 *
 * 为什么要有这一步：计划里"token 单一源 + CI 哈希断言"是防止两套前端风格漂移的硬机制。
 * 生成物顶部写入源文件的 sha256，`--check` 会重新计算并逐字节比对生成结果，
 * 任何手改生成物或漏跑生成器都会让 CI 直接失败。
 *
 * 用法：
 *   node tools/gen-tokens.mjs            # 生成（覆盖）
 *   node tools/gen-tokens.mjs --check    # 只校验，不一致则退出码 1
 */
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const sourcePath = resolve(repoRoot, 'design/tokens.json');
const targetPath = resolve(repoRoot, 'web/src/styles/antd-theme.ts');

const checkOnly = process.argv.includes('--check');

const rawSource = readFileSync(sourcePath, 'utf8');
const hash = createHash('sha256').update(rawSource).digest('hex');
const tokens = JSON.parse(rawSource);

const generated = render(tokens, hash);

if (checkOnly) {
  let current;
  try {
    current = readFileSync(targetPath, 'utf8');
  } catch {
    fail(`未找到生成物 ${relative(repoRoot, targetPath)}，请先执行 node tools/gen-tokens.mjs`);
  }
  if (current !== generated) {
    fail(
      [
        `生成物 ${relative(repoRoot, targetPath)} 与 design/tokens.json 不一致（源 sha256=${hash}）。`,
        '  · 如果改了 tokens.json：执行 node tools/gen-tokens.mjs 重新生成并提交。',
        '  · 如果手改了生成物：改回 tokens.json，再重新生成。',
      ].join('\n'),
    );
  }
  console.log(`tokens 一致性校验通过（sha256=${hash}）`);
  process.exit(0);
}

mkdirSync(dirname(targetPath), { recursive: true });
writeFileSync(targetPath, generated, 'utf8');
console.log(`已生成 ${relative(repoRoot, targetPath)}（sha256=${hash}）`);

function fail(message) {
  console.error(message);
  process.exit(1);
}

function render(t, sourceHash) {
  const modes = t.modes;
  const families = t.families;

  const lines = [];
  lines.push('/* eslint-disable */');
  lines.push('// ─────────────────────────────────────────────────────────────');
  lines.push('// 本文件由 tools/gen-tokens.mjs 自动生成，请勿手工修改。');
  lines.push('// 唯一源：design/tokens.json');
  lines.push(`// 源 sha256：${sourceHash}`);
  lines.push('// 修改色板/圆角/字号请改 tokens.json 后重新生成。');
  lines.push('// ─────────────────────────────────────────────────────────────');
  lines.push("import type { ThemeConfig } from 'antd';");
  lines.push('');
  lines.push(`export const TOKENS_SHA256 = '${sourceHash}';`);
  lines.push('');
  lines.push(`export type ThemeMode = ${modes.map((m) => literal(m.key)).join(' | ')};`);
  lines.push(`export type ThemeFamily = ${families.map((f) => literal(f.key)).join(' | ')};`);
  lines.push('');
  lines.push('/** 全部模式（浅色 / 深色 / 深黑），顺序即切换器的展示顺序。 */');
  lines.push('export const THEME_MODES = [');
  for (const mode of modes) {
    lines.push(
      `  { key: ${literal(mode.key)}, label: ${literal(mode.label)}, dark: ${mode.antdAlgorithm === 'dark'} },`,
    );
  }
  lines.push('] as const satisfies ReadonlyArray<{ key: ThemeMode; label: string; dark: boolean }>;');
  lines.push('');
  lines.push('/** 全部色系（8 族），与 Avalonia 客户端的 ThemeTypes 家族一一对应。 */');
  lines.push('export const THEME_FAMILIES = [');
  for (const family of families) {
    lines.push(
      `  { key: ${literal(family.key)}, label: ${literal(family.label)}, colorName: ${literal(family.colorName)} },`,
    );
  }
  lines.push('] as const satisfies ReadonlyArray<{ key: ThemeFamily; label: string; colorName: string }>;');
  lines.push('');
  lines.push('/** 与色系/模式无关的公共形状、字体、动效令牌。 */');
  lines.push(
    `export const SHARED_TOKENS = ${JSON.stringify(
      {
        shape: t.shape,
        typography: t.typography,
        motion: t.motion,
      },
      null,
      2,
    ).replace(/\n/g, '\n')} as const;`,
  );
  lines.push('');
  lines.push('/** 间距刻度：布局与卡片内边距统一从这里取值。 */');
  lines.push(`export const SPACING = ${JSON.stringify(t.spacing, null, 2)} as const;`);
  lines.push('');
  lines.push('/** 24 套主题的 token 覆盖表：[色系][模式] → antd ThemeConfig.token。 */');
  lines.push('const PALETTES: Record<ThemeFamily, Record<ThemeMode, ThemeConfig[\'token\']>> = {');
  for (const family of families) {
    lines.push(`  ${propertyKey(family.key)}: {`);
    for (const mode of modes) {
      const neutral = t.neutrals[mode.key];
      const merged = {
        colorPrimary: family.seeds[mode.key],
        colorInfo: family.seeds[mode.key],
        colorLink: family.seeds[mode.key],
        ...t.semantic,
        ...neutral,
        ...t.shape,
        ...t.typography,
        ...t.motion,
      };
      lines.push(`    ${propertyKey(mode.key)}: ${JSON.stringify(merged, null, 6).replace(/\n/g, '\n    ')},`);
    }
    lines.push('  },');
  }
  lines.push('};');
  lines.push('');
  lines.push('/** 默认主题：海洋蓝 + 浅色。 */');
  lines.push(`export const DEFAULT_THEME = { family: 'blue' as ThemeFamily, mode: 'light' as ThemeMode };`);
  lines.push('');
  lines.push('/** 取某色系某模式的 token 覆盖（配合 ConfigProvider 的 theme.token）。 */');
  lines.push('export function getThemeTokens(family: ThemeFamily, mode: ThemeMode): ThemeConfig[\'token\'] {');
  lines.push('  return PALETTES[family][mode];');
  lines.push('}');
  lines.push('');
  lines.push('/** 某模式是否为暗色（决定 ConfigProvider 用 darkAlgorithm 还是 defaultAlgorithm）。 */');
  lines.push('export function isDarkMode(mode: ThemeMode): boolean {');
  lines.push('  return mode === \'dark\' || mode === \'deepdark\';');
  lines.push('}');
  lines.push('');
  lines.push('export function isThemeMode(value: string): value is ThemeMode {');
  lines.push('  return Object.prototype.hasOwnProperty.call(PALETTES[DEFAULT_THEME.family], value);');
  lines.push('}');
  lines.push('');
  lines.push('export function isThemeFamily(value: string): value is ThemeFamily {');
  lines.push('  return Object.prototype.hasOwnProperty.call(PALETTES, value);');
  lines.push('}');
  lines.push('');
  return lines.join('\n');
}

function literal(value) {
  return `'${String(value).replace(/'/g, "\\'")}'`;
}

function propertyKey(value) {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(value) ? value : literal(value);
}
