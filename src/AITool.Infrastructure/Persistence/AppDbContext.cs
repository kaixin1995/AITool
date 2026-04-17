using AITool.Domain.Detection;
using AITool.Domain.Models;
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

        base.OnModelCreating(modelBuilder);
    }
}
