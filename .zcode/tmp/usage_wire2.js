const fs = require('fs');
const edit = (path, oldS, newS, expect, tag) => {
  let raw = fs.readFileSync(path, 'utf8');
  const eol = raw.includes('\r\n') ? '\r\n' : '\n';
  let src = raw.replace(/\r\n/g, '\n');
  const n = src.split(oldS).length - 1;
  if (n !== expect) throw new Error('[' + tag + '] 匹配 ' + n);
  src = src.split(oldS).join(newS);
  fs.writeFileSync(path, src.split('\n').join(eol), 'utf8');
  console.log(tag + ' OK');
};

const responses = "D:/Code/AI-Tool/src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs";
edit(responses,
"\n                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,",
"\n                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,\n                RateLimitRetries = streamResult.RateLimitRetryCount,", 1, 'Responses流式2');

const chat = "D:/Code/AI-Tool/src/AITool.Web/Controllers/Admin/ChatApiController.cs";
edit(chat,
"                    RetryCount = forwardResult.Success ? attemptIndex - 1 : attemptIndex,",
"                    RetryCount = forwardResult.Success ? attemptIndex - 1 : attemptIndex,\n                    RateLimitRetries = forwardResult.RateLimitRetryCount,", 1, 'Chat转发');
edit(chat,
"\n                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,",
"\n                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,\n                RateLimitRetries = streamResult.RateLimitRetryCount,", 1, 'Chat流式');

edit("D:/Code/AI-Tool/src/AITool.Infrastructure/Health/ModelHealthRequestService.cs",
"            RetryCount = 0,",
"            RetryCount = 0,\n            RateLimitRetries = forwardResult.RateLimitRetryCount,", 1, '检测探测');

edit("D:/Code/AI-Tool/src/AITool.Web/Controllers/Admin/UsageLogsApiController.cs",
"                RetryCount = x.RetryCount,",
"                RetryCount = x.RetryCount,\n                rateLimitRetries = x.RateLimitRetries,", 1, 'API投影');

console.log('剩余 5 处完成');
