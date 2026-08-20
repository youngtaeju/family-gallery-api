using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FamilyGallery.Api.Endpoints;

public static class MediaEndpoints
{
    private const int DefaultPageSize = 50;

    private const int MaxPageSize = 200;

    private const string InvalidCursorMessage = "cursor 값이 올바르지 않습니다.";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/media");

        // 조회 범위는 사용자별로 구분하지 않음. 인증만 통과하면 전체 미디어 접근 가능.
        group.MapGet("/", ListAsync).WithName("ListMedia");
        group.MapGet("/{id:int}", GetAsync).WithName("GetMedia");

        return app;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        string? cursor = null,
        int? limit = null)
    {
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.MediaItems.AsNoTracking();

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryParseCursor(cursor, out var capturedAt, out var id))
            {
                return Results.Problem(detail: InvalidCursorMessage, statusCode: StatusCodes.Status400BadRequest);
            }

            // 촬영일시가 같은 항목이 페이지 경계에서 누락되거나 중복되지 않도록 Id를 보조 키로 사용.
            query = query.Where(m => m.CapturedAt < capturedAt || (m.CapturedAt == capturedAt && m.Id < id));
        }

        // 다음 페이지 존재 여부 판단용으로 1건 더 조회.
        var items = await query
            .OrderByDescending(m => m.CapturedAt)
            .ThenByDescending(m => m.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = items.Count > pageSize;

        if (hasMore)
        {
            items.RemoveAt(pageSize);
        }

        return Results.Ok(new MediaListResponse(
            items.Select(ToResponse).ToList(),
            hasMore ? CreateCursor(items[^1]) : null));
    }

    private static async Task<IResult> GetAsync(int id, AppDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

        return item is null
            ? Results.Problem(detail: "미디어를 찾을 수 없습니다.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ToResponse(item));
    }

    // 클라이언트가 내부 구조에 의존하지 않도록 불투명 문자열로 전달.
    private static string CreateCursor(MediaItem item)
    {
        return Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($"{item.CapturedAt.Ticks.ToString(CultureInfo.InvariantCulture)}_{item.Id.ToString(CultureInfo.InvariantCulture)}"));
    }

    // 밀리초 등으로 절삭하면 같은 초 안의 항목이 경계에서 어긋남. tick 그대로 왕복.
    private static bool TryParseCursor(string cursor, out DateTime capturedAt, out int id)
    {
        capturedAt = default;
        id = 0;

        byte[] decoded;

        try
        {
            decoded = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(decoded).Split('_');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        capturedAt = new DateTime(ticks, DateTimeKind.Utc);

        return true;
    }

    // NAS 실제 경로는 응답에 싣지 않음. 미디어 참조는 Id 기반.
    private static MediaItemResponse ToResponse(MediaItem item)
    {
        return new MediaItemResponse(
            item.Id,
            item.MediaType.ToString(),
            item.OriginalFileName,
            item.FileSize,
            item.CapturedAt,
            item.Width,
            item.Height,
            item.DurationMs);
    }
}

public sealed record MediaItemResponse(
    int Id,
    string MediaType,
    string FileName,
    long FileSize,
    DateTime CapturedAt,
    int? Width,
    int? Height,
    int? DurationMs);

// nextCursor가 null이면 마지막 페이지.
public sealed record MediaListResponse(IReadOnlyList<MediaItemResponse> Items, string? NextCursor);
