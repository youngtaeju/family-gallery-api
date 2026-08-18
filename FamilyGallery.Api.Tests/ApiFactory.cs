using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FamilyGallery.Api;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyGallery.Api.Tests;

// 테스트 클래스마다 별도 인스턴스. DB와 rate limiter 상태가 클래스 간에 섞이지 않음.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // JwtOptions의 MinLength(32) 충족용.
    private const string SigningKey = "family-gallery-integration-test-signing-key";

    private readonly string _rootPath;

    private readonly string _databasePath;

    public ApiFactory()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"fg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        _databasePath = Path.Combine(_rootPath, "test.db");

        // Services 접근 시 호스트가 생성되며 ConfigureWebHost가 적용됨.
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }

    public async Task AddUserAsync(string username, string password, UserRole role = UserRole.Viewer)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Users.Add(new User
        {
            Username = username,
            DisplayName = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // appsettings.Development.json의 .local 경로를 타지 않도록 별도 환경 사용.
        builder.UseEnvironment("Testing");

        // 기존 구성 소스 뒤에 추가되어 우선 적용.
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={_databasePath}",
                ["Jwt:SigningKey"] = SigningKey,
                ["Gallery:RootPath"] = _rootPath
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !Directory.Exists(_rootPath))
        {
            return;
        }

        // SQLite 파일 핸들이 남아 있으면 정리 실패. 임시 디렉터리라 방치해도 무해.
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
