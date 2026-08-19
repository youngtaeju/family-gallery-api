using System;
using FamilyGallery.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyGallery.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // 엔티티 추가 시 개별 지정 없이 모든 DateTime 속성에 적용.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.Username).HasMaxLength(64);
            entity.Property(u => u.PasswordHash).HasMaxLength(256);
            entity.Property(u => u.DisplayName).HasMaxLength(64);

            // DB 직접 조회 시 가독성 확보. enum 값 재배치에도 안전.
            entity.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.Property(t => t.TokenHash).HasMaxLength(128);

            entity.HasOne(t => t.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
