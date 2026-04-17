using AITool.Domain.Models;
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

        base.OnModelCreating(modelBuilder);
    }
}
