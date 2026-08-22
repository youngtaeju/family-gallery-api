using System;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyGallery.Api.Services;

// DSM·SMB로 직접 투입된 파일을 주기 스캔으로 따라감. API 업로드분은 편입 시점에 별도 등록.
public sealed class MediaIndexingService(
    IServiceScopeFactory scopeFactory,
    IOptions<IndexingOptions> options,
    ILogger<MediaIndexingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));

        try
        {
            // 기동 직후 1회 실행 후 주기 반복.
            do
            {
                await ScanAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // 호스트 종료.
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<MediaScanner>();

            var result = await scanner.ScanAsync(cancellationToken);

            logger.LogInformation(
                "미디어 스캔 완료. 추가 {Added}건, 갱신 {Updated}건, 제거 {Removed}건, 건너뜀 {Skipped}건.",
                result.Added,
                result.Updated,
                result.Removed,
                result.Skipped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 한 주기의 실패가 이후 주기를 막지 않도록 격리.
            logger.LogError(ex, "미디어 스캔 중 오류가 발생했습니다.");
        }
    }
}
