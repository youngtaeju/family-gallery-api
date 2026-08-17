using System;
using System.IO;
using System.Text;
using FamilyGallery.Api.Cli;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Endpoints;
using FamilyGallery.Api.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FamilyGallery.Api;

public class Program
{
    public static int Main(string[] args)
    {
        var isUserCommand = UserCommands.Matches(args);

        // CLI 인자는 위치 인자를 포함해 구성 바인더가 파싱하지 못함. 해당 모드에서는 전달하지 않음.
        var builder = WebApplication.CreateBuilder(isUserCommand ? [] : args);

        if (isUserCommand)
        {
            // 마이그레이션 SQL 로그가 프롬프트를 가림. 결과와 오류는 표준 출력으로 직접 전달.
            builder.Logging.ClearProviders();
        }

        builder.Services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<GalleryOptions>()
            .BindConfiguration(GalleryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

        // TLS 종료는 Cloudflare Tunnel 담당. 컨테이너는 평문 HTTP만 수신하므로 HTTPS 리디렉션 없음.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Tunnel이 유일한 외부 진입 경로. 컨테이너 네트워크 주소가 가변이라 기본 loopback 제한 해제.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            // 엔드포인트 추가 시 인증 누락 방지. 공개 경로만 명시적으로 AllowAnonymous.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        builder.Services.AddOpenApi();

        var app = builder.Build();

        InitializeDatabase(app);

        if (isUserCommand)
        {
            return UserCommands.Execute(app.Services, args);
        }

        app.UseForwardedHeaders();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthEndpoints();

        app.Run();

        return 0;
    }

    // 단일 인스턴스 배포. 기동 시점 마이그레이션 적용으로 충분.
    private static void InitializeDatabase(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EnsureDataSourceDirectory(db.Database.GetConnectionString());

        // journal_mode는 미설정. EF Core가 생성하는 SQLite DB는 WAL이 기본값.
        db.Database.Migrate();
    }

    // SQLite는 상위 디렉터리를 만들지 않음. 최초 실행 시 연결 실패 방지.
    private static void EnsureDataSourceDirectory(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;

        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
