using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 提供历史数据库表结构补齐能力，确保旧库通过 EnsureCreated 升级后不缺字段和新表。
/// <para>
/// SQLite 的 EnsureCreated 仅在数据库不存在时创建，已有的旧库不会自动增加新列或新表。
/// 此类在每次启动时检查并补充缺失的结构，保证功能正常运行。
/// </para>
/// </summary>
public static class DatabaseSchemaMigrator
{
    /// <summary>
    /// 为历史数据库补齐代理日志新增列，避免旧库因 EnsureCreated 不重建而缺字段。
    /// <para>
    /// 检查 ProxyUsageLogs、SiteModelMappings、Sites、SystemRuntimeSettings、ProxyRouteRules
    /// 等表中是否存在后续版本新增的列，缺失则通过 ALTER TABLE 补充。
    /// </para>
    /// </summary>
    /// <param name="dbContext">EF Core 数据库上下文。</param>
    public static async Task EnsureProxyUsageLogSchemaAsync(AppDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync();
        }

        try
        {
            if (!await ColumnExistsAsync(connection, "ProxyUsageLogs", "ForwardingMode"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN ForwardingMode TEXT NULL";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "SiteModelMappings", "MaxConcurrency"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE SiteModelMappings ADD COLUMN MaxConcurrency INTEGER NOT NULL DEFAULT 0";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "Sites", "EndpointPathMode"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE Sites ADD COLUMN EndpointPathMode TEXT NOT NULL DEFAULT 'standard-root'";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "SystemRuntimeSettings", "ConcurrencyMode"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE SystemRuntimeSettings ADD COLUMN ConcurrencyMode INTEGER NOT NULL DEFAULT 0";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "SystemRuntimeSettings", "ConcurrencyQueueTimeoutSeconds"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE SystemRuntimeSettings ADD COLUMN ConcurrencyQueueTimeoutSeconds INTEGER NOT NULL DEFAULT 120";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "SystemRuntimeSettings", "ConversationLogEnabled"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE SystemRuntimeSettings ADD COLUMN ConversationLogEnabled INTEGER NOT NULL DEFAULT 1";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "ProxyRouteRules", "AvailabilityMode"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE ProxyRouteRules ADD COLUMN AvailabilityMode TEXT NOT NULL DEFAULT 'AllDay'";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "ProxyRouteRules", "TimeRangesJson"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE ProxyRouteRules ADD COLUMN TimeRangesJson TEXT NOT NULL DEFAULT ''";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// 为历史数据库补齐结构化对话记录表，避免旧库缺少新功能所需表结构。
    /// <para>
    /// 创建 ConversationTurnLogs 表及索引（如不存在），并补充后续版本新增的列。
    /// 旧表可能包含已废弃的 AssistantOutputPlainText 列，检测到后自动移除。
    /// </para>
    /// </summary>
    /// <param name="dbContext">EF Core 数据库上下文。</param>
    public static async Task EnsureConversationLogSchemaAsync(AppDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS ConversationTurnLogs (
    Id TEXT NOT NULL PRIMARY KEY,
    RequestId TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UserCreatedAt TEXT NULL,
    SourceTool TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ConversationGroupKey TEXT NOT NULL,
    AccessKeyId TEXT NOT NULL,
    RequestModel TEXT NOT NULL,
    ProtocolType TEXT NOT NULL,
    RequestPath TEXT NOT NULL,
    Source TEXT NOT NULL,
    UserInputText TEXT NOT NULL,
    AssistantOutputMarkdown TEXT NOT NULL,
    InputTokens INTEGER NOT NULL,
    CachedTokens INTEGER NOT NULL,
    OutputTokens INTEGER NOT NULL,
    IsStreaming INTEGER NOT NULL,
    Status TEXT NOT NULL,
    MetadataJson TEXT NOT NULL,
    ConversationTitle TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_CreatedAt ON ConversationTurnLogs (CreatedAt);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_RequestId ON ConversationTurnLogs (RequestId);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_ConversationGroupKey ON ConversationTurnLogs (ConversationGroupKey);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_SourceTool_SessionId_CreatedAt ON ConversationTurnLogs (SourceTool, SessionId, CreatedAt);
";
            await command.ExecuteNonQueryAsync();

            // 旧表可能包含已废弃的 AssistantOutputPlainText 列，需要移除。
            if (await ColumnExistsAsync(connection, "ConversationTurnLogs", "AssistantOutputPlainText"))
            {
                command.CommandText = "ALTER TABLE ConversationTurnLogs DROP COLUMN AssistantOutputPlainText;";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "ConversationTurnLogs", "UserCreatedAt"))
            {
                command.CommandText = "ALTER TABLE ConversationTurnLogs ADD COLUMN UserCreatedAt TEXT NULL;";
                await command.ExecuteNonQueryAsync();
            }

            if (!await ColumnExistsAsync(connection, "ConversationTurnLogs", "ConversationTitle"))
            {
                command.CommandText = "ALTER TABLE ConversationTurnLogs ADD COLUMN ConversationTitle TEXT NOT NULL DEFAULT '';";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// 检查指定表是否已经存在目标列。
    /// <para>
    /// 通过 PRAGMA table_info 查询表结构，逐行比对列名（不区分大小写）。
    /// </para>
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="tableName">表名。</param>
    /// <param name="columnName">列名。</param>
    /// <returns>列存在返回 true，否则 false。</returns>
    public static async Task<bool> ColumnExistsAsync(DbConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader[1]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
