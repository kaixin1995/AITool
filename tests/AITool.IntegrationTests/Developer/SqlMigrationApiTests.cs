using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Developer;

/// <summary>
/// SQL 迁移脚本执行 API 集成测试：验证执行/试运行/回滚/密码门禁/开关隐藏/路径穿越防护/审计记录。
/// </summary>
public sealed class SqlMigrationApiTests
{
    private const string AdminPassword = SqlMigrationTestConstants.AdminPassword;
    private const string ProbeTable = SqlMigrationTestConstants.ProbeTable;

    [Fact]
    public async Task Execute_commits_script_and_records_history()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        factory.WriteScript("001-probe.sql", $"""
            -- 初始化探针表
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);
            INSERT INTO {ProbeTable}(Value) VALUES ('hello');
            """);
        using var client = factory.CreateClient();

        var response = await PostExecuteAsync(client, "001-probe.sql", AdminPassword, dryRun: false);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeTrue();
        data.GetProperty("dryRun").GetBoolean().Should().BeFalse();
        data.GetProperty("statementCount").GetInt32().Should().Be(2);
        data.GetProperty("errorMessage").ValueKind.Should().Be(JsonValueKind.Null);

        factory.ProbeValues().Should().BeEquivalentTo(["hello"], "脚本提交后探针表应写入一行");

        var history = factory.History("001-probe.sql");
        history.Should().ContainSingle();
        history[0].Success.Should().BeTrue();
        history[0].DryRun.Should().BeFalse();
        history[0].StatementCount.Should().Be(2);
        history[0].RowsAffected.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Execute_same_script_twice_is_allowed()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        factory.WriteScript("001-repeat.sql", $"""
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);
            INSERT INTO {ProbeTable}(Value) VALUES ('x');
            """);
        using var client = factory.CreateClient();

        var first = await PostExecuteAsync(client, "001-repeat.sql", AdminPassword, dryRun: false);
        (await first.Content.ReadAsStringAsync()).Should().Contain("\"success\":true", "第一次执行应成功");

        var second = await PostExecuteAsync(client, "001-repeat.sql", AdminPassword, dryRun: false);
        var body = await second.Content.ReadAsStringAsync();
        second.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"success\":true", "重复执行按用户决策是允许的");

        factory.ProbeValues().Should().HaveCount(2, "同一脚本执行两次应写入两行");
        factory.History("001-repeat.sql").Should().HaveCount(2);
    }

    [Fact]
    public async Task Dry_run_executes_then_rolls_back()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        factory.WriteScript("001-dry.sql", $"""
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);
            INSERT INTO {ProbeTable}(Value) VALUES ('dry');
            """);
        using var client = factory.CreateClient();

        var response = await PostExecuteAsync(client, "001-dry.sql", AdminPassword, dryRun: true);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeTrue();
        data.GetProperty("dryRun").GetBoolean().Should().BeTrue();

        factory.ProbeValues().Should().BeEmpty("试运行应整体回滚，不落任何数据变更");

        var history = factory.History("001-dry.sql");
        history.Should().ContainSingle();
        history[0].DryRun.Should().BeTrue();
        history[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_with_wrong_password_is_rejected_without_history()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        factory.WriteScript("001-secret.sql", $"""
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);
            INSERT INTO {ProbeTable}(Value) VALUES ('x');
            """);
        using var client = factory.CreateClient();

        var response = await PostExecuteAsync(client, "001-secret.sql", "wrong-password", dryRun: false);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("管理员密码校验失败");

        factory.ProbeValues().Should().BeEmpty("密码错误不应执行任何语句");
        factory.History("001-secret.sql").Should().BeEmpty("密码错误属于身份校验失败，不写执行审计");
    }

    [Fact]
    public async Task Api_returns_404_when_developer_features_disabled()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: false);
        factory.WriteScript("001-hidden.sql", $"CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);");
        using var client = factory.CreateClient();

        var list = await client.GetAsync("/api/admin/sql-migrations");
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var execute = await PostExecuteAsync(client, "001-hidden.sql", AdminPassword, dryRun: false);
        execute.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Failing_statement_rolls_back_whole_script_and_records_failure()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        factory.WriteScript("001-fail.sql", $"""
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);
            INSERT INTO {ProbeTable}(Value) VALUES ('should-rollback');
            INSERT INTO table_that_does_not_exist(Id) VALUES (1);
            """);
        using var client = factory.CreateClient();

        var response = await PostExecuteAsync(client, "001-fail.sql", AdminPassword, dryRun: false);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "SQL 失败是合法的执行结果，接口本身返回 200");
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeFalse();
        data.GetProperty("errorMessage").GetString().Should().Contain("第 3 条语句执行失败");

        factory.ProbeValues().Should().BeEmpty("失败语句之前的写入应整体回滚");

        var history = factory.History("001-fail.sql");
        history.Should().ContainSingle();
        history[0].Success.Should().BeFalse();
        history[0].ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Statement_splitter_respects_strings_and_comments()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        // 值里带分号与 -- 注释样式文本，块注释中再带一个分号：都不应被当作语句分隔符。
        factory.WriteScript("001-split.sql", $"""
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT); /* 注释;分号 */
            -- 行注释;又一个分号
            INSERT INTO {ProbeTable}(Value) VALUES ('a;b -- not comment');
            """);
        using var client = factory.CreateClient();

        var response = await PostExecuteAsync(client, "001-split.sql", AdminPassword, dryRun: false);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeTrue(body);
        data.GetProperty("statementCount").GetInt32().Should().Be(2, "两个可执行语句：建表 + 插入");

        factory.ProbeValues().Should().BeEquivalentTo(["a;b -- not comment"], "字符串值应原样保留");
    }

    [Fact]
    public async Task Execute_rejects_path_traversal_file_name()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var traversal = await client.PostAsync(
            $"/api/admin/sql-migrations/{Uri.EscapeDataString("..\\appsettings.json")}/execute",
            JsonContentOf(AdminPassword));
        traversal.StatusCode.Should().Be(HttpStatusCode.NotFound, "路径穿越样式的文件名必须被拒绝");

        var missing = await PostExecuteAsync(client, "999-not-exist.sql", AdminPassword, dryRun: false);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        factory.AllHistory().Should().BeEmpty("拒绝的请求不应产生执行审计");
    }

    [Fact]
    public async Task List_returns_scripts_with_execution_summary()
    {
        await using var factory = new SqlMigrationWebApplicationFactory(developerFeaturesEnabled: true);
        factory.WriteScript("001-list.sql", $"""
            CREATE TABLE IF NOT EXISTS {ProbeTable}(Value TEXT);
            INSERT INTO {ProbeTable}(Value) VALUES ('x');
            """);
        using var client = factory.CreateClient();

        await PostExecuteAsync(client, "001-list.sql", AdminPassword, dryRun: false);

        var response = await client.GetAsync("/api/admin/sql-migrations");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("directory").GetString().Should().NotBeNullOrEmpty("应返回脚本目录路径供前端提示");
        var scripts = data.GetProperty("scripts");
        scripts.GetArrayLength().Should().Be(1);
        var script = scripts[0];
        script.GetProperty("fileName").GetString().Should().Be("001-list.sql");
        script.GetProperty("totalExecutions").GetInt32().Should().Be(1);
        script.GetProperty("successExecutions").GetInt32().Should().Be(1);
        script.GetProperty("contentPreview").GetString().Should().Contain(ProbeTable);
    }

    private static StringContent JsonContentOf(string password, bool dryRun = false)
        => new($"{{\"password\":\"{password}\",\"dryRun\":{(dryRun ? "true" : "false")}}}", Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostExecuteAsync(HttpClient client, string fileName, string password, bool dryRun)
        => await client.PostAsync(
            $"/api/admin/sql-migrations/{Uri.EscapeDataString(fileName)}/execute",
            JsonContentOf(password, dryRun));
}

