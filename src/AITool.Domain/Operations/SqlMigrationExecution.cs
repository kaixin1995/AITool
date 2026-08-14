using SqlSugar;

namespace AITool.Domain.Operations;

/// <summary>
/// 表示一次 SQL 迁移脚本的执行记录（含试运行）。
/// 脚本本体存放在服务器 sql-migrations 目录，接口只允许触发已有文件，
/// 此表用于完整审计：谁（IP）、何时、执行了哪个哈希版本的脚本、结果如何。
/// </summary>
[SugarTable("SqlMigrationExecutions")]
[SugarIndex("IX_SqlMigrationExecutions_FileName_ExecutedAt", nameof(FileName), OrderByType.Asc, nameof(ExecutedAt), OrderByType.Desc)]
public sealed class SqlMigrationExecution
{
    /// <summary>
    /// 执行记录唯一标识。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 脚本文件名（不含路径，与目录枚举结果精确匹配）。
    /// </summary>
    [SugarColumn(Length = 255, IsNullable = false)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 执行时脚本内容的 SHA256 哈希（十六进制），用于事后核对执行的是哪个版本。
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = false)]
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// 是否为试运行（事务内执行后回滚，不落任何数据变更）。
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// 执行是否成功（所有语句均执行完成；试运行以回滚前的执行结果为准）。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 全部语句累计影响行数。
    /// </summary>
    public int RowsAffected { get; set; }

    /// <summary>
    /// 拆分后的语句条数。
    /// </summary>
    public int StatementCount { get; set; }

    /// <summary>
    /// 执行耗时（毫秒）。
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// 失败时的错误信息（含失败语句序号），成功时为空。
    /// </summary>
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 触发本次执行的客户端 IP，用于审计。
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? OperatorIp { get; set; }

    /// <summary>
    /// 执行时间。
    /// </summary>
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
}
