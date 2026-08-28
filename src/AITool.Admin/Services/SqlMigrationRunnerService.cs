using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// 一次 SQL 迁移脚本的执行结果（含试运行）。
/// </summary>
public sealed record SqlMigrationExecutionResult(
    string FileName,
    string FileHash,
    bool DryRun,
    bool Success,
    int StatementCount,
    int RowsAffected,
    int DurationMs,
    string? ErrorMessage);

/// <summary>
/// 脚本目录中的单个 .sql 文件及其执行状态汇总。
/// </summary>
public sealed record SqlMigrationScriptInfo(
    string FileName,
    long SizeBytes,
    string FileHash,
    string ContentPreview,
    bool ContentTruncated,
    int TotalExecutions,
    int SuccessExecutions,
    DateTimeOffset? LastExecutedAt,
    bool? LastSuccess,
    bool LastDryRun,
    string? LastErrorMessage);

/// <summary>
/// SQL 迁移脚本执行器。
/// <para>
/// 安全模型：接口层不接收任何 SQL 文本，只允许触发服务器 <c>sql-migrations</c> 目录下已存在的
/// .sql 文件（文件名必须与目录枚举结果精确匹配），脚本本体只能由管理员通过 SSH/SFTP 放置；
/// 每次执行（含试运行）都必须重新校验管理员密码；全程事务，试运行回滚；所有尝试写入
/// <see cref="SqlMigrationExecution"/> 审计表并记录 NLog 告警日志。
/// </para>
/// </summary>
public sealed class SqlMigrationRunnerService
{
    /// <summary>
    /// 单个脚本文件大小上限（1MB），防止误放超大文件拖垮 SQLite。
    /// </summary>
    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// 单个脚本拆分后的语句数上限。
    /// </summary>
    private const int MaxStatementCount = 500;

    /// <summary>
    /// 列表接口返回的内容预览上限（64KB），完整内容以服务器文件为准。
    /// </summary>
    private const int MaxPreviewChars = 64 * 1024;

    /// <summary>
    /// 执行串行化锁：同一时刻只允许一个脚本在执行，避免并发事务互相锁库。
    /// </summary>
    private static readonly SemaphoreSlim ExecuteLock = new(1, 1);

    private readonly AppDbContext _dbContext;
    private readonly AdminAuthService _adminAuth;
    private readonly LoginRateLimitService _rateLimiter;
    private readonly ILogger<SqlMigrationRunnerService> _logger;
    private readonly string _scriptsDirectory;

