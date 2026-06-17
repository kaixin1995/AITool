using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 在每次 SQLite 连接打开时设置连接级 PRAGMA（cache_size/busy_timeout）。
/// 持久化的 PRAGMA（journal_mode=WAL, synchronous=NORMAL）由启动代码执行一次。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string CacheSizePragma = "PRAGMA cache_size=-65536;";
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

    private static async Task SetPragmaAsync(
        DbConnection connection, string pragma, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch { }
    }

    private static void SetPragma(DbConnection connection, string pragma)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
        catch { }
    }
}
