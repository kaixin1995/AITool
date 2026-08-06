/** @type {import('tailwindcss').Config} */
export default {
  // 仅扫描 src，避免和 Naive UI 的样式冲突。
  // 重要：preflight 关闭，避免 Tailwind 的 CSS reset 覆盖 Naive UI 的基础样式。
  content: ['./index.html', './src/**/*.{vue,js,ts,jsx,tsx}'],
  corePlugins: {
    preflight: false
  },
  theme: {
    extend: {}
  },
  plugins: []
}
