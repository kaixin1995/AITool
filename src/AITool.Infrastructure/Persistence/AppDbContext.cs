using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Persistence;

// 统一数据库上下文，管理所有实体映射
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // 站点数据集
    public DbSet<Site> Sites => Set<Site>();

    // 模型库数据集
    public DbSet<ModelLibraryItem> ModelLibraryItems => Set<ModelLibraryItem>();

    // 站点模型映射数据集
    public DbSet<SiteModelMapping> SiteModelMappings => Set<SiteModelMapping>();

    // 检测日志数据集
    public DbSet<DetectionLog> DetectionLogs => Set<DetectionLog>();

    // 定时检测任务数据集
    public DbSet<DetectionTask> DetectionTasks => Set<DetectionTask>();

    // 检测任务执行记录数据集
    public DbSet<DetectionTaskExecution> DetectionTaskExecutions => Set<DetectionTaskExecution>();

    // 代理路由规则数据集
    public DbSet<ProxyRouteRule> ProxyRouteRules => Set<ProxyRouteRule>();

    // 平台访问密钥数据集
    public DbSet<ProxyAccessKey> ProxyAccessKeys => Set<ProxyAccessKey>();

    // 代理使用日志数据集
    public DbSet<ProxyUsageLog> ProxyUsageLogs => Set<ProxyUsageLog>();

    // 模型健康监控配置数据集
    public DbSet<ModelHealthMonitor> ModelHealthMonitors => Set<ModelHealthMonitor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 站点实体配置
        modelBuilder.Entity<Site>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.BaseUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ApiKey).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ProtocolType).IsRequired().HasMaxLength(50);
        });

        // 模型库实体配置
        modelBuilder.Entity<ModelLibraryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.ModelType).IsRequired().HasMaxLength(50);
            // 模型名称唯一索引，防止重复模型
            entity.HasIndex(e => e.ModelName).IsUnique();
        });

        // 站点模型映射实体配置
        modelBuilder.Entity<SiteModelMapping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RemoteModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.LastStatus).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => new { e.SiteId, e.RemoteModelName }).IsUnique();
        });

        // 检测日志实体配置
        modelBuilder.Entity<DetectionLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.HasIndex(e => e.CheckedAt);
        });

        // 定时检测任务实体配置
        modelBuilder.Entity<DetectionTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CronExpression).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ModelLibraryItemId);
        });

        // 检测任务执行记录实体配置
        modelBuilder.Entity<DetectionTaskExecution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Summary).HasMaxLength(2000);
            entity.HasIndex(e => e.StartedAt);
        });

        // 代理路由规则实体配置
        modelBuilder.Entity<ProxyRouteRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExternalModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SiteModelName).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.ExternalModelName);
        });

        // 平台访问密钥实体配置
        modelBuilder.Entity<ProxyAccessKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccessKeyHash).IsRequired().HasMaxLength(500);
            entity.Property(e => e.MaskedValue).IsRequired().HasMaxLength(100);
        });

        // 代理使用日志实体配置
        modelBuilder.Entity<ProxyUsageLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProtocolType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.RequestModel).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.RequestedAt);
        });

        // 模型健康监控配置实体配置
        modelBuilder.Entity<ModelHealthMonitor>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ModelLibraryItemId).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