    public SqlMigrationRunnerService(
        AppDbContext dbContext,
        AdminAuthService adminAuth,
        LoginRateLimitService rateLimiter,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<SqlMigrationRunnerService> logger)
    {
        _dbContext = dbContext;
        _adminAuth = adminAuth;
        _rateLimiter = rateLimiter;
        _logger = logger;

        // 目录可由配置 SqlMigrations:Directory 覆盖（集成测试用）；默认部署目录下的 sql-migrations。
        var configured = configuration["SqlMigrations:Directory"];
        _scriptsDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "sql-migrations")
            : Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
    }

    /// <summary>
    /// 脚本目录的绝对路径（前端展示用，提示管理员应把文件放到哪里）。
    /// </summary>
    public string ScriptsDirectory => _scriptsDirectory;

    /// <summary>
    /// 列出脚本目录下全部 .sql 文件及其执行历史汇总。目录不存在时返回空列表（不报错）。
    /// </summary>
    public async Task<List<SqlMigrationScriptInfo>> ListScriptsAsync(CancellationToken cancellationToken = default)
    {
        var files = Directory.Exists(_scriptsDirectory)
            ? Directory.GetFiles(_scriptsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        var histories = (await _dbContext.SqlMigrationExecutions
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.ExecutedAt).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var scripts = new List<SqlMigrationScriptInfo>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var fileInfo = new FileInfo(file);
            histories.TryGetValue(fileName, out var fileHistory);
            var last = fileHistory?.FirstOrDefault();

            string preview;
            bool truncated;
            string hash;
            if (fileInfo.Length > MaxFileBytes)
            {
                // 超限文件仍列出（便于发现），但预览置空、哈希置空，执行时会被拒绝。
                preview = string.Empty;
                truncated = true;
                hash = string.Empty;
            }
            else
            {
                var content = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
                preview = content.Length > MaxPreviewChars ? content[..MaxPreviewChars] : content;
                truncated = content.Length > MaxPreviewChars;
                hash = HashContent(content);
            }

            scripts.Add(new SqlMigrationScriptInfo(
                fileName,
                fileInfo.Length,
                hash,
                preview,
                truncated,
                fileHistory?.Count ?? 0,
                fileHistory?.Count(x => x.Success && !x.DryRun) ?? 0,
                last?.ExecutedAt,
                last?.Success,
                last?.DryRun ?? false,
                last?.ErrorMessage));
        }

        return scripts;
    }

    /// <summary>
    /// 执行指定脚本。密码校验失败抛 <see cref="InvalidOperationException"/>（身份未通过校验，不写审计表）；
    /// 文件不存在或参数非法抛 <see cref="FileNotFoundException"/>；
    /// 其余执行结果（成功/失败/试运行）一律写入审计表并返回结果对象。
    /// </summary>
    public async Task<SqlMigrationExecutionResult> ExecuteAsync(
        string fileName,
        string password,
        bool dryRun,
        string? operatorIp,
        CancellationToken cancellationToken = default)
    {
        // 文件名只允许是目录内枚举到的纯文件名：拒绝路径分隔符与上级目录引用。
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/') || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal)
            || Path.GetFileName(fileName) != fileName)
        {
            throw new FileNotFoundException($"脚本不存在：{fileName}");
        }

        // 密码校验与登录接口共用同一套暴力破解防护：连续失败按 IP 锁定。
        var rateKey = string.IsNullOrWhiteSpace(operatorIp) ? "unknown" : operatorIp;
        var lockSeconds = _rateLimiter.CheckLocked(rateKey);
        if (lockSeconds is not null)
        {
            _logger.LogWarning("SqlMigration execute rejected: ip locked (file={FileName}, ip={Ip})", fileName, operatorIp);
            throw new InvalidOperationException($"尝试次数过多已被锁定，请约 {lockSeconds} 秒后再试");
        }

        if (!_adminAuth.VerifyPassword(password))
        {
            // 密码错误不写入执行审计（身份未通过校验），但记录告警日志便于发现爆破尝试。
            _rateLimiter.RecordFailure(rateKey);
            _logger.LogWarning("SqlMigration execute rejected: invalid admin password (file={FileName}, ip={Ip})", fileName, operatorIp);
            throw new InvalidOperationException("管理员密码校验失败，已拒绝执行");
        }

        _rateLimiter.RecordSuccess(rateKey);

        await ExecuteLock.WaitAsync(cancellationToken);
        try
        {
            var target = Path.Combine(_scriptsDirectory, fileName);
            if (!Directory.Exists(_scriptsDirectory) || !File.Exists(target))
            {
                throw new FileNotFoundException($"脚本不存在：{fileName}");
            }

            var fileInfo = new FileInfo(target);
            if (fileInfo.Length > MaxFileBytes)
            {
                throw new InvalidOperationException($"脚本文件超过 {MaxFileBytes / 1024} KB 上限，拒绝执行");
            }

            var content = (await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken)).TrimStart('\uFEFF');
            var fileHash = HashContent(content);
            var statements = SplitStatements(content);
            if (statements.Count == 0)
            {
                throw new InvalidOperationException("脚本不包含任何可执行语句");
            }

            if (statements.Count > MaxStatementCount)
            {
                throw new InvalidOperationException($"脚本拆分出 {statements.Count} 条语句，超过 {MaxStatementCount} 条上限");
            }

            var result = await RunStatementsAsync(fileName, fileHash, statements, dryRun, cancellationToken);
            try
            {
                await RecordAsync(fileName, fileHash, dryRun, result, operatorIp, cancellationToken);
            }
            catch (Exception recordEx)
            {
                // 审计写入失败不能吞掉已提交的执行结果：SQL 可能已经真实落库，
                // 此时向调用方报错会诱导用户误以为未执行而重跑非幂等脚本。
                _logger.LogWarning(recordEx, "SqlMigration 审计记录写入失败（脚本可能已执行）：{FileName}", fileName);
            }

            return result;
        }
        finally
        {
            ExecuteLock.Release();
        }
    }

    /// <summary>
    /// 在独立连接的事务内逐条执行语句：任一语句失败即回滚整体；
    /// 试运行（dryRun）在全部执行完成后回滚，不落任何数据变更。
    /// </summary>
    private async Task<SqlMigrationExecutionResult> RunStatementsAsync(
        string fileName,
        string fileHash,
        List<string> statements,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rowsAffected = 0;
        var executingIndex = 0;
        string? errorMessage = null;

        // CopyNew 独立连接执行，不碰单例 SqlSugarScope 的连接状态。
        // 注意：语句用同步 ExecuteCommand 逐条执行——IAdo 的 ExecuteCommandAsync(string, object)
        // 会把第二参数当参数容器解析（传 CancellationToken 会报“参数格式错误”），
        // 且 SQLite 本地执行无异步收益，与代码库现有原生 SQL 用法保持一致。
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        client.Ado.BeginTran();
        try
        {
            for (executingIndex = 0; executingIndex < statements.Count; executingIndex++)
            {
                rowsAffected += client.Ado.ExecuteCommand(statements[executingIndex]);
            }

            if (dryRun)
            {
                client.Ado.RollbackTran();
            }
            else
            {
                client.Ado.CommitTran();
            }
        }
        catch (Exception ex)
        {
            try
            {
                client.Ado.RollbackTran();
            }
            catch
            {
                // 回滚失败通常意味着连接已中断，无需额外处理。
            }

            errorMessage = $"第 {executingIndex + 1} 条语句执行失败：{ex.Message}";
        }

        stopwatch.Stop();
        return new SqlMigrationExecutionResult(
            fileName,
            fileHash,
            dryRun,
            errorMessage == null,
            statements.Count,
            rowsAffected,
            (int)stopwatch.ElapsedMilliseconds,
            errorMessage);
    }

    /// <summary>
    /// 写入执行审计记录并输出 NLog 告警（无论成功失败）。
    /// </summary>
    private async Task RecordAsync(
        string fileName,
        string fileHash,
        bool dryRun,
        SqlMigrationExecutionResult result,
        string? operatorIp,
        CancellationToken cancellationToken)
    {
        await _dbContext.InsertAsync(new SqlMigrationExecution
        {
            FileName = fileName,
            FileHash = fileHash,
            DryRun = dryRun,
            Success = result.Success,
            RowsAffected = result.RowsAffected,
            StatementCount = result.StatementCount,
            DurationMs = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            OperatorIp = operatorIp,
            ExecutedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        if (result.Success)
        {
            _logger.LogWarning(
                "SqlMigration {Mode} executed: file={FileName} hash={FileHash} statements={Statements} rows={Rows} duration={Duration}ms ip={Ip}",
                dryRun ? "dry-run" : "commit", fileName, fileHash, result.StatementCount, result.RowsAffected, result.DurationMs, operatorIp);
        }
        else
        {
            _logger.LogWarning(
                "SqlMigration {Mode} FAILED: file={FileName} hash={FileHash} error={Error} ip={Ip}",
                dryRun ? "dry-run" : "commit", fileName, fileHash, result.ErrorMessage, operatorIp);
        }
    }

    /// <summary>
    /// 计算脚本内容的 SHA256（十六进制）。
    /// </summary>
    private static string HashContent(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    /// 按分号拆分 SQL 脚本为独立语句。拆分器识别并原样保留：
    /// <c>--</c> 行注释、<c>/* */</c> 块注释、单引号字符串（<c>''</c> 转义）、
    /// 双引号标识符（<c>""</c> 转义）、方括号标识符（<c>]]</c> 转义）——这些结构内的分号不参与拆分。
    /// 纯注释/空白的片段会被丢弃。
    /// </summary>
    internal static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var buffer = new StringBuilder();
        var index = 0;
        var length = sql.Length;

        while (index < length)
        {
            var c = sql[index];

            // 行注释：保留原文（含换行）到行尾，注释内的分号不拆分。
            if (c == '-' && index + 1 < length && sql[index + 1] == '-')
            {
                var start = index;
                while (index < length && sql[index] != '\n')
                {
                    index++;
                }
                if (index < length)
                {
                    index++;
                }
                buffer.Append(sql, start, index - start);
                continue;
            }

            // 块注释：保留原文到 */，注释内的分号不拆分。
            if (c == '/' && index + 1 < length && sql[index + 1] == '*')
            {
                var start = index;
                index += 2;
                while (index < length && !(sql[index] == '*' && index + 1 < length && sql[index + 1] == '/'))
                {
                    index++;
                }
                index = Math.Min(index + 2, length);
                buffer.Append(sql, start, index - start);
                continue;
            }

            // 引号/方括号结构：原样吞到闭合（支持成对转义），内部分号不拆分。
            if (c is '\'' or '"' or '[')
            {
                var closer = c == '[' ? ']' : c;
                buffer.Append(c);
                index++;
                while (index < length)
                {
                    buffer.Append(sql[index]);
                    if (sql[index] == closer)
                    {
                        if (index + 1 < length && sql[index + 1] == closer)
                        {
                            buffer.Append(sql[index + 1]);
                            index += 2;
                            continue;
                        }
                        index++;
                        break;
                    }
                    index++;
                }
                continue;
            }

            if (c == ';')
            {
                AddIfExecutable(statements, buffer);
                buffer.Clear();
                index++;
                continue;
            }

            buffer.Append(c);
            index++;
        }

        // 末尾未带分号的语句。
        AddIfExecutable(statements, buffer);
        return statements;
    }

    /// <summary>
    /// 缓冲区内容剔除注释与空白后非空才加入语句列表（丢弃纯注释片段）。
    /// </summary>
    private static void AddIfExecutable(List<string> statements, StringBuilder buffer)
    {
        var text = buffer.ToString().Trim();
        if (text.Length == 0 || !HasExecutableContent(text))
        {
            return;
        }

        statements.Add(text);
    }

    /// <summary>
    /// 判断片段剔除注释与空白后是否仍有内容。
    /// </summary>
    private static bool HasExecutableContent(string text)
    {
        var index = 0;
        var length = text.Length;
        while (index < length)
        {
            var c = text[index];
            if (c == '-' && index + 1 < length && text[index + 1] == '-')
            {
                while (index < length && text[index] != '\n')
                {
                    index++;
                }
                continue;
            }

            if (c == '/' && index + 1 < length && text[index + 1] == '*')
            {
                index += 2;
                while (index < length && !(text[index] == '*' && index + 1 < length && text[index + 1] == '/'))
                {
                    index++;
                }
                index = Math.Min(index + 2, length);
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                return true;
            }
            index++;
        }
        return false;
    }
}