/// <summary>
/// 测试与测试宿主共享的常量。
/// </summary>
internal static class SqlMigrationTestConstants
{
    public const string AdminPassword = "sql-migration-test-password";
    public const string ProbeTable = "__sql_migration_probe";
}

/// <summary>
/// SQL 迁移测试宿主：临时 SQLite 库 + 临时脚本目录 + 固定测试管理员密码 + 可控开发者开关。
/// </summary>
internal sealed class SqlMigrationWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-sql-migration-{Guid.NewGuid():N}.db");
    private readonly string _scriptsDirectory = Path.Combine(Path.GetTempPath(), $"aitool-sql-migration-{Guid.NewGuid():N}");
    private readonly bool _developerFeaturesEnabled;

    public SqlMigrationWebApplicationFactory(bool developerFeaturesEnabled)
    {
        _developerFeaturesEnabled = developerFeaturesEnabled;
        Directory.CreateDirectory(_scriptsDirectory);
    }

    public void WriteScript(string fileName, string content)
        => File.WriteAllText(Path.Combine(_scriptsDirectory, fileName), content, new UTF8Encoding(false));

    public List<string> ProbeValues()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            return db.Client.Ado.SqlQuery<string>($"SELECT Value FROM {SqlMigrationTestConstants.ProbeTable} ORDER BY RowId");
        }
        catch
        {
            return [];
        }
    }

    public List<SqlMigrationExecution> History(string fileName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.SqlMigrationExecutions.Where(x => x.FileName == fileName).OrderBy(x => x.ExecutedAt).ToList();
    }

    public List<SqlMigrationExecution> AllHistory()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.SqlMigrationExecutions.ToList();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:Port"] = "0",
                ["AdminAuth:PasswordHash"] = PasswordHasher.Hash(SqlMigrationTestConstants.AdminPassword),
                ["SqlMigrations:Directory"] = _scriptsDirectory
            });
        });
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        Seed();
    }

    private void Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
        db.Client.Insertable(new SystemRuntimeSettings
        {
            Id = 1,
            DeveloperFeaturesEnabled = _developerFeaturesEnabled
        }).ExecuteCommand();
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (Directory.Exists(_scriptsDirectory))
            {
                Directory.Delete(_scriptsDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }

        base.Dispose(disposing);
    }
}
