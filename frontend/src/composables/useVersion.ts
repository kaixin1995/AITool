// 前端版本号。与后端 AppVersionInfo 对齐，构建时可通过环境变量覆盖。
// 这里用静态常量；如需动态从后端获取，可改为 API 调用。
export const version = __APP_VERSION__

// 占位导出，保留与后端 AppVersionInfo 类对应的命名。
export const AppVersion = { value: version }
