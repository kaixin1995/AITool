/// <reference types="vite/client" />

// vite.config.ts 中 define 注入的版本号常量。
declare const __APP_VERSION__: string

// Vue SFC 类型声明。
declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>
  export default component
}
