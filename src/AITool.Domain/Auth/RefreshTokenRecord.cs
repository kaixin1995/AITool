using SqlSugar;

namespace AITool.Domain.Auth;

/// <summary>
/// 持久化的 refresh token 记录，存储到数据库。
/// 解决内存存储重启丢失的问题。
/// </summary>
[SugarTable("RefreshToken")]
public class RefreshTokenRecord
{
    /// <summary>
    /// refresh token 字符串（主键）。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, ColumnName = "Token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// 关联的用户标识。
    /// </summary>
    [SugarColumn(ColumnName = "SubjectId")]
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// 过期时间（UTC）。
    /// </summary>
    [SugarColumn(ColumnName = "ExpiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// 创建时间（UTC），用于清理。
    /// </summary>
    [SugarColumn(ColumnName = "CreatedAt")]
    public DateTimeOffset CreatedAt { get; set; }
}
