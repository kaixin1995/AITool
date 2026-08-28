import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'path'

// 后端地址：默认 Admin 宿主 5030（生产同进程时走相对路径无需配置），开发时可用 VITE_API_TARGET 覆盖。
const apiTarget = process.env.VITE_API_TARGET || 'http://127.0.0.1:5030'
// 双宿主：/v1 代理端点在 Core 宿主（默认 5029），开发时可用 VITE_CORE_TARGET 覆盖。
const coreTarget = process.env.VITE_CORE_TARGET || 'http://127.0.0.1:5029'

// 同进程托管：build 产物直接输出到 Admin 宿主 wwwroot，由 ASP.NET Core 提供静态文件服务。
// 开发模式下通过 proxy 把 /api 转发到 Admin、/v1 转发到 Core，避免跨域。
export default defineConfig({
  plugins: [vue()],
  // 绝对 base：多级路由（如 /system/settings）下相对路径 ./assets 会解析到错误目录，
  // 导致 JS 加载失败整页白屏。用绝对 / 确保资源路径与路由深度无关。
  base: '/',
  define: {
    // 注入版本号常量，供前端展示。默认 1.0.1（与后端大致对齐，正式发布时由 CI 覆盖）。
    __APP_VERSION__: JSON.stringify(process.env.npm_package_version ?? '1.0.0')
  },
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src')
    }
  },
  build: {
    outDir: '../src/AITool.Admin/wwwroot',
    emptyOutDir: true,
    sourcemap: false
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true
      },
      '/v1': {
        target: coreTarget,
        changeOrigin: true
      },
      '/health': {
        target: apiTarget,
        changeOrigin: true
      }
    }
  }
})
