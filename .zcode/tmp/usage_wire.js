const fs = require('fs');
const edit = (path, oldS, newS, expect, tag) => {
  let raw = fs.readFileSync(path, 'utf8');
  const eol = raw.includes('\r\n') ? '\r\n' : '\n';
  let src = raw.replace(/\r\n/g, '\n');
  const n = src.split(oldS).length - 1;
  if (n !== expect) throw new Error('[' + tag + '] 匹配 ' + n + ': ' + oldS.slice(0, 70));
  src = src.split(oldS).join(newS);
  fs.writeFileSync(path, src.split('\n').join(eol), 'utf8');
  console.log(tag + ' OK x' + n);
};

// 1) 实体
edit("D:/Code/AI-Tool/src/AITool.Domain/Proxy/ProxyUsageLog.cs",
`    /// <summary>
    /// 本次请求链路中累计尝试的路由数量，可用于反映重试或切换次数。
    /// </summary>
    public int RetryCount { get; set; }`,
`    /// <summary>
    /// 本次请求链路中累计尝试的路由数量，可用于反映重试或切换次数。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 本次转发内部因 429 速率限制实际重试的次数（退避后重发的次数），
    /// 是 RateLimitRetryCount 配置是否生效的直观证据。
    /// </summary>
    public int RateLimitRetries { get; set; }`, 1, '实体');

// 2) Entry
edit("D:/Code/AI-Tool/src/AITool.Application/UsageLogs/IUsageLogService.cs",
`    /// 记录请求链路中累计尝试过多少条路由，便于分析重试情况。
    /// </summary>
    public int RetryCount { get; set; }`,
`    /// 记录请求链路中累计尝试过多少条路由，便于分析重试情况。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 本次转发内部因 429 速率限制实际重试的次数。
    /// </summary>
    public int RateLimitRetries { get; set; }`, 1, 'Entry');

// 3) 批量写入器映射
edit("D:/Code/AI-Tool/src/AITool.Infrastructure/Proxy/ProxyUsageLogBatchWriter.cs",
`            RetryCount = entry.RetryCount,`,
`            RetryCount = entry.RetryCount,
            RateLimitRetries = entry.RateLimitRetries,`, 1, '写入器');

// 4) 代理控制器日志点（RetryCount 行后追加，按结果变量名分组）
const openai = "D:/Code/AI-Tool/src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs";
edit(openai,
`                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,`,
`                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                    RateLimitRetries = streamResult.RateLimitRetryCount,`, 1, 'OpenAI流式');
edit(openai,
`                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,`,
`                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
                RateLimitRetries = result.RateLimitRetryCount,`, 1, 'OpenAI非流式');

const anthropic = "D:/Code/AI-Tool/src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs";
edit(anthropic,
`                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,`,
`                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                    RateLimitRetries = streamResult.RateLimitRetryCount,`, 1, 'Anthropic流式');
edit(anthropic,
`                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,`,
`                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
                RateLimitRetries = result.RateLimitRetryCount,`, 1, 'Anthropic非流式');

const responses = "D:/Code/AI-Tool/src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs";
edit(responses,
`                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,`,
`                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                    RateLimitRetries = streamResult.RateLimitRetryCount,`, 1, 'Responses流式');
edit(responses,
`                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,`,
`                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
                RateLimitRetries = result.RateLimitRetryCount,`, 1, 'Responses非流式');
edit(responses,
`                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,`,
`                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                RateLimitRetries = streamResult.RateLimitRetryCount,`, 1, 'Responses流式2');

const chat = "D:/Code/AI-Tool/src/AITool.Web/Controllers/Admin/ChatApiController.cs";
edit(chat,
`                    RetryCount = forwardResult.Success ? attemptIndex - 1 : attemptIndex,`,
`                    RetryCount = forwardResult.Success ? attemptIndex - 1 : attemptIndex,
                    RateLimitRetries = forwardResult.RateLimitRetryCount,`, 1, 'Chat转发');
edit(chat,
`                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,`,
`                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                RateLimitRetries = streamResult.RateLimitRetryCount,`, 1, 'Chat流式');

// 5) 检测探测
edit("D:/Code/AI-Tool/src/AITool.Infrastructure/Health/ModelHealthRequestService.cs",
`            RetryCount = 0,`,
`            RetryCount = 0,
            RateLimitRetries = forwardResult.RateLimitRetryCount,`, 1, '检测探测');

// 6) UsageLogs API 投影
edit("D:/Code/AI-Tool/src/AITool.Web/Controllers/Admin/UsageLogsApiController.cs",
`                RetryCount = x.RetryCount,`,
`                RetryCount = x.RetryCount,
                rateLimitRetries = x.RateLimitRetries,`, 1, 'API投影');

console.log('usage 链路全部打通');
