using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 统一数据库上下文，管理所有实体映射
/// </summary>
public sealed class AppDbContext : DbContext
{
    /// <summary>
    /// 注入数据库上下文配置选项
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// <summary>
    /// 站点数据集
    /// </summary>
    public DbSet<Site> Sites => Set<Site>();

    /// <summary>
    /// 模型库数据集
    /// </summary>
    public DbSet<ModelLibraryItem> ModelLibraryItems => Set<ModelLibraryItem>();

    /// <summary>
    /// 站点模型映射数据集
    /// </summary>
    public DbSet<SiteModelMapping> SiteModelMappings => Set<SiteModelMapping>();

    /// <summary>
    /// 定时检测任务数据集
    /// </summary>
    public DbSet<DetectionTask> DetectionTasks => Set<DetectionTask>();

    /// <summary>
    /// 检测任务执行记录数据集
    /// </summary>
    public DbSet<DetectionTaskExecution> DetectionTaskExecutions => Set<DetectionTaskExecution>();

    /// <summary>
    /// 代理主入口数据集
    /// </summary>
    public DbSet<ProxyRouteEntry> ProxyRouteEntries => Set<ProxyRouteEntry>();

    /// <summary>
    /// 代理路由规则数据集
    /// </summary>
    public DbSet<ProxyRouteRule> ProxyRouteRules => Set<ProxyRouteRule>();

    /// <summary>
    /// 平台访问密钥数据集
    /// </summary>
    public DbSet<ProxyAccessKey> ProxyAccessKeys => Set<ProxyAccessKey>();

    /// <summary>
    /// 代理使用日志数据集
    /// </summary>
    public DbSet<ProxyUsageLog> ProxyUsageLogs => Set<ProxyUsageLog>();

    /// <summary>
    /// 结构化对话记录数据集
    /// </summary>
    public DbSet<ConversationTurnLog> ConversationTurnLogs => Set<ConversationTurnLog>();

    /// <summary>
    /// 模型健康监控配置数据集
    /// </summary>
    public DbSet<ModelHealthMonitor> ModelHealthMonitors => Set<ModelHealthMonitor>();

    /// <summary>
    /// 系统运行时设置数据集
    /// </summary>
    public DbSet<SystemRuntimeSettings> SystemRuntimeSettings => Set<SystemRuntimeSettings>();

    /// <summary>
    /// 配置所有实体的主键、字段约束、索引等数据库映射规则
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 站点实体配置
        modelBuilder.Entity<Site>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.BaseUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.EndpointPathMode).IsRequired().HasMaxLength(50).HasDefaultValue("standard-root");
            entity.Property(e => e.ApiKey).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ProtocolType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SupportsOpenAi).IsRequired();
            entity.Property(e => e.SupportsAnthropic).IsRequired();
        });

        // 模型库实体配置
        modelBuilder.Entity<ModelLibraryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            // 兼容旧库中仍保留的 ModelType 非空列，但不再对外暴露该字段。
            entity.Property<string>("ModelType").IsRequired().HasMaxLength(50);
            // 模型名称唯一索引，防止重复模型
            entity.HasIndex(e => e.ModelName).IsUnique();
        });

        // 站点模型映射实体配置
        modelBuilder.Entity<SiteModelMapping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RemoteModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.LastStatus).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MaxConcurrency).IsRequired();
            entity.Property(e => e.IsEnabled).IsRequired();
            entity.HasIndex(e => new { e.SiteId, e.RemoteModelName }).IsUnique();
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

        // 代理主入口实体配置
        modelBuilder.Entity<ProxyRouteEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntryName).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.EntryName).IsUnique();
        });

        // 代理路由规则实体配置
        modelBuilder.Entity<ProxyRouteRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExternalModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.UpstreamModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SiteModelName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AvailabilityMode).IsRequired().HasMaxLength(50).HasDefaultValue("AllDay");
            entity.Property(e => e.TimeRangesJson).IsRequired().HasMaxLength(2000).HasDefaultValue(string.Empty);
            entity.HasIndex(e => new { e.ExternalModelName, e.Priority });
            entity.HasIndex(e => new { e.ExternalModelName, e.IsEnabled, e.ModelPriority, e.InstancePriority, e.Priority });
        });

        // 平台访问密钥实体配置
        modelBuilder.Entity<ProxyAccessKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PlainKey).IsRequired().HasMaxLength(500);
            entity.Property(e => e.AccessKeyHash).IsRequired().HasMaxLength(500);
            entity.Property(e => e.MaskedValue).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.AccessKeyHash, e.IsEnabled });
        });

        // 代理使用日志实体配置
        modelBuilder.Entity<ProxyUsageLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProtocolType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ForwardingMode).HasMaxLength(50);
            entity.Property(e => e.RequestModel).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AttemptedModel).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ReasoningEffort).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.IsStreamInterrupted).IsRequired();
            entity.HasIndex(e => e.RequestedAt);
            entity.HasIndex(e => e.RequestId);
            entity.HasIndex(e => new { e.RequestedAt, e.Status });
            entity.HasIndex(e => e.TargetSiteId);
            entity.HasIndex(e => e.AccessKeyId);
            entity.HasIndex(e => e.AttemptedModel);
        });

        // 结构化对话记录实体配置
        modelBuilder.Entity<ConversationTurnLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceTool).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ConversationGroupKey).IsRequired().HasMaxLength(260);
            entity.Property(e => e.RequestModel).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProtocolType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.RequestPath).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
            entity.Property(e => e.UserInputText).IsRequired().HasMaxLength(20000);
            entity.Property(e => e.AssistantOutputMarkdown).IsRequired().HasMaxLength(50000);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MetadataJson).IsRequired().HasMaxLength(20000);
            entity.Property(e => e.ConversationTitle).IsRequired().HasMaxLength(200);
            entity.Property(e => e.UserCreatedAt);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.RequestId);
            entity.HasIndex(e => e.ConversationGroupKey);
            entity.HasIndex(e => new { e.SourceTool, e.SessionId, e.CreatedAt });
        });

        // 模型健康监控配置实体配置
        modelBuilder.Entity<ModelHealthMonitor>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ModelLibraryItemId).IsUnique();
        });

        // 系统运行时设置实体配置
        modelBuilder.Entity<SystemRuntimeSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// 保存前自动补全旧 ModelType 兼容字段
    /// </summary>
    public override int SaveChanges()
    {
        ApplyLegacyModelTypeCompatibility();
        return base.SaveChanges();
    }

    /// <summary>
    /// 保存前自动补全旧 ModelType 兼容字段
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyLegacyModelTypeCompatibility();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// 异步保存前自动补全旧 ModelType 兼容字段
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyLegacyModelTypeCompatibility();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 异步保存前自动补全旧 ModelType 兼容字段
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyLegacyModelTypeCompatibility();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 旧数据库还保留 ModelType 非空列时，为新增模型补一个兼容值，避免写库失败。
    /// </summary>
    private void ApplyLegacyModelTypeCompatibility()
    {
        foreach (var entry in ChangeTracker.Entries<ModelLibraryItem>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            var modelTypeProperty = entry.Property<string>("ModelType");
            if (string.IsNullOrWhiteSpace(modelTypeProperty.CurrentValue))
            {
                modelTypeProperty.CurrentValue = "chat";
            }
        }
    }
}
