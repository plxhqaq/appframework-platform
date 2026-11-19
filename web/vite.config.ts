import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// 同源部署是计划里的既定取舍（React 静态资源由 Platform.Api 托管或 nginx 同域），
// 所以开发期用 vite 代理把 /api 打到本机 Api，生产期不需要 CORS。
const apiTarget = process.env.PLATFORM_API_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: false,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
      // AdminHub 实时通道：开发期同样走代理，ws:true 供 WebSocket/SSE 升级。
      '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
      '/healthz': { target: apiTarget, changeOrigin: true },
      '/ready': { target: apiTarget, changeOrigin: true },
      // 侧栏底部的 Scalar 入口在开发期也要能直接打开，文档页自己再去取 /openapi。
      '/scalar': { target: apiTarget, changeOrigin: true },
      '/openapi': { target: apiTarget, changeOrigin: true },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    // antd + 图标库 + dayjs 单块约 1.3MB（gzip 后约 408KB），管理台可接受；显式分组让版本升级只影响对应 chunk 的缓存
    chunkSizeWarningLimit: 1360,
    rollupOptions: {
      output: {
        manualChunks: {
          react: ['react', 'react-dom', 'react-router-dom'],
          antd: ['antd', '@ant-design/icons'],
          query: ['@tanstack/react-query', 'zustand'],
        },
      },
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
});
