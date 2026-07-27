import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'path'

// 后端地址：默认 5029（生产同进程时走相对路径无需配置），开发时可用 VITE_API_TARGET 覆盖。
const apiTarget = process.env.VITE_API_TARGET || 'http://127.0.0.1:5029'

// 同进程托管：build 产物直接输出到后端 wwwroot，由 ASP.NET Core 提供静态文件服务。
// 开发模式下通过 proxy 把 /api 和 /v1 转发到后端，避免跨域。
export default defineConfig({
  plugins: [vue()],
  base: './',
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
    outDir: '../src/AITool.Web/wwwroot',
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
        target: apiTarget,
        changeOrigin: true
      },
      '/health': {
        target: apiTarget,
        changeOrigin: true
      }
    }
  }
})
