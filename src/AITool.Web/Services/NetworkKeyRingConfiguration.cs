using System.Data.Common;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>Keep the network secret key ring beside the actual database, including isolated test databases.</summary>
internal sealed class NetworkKeyRingConfiguration(
    IConfiguration configuration,
    IHostEnvironment environment,
    ISqlSugarClient database,
    ILoggerFactory loggerFactory) : IConfigureOptions<KeyManagementOptions>
{
    private readonly string _memoryDirectory = Path.Combine(Path.GetTempPath(), "aitool-network-keys", Guid.NewGuid().ToString("N"));

    public void Configure(KeyManagementOptions options)
    {
        var configured = configuration["NetworkSecurity:KeyRingPath"];
        string directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            directory = Path.GetFullPath(configured, environment.ContentRootPath);
        }
        else
        {
            var connection = new DbConnectionStringBuilder { ConnectionString = database.CurrentConnectionConfig.ConnectionString };
            var sourceKey = connection.Keys.Cast<string>()
                .FirstOrDefault(key => string.Equals(key, "Data Source", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "DataSource", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "Filename", StringComparison.OrdinalIgnoreCase));
            var source = sourceKey is null ? null : connection[sourceKey]?.ToString();
            if (string.IsNullOrWhiteSpace(source) || source == ":memory:")
            {
                if (!environment.IsEnvironment("Testing"))
                    throw new InvalidOperationException("A persistent database or NetworkSecurity:KeyRingPath is required for network secrets.");
                directory = _memoryDirectory;
            }
            else
            {
                var databasePath = Path.GetFullPath(source, environment.ContentRootPath);
                directory = Path.Combine(Path.GetDirectoryName(databasePath)!, ".aitool-secrets", Path.GetFileName(databasePath), "keyring");
            }
        }

        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(directory), loggerFactory);
    }
}
