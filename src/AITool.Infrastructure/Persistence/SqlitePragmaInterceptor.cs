using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 在每次 SQLite 连接打开时设置连接级 PRAGMA。
/// <para>
/// cache_size=-65536（约 64MB 页缓存）减少磁盘 IO；
/// busy_timeout=5000 让并发写竞争时等待 5 秒而非立即抛 "database is locked"。
/// 这些是连接级属性（不持久），每次打开连接都需要重新设置。
/// </para>
/// <para>
/// 持久化的 PRAGMA（journal_mode=WAL, synchronous=NORMAL）由
/// <see cref="AdminStartupInitializer.ApplyPersistentPragmasAsync"/> 在启动时一次性设置。
/// </para>
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// 页缓存大小（页数，负数表示 KB）。-65536 ≈ 64MB。
    /// </summary>
    private const string CacheSizePragma = "PRAGMA cache_size=-65536;";

    /// <summary>
    /// 锁等待超时（毫秒）。并发写竞争时等待而非立即失败。
    /// </summary>
    private const string BusyTimeoutPragma = "PRAGMA busy_timeout=5000;";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetPragmaAsync(connection, CacheSizePragma, cancellationToken);
        await SetPragmaAsync(connection, BusyTimeoutPragma, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetPragma(connection, CacheSizePragma);
        SetPragma(connection, BusyTimeoutPragma);
        base.ConnectionOpened(connection, eventData);
    }

    private static async ValueTask SetPragmaAsync(
        DbConnection connection, string pragma, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // PRAGMA 设置失败不阻塞连接使用（降级为默认值）。
        }
    }

    private static void SetPragma(DbConnection connection, string pragma)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
        catch
        {
            // PRAGMA 设置失败不阻塞连接使用（降级为默认值）。
        }
    }
}
