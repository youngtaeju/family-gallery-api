using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.RateLimiting;
using FamilyGallery.Api.Cli;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Endpoints;
using FamilyGallery.Api.Options;
using FamilyGallery.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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
    // 가족 단위 사용량 기준. 정상 사용자의 재시도는 허용하고 무차별 대입만 차단.
    private const int AuthRequestsPerWindow = 20;

    private static readonly TimeSpan AuthRateLimitWindow = TimeSpan.FromMinutes(1);

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

        builder.Services.AddSingleton<TokenService>();

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

                // 기본 인바운드 매핑으로 sub, role 클레임명이 WS-* URI로 변환되는 문제 방지
                bearer.MapInboundClaims = false;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = TokenService.NameClaimType,
                    RoleClaimType = TokenService.RoleClaimType
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            // 엔드포인트 추가 시 인증 누락 방지. 공개 경로만 명시적으로 AllowAnonymous.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        builder.Services.AddRateLimiter(options =>
        {
            // 인증 없이 반복 호출 가능한 경로만 대상. 인증 필요 경로는 토큰 자체가 관문.
            options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = AuthRequestsPerWindow,
                        Window = AuthRateLimitWindow,
                        QueueLimit = 0
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await Results
                    .Problem(detail: "요청이 너무 잦습니다. 잠시 후 다시 시도하세요.",
                        statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext);
            };
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

        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthEndpoints();
        app.MapAuthEndpoints();

        app.Run();

        return 0;
    }

    // KnownProxies 미지정으로 X-Forwarded-For는 클라이언트가 위조 가능.
    // Tunnel 경유 트래픽에서 Cloudflare가 항상 덮어쓰는 헤더를 우선 사용.
    private static string GetClientKey(HttpContext context)
    {
        var cloudflareIp = context.Request.Headers["CF-Connecting-IP"].ToString();

        if (!string.IsNullOrWhiteSpace(cloudflareIp))
        {
            return cloudflareIp;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
