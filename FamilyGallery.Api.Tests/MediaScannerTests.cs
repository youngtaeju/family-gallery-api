using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyGallery.Api.Tests;

// 스캔은 DB와 갤러리 트리를 함께 바꿈. 테스트마다 별도 fixture로 상태를 격리.
public sealed class MediaScannerTests
{
    private static readonly DateTime CapturedLocal = new(2026, 3, 15, 13, 5, 6, DateTimeKind.Unspecified);

    [Fact]
    public async Task 스캔하면_화이트리스트_확장자만_인덱싱()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("a"));
        WriteGalleryFile(factory, "2026/clip.mp4", MediaFixtures.CreateOpaqueBytes("b"));
        WriteGalleryFile(factory, "notes.txt", MediaFixtures.CreateOpaqueBytes("c"));
        WriteGalleryFile(factory, "archive.zip", MediaFixtures.CreateOpaqueBytes("d"));

        var result = await factory.ScanAsync();

        Assert.Equal(2, result.Added);

        var items = await ReadIndexAsync(factory);

        Assert.Equal(["2026/clip.mp4", "photo.jpg"], items.Select(i => i.RelativePath));
        Assert.Equal(MediaType.Video, items.Single(i => i.RelativePath == "2026/clip.mp4").MediaType);
        Assert.Equal(MediaType.Image, items.Single(i => i.RelativePath == "photo.jpg").MediaType);
    }

    [Fact]
    public async Task 점으로_시작하는_디렉터리는_순회에서_제외()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("a"));
        WriteGalleryFile(factory, ".uploads/staging.jpg", MediaFixtures.CreateOpaqueBytes("b"));
        WriteGalleryFile(factory, ".trash/deleted.jpg", MediaFixtures.CreateOpaqueBytes("c"));

        var result = await factory.ScanAsync();

        Assert.Equal(1, result.Added);
        Assert.Equal(["photo.jpg"], (await ReadIndexAsync(factory)).Select(i => i.RelativePath));
    }

    [Fact]
    public async Task EXIF_오프셋이_있으면_해당_오프셋으로_UTC_변환()
    {
        // 표준시 구성과 무관하게 태그 값이 우선함을 확인.
        using var factory = new ApiFactory(timeZone: "UTC");

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateJpeg(800, 600, CapturedLocal, "+09:00"));

        await factory.ScanAsync();

        var item = Assert.Single(await ReadIndexAsync(factory));

        Assert.Equal(new DateTime(2026, 3, 15, 4, 5, 6, DateTimeKind.Utc), item.CapturedAt);
    }

    [Fact]
    public async Task EXIF_오프셋이_없으면_구성된_표준시로_해석()
    {
        using var factory = new ApiFactory(timeZone: "Asia/Seoul");

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateJpeg(800, 600, CapturedLocal));

        await factory.ScanAsync();

        var item = Assert.Single(await ReadIndexAsync(factory));

        Assert.Equal(new DateTime(2026, 3, 15, 4, 5, 6, DateTimeKind.Utc), item.CapturedAt);
    }

    [Fact]
    public async Task EXIF가_없으면_mtime을_촬영일시로_사용()
    {
        using var factory = new ApiFactory();

        var modifiedAt = new DateTime(2025, 7, 1, 9, 30, 0, DateTimeKind.Utc);

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateJpeg(800, 600), modifiedAt);

        await factory.ScanAsync();

        var item = Assert.Single(await ReadIndexAsync(factory));

        Assert.Equal(modifiedAt, item.CapturedAt);
        Assert.Equal(modifiedAt, item.FileModifiedAt);
    }

    [Fact]
    public async Task 촬영일시가_성립하지_않으면_mtime으로_대체()
    {
        using var factory = new ApiFactory(timeZone: "UTC");

        var modifiedAt = new DateTime(2025, 7, 1, 9, 30, 0, DateTimeKind.Utc);
        var brokenCapturedAt = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateJpeg(800, 600, brokenCapturedAt, "+00:00"), modifiedAt);

        await factory.ScanAsync();

        Assert.Equal(modifiedAt, Assert.Single(await ReadIndexAsync(factory)).CapturedAt);
    }

    [Fact]
    public async Task 해상도를_추출하고_회전_방향이면_교환()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "upright.jpg", MediaFixtures.CreateJpeg(800, 600, CapturedLocal, "+09:00", orientation: 1));
        WriteGalleryFile(factory, "rotated.jpg", MediaFixtures.CreateJpeg(800, 600, CapturedLocal, "+09:00", orientation: 6, filler: 0x01));

        await factory.ScanAsync();

        var items = await ReadIndexAsync(factory);

        var upright = items.Single(i => i.RelativePath == "upright.jpg");
        var rotated = items.Single(i => i.RelativePath == "rotated.jpg");

        Assert.Equal((800, 600), (upright.Width, upright.Height));
        Assert.Equal((600, 800), (rotated.Width, rotated.Height));
    }

    [Fact]
    public async Task 변경되지_않은_파일은_다시_인덱싱하지_않음()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("a"));

        Assert.Equal(1, (await factory.ScanAsync()).Added);

        var indexedAt = Assert.Single(await ReadIndexAsync(factory)).IndexedAt;
        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);

        // 재인덱싱이 없었음을 IndexedAt 유지로 확인.
        Assert.Equal(indexedAt, Assert.Single(await ReadIndexAsync(factory)).IndexedAt);
    }

    [Fact]
    public async Task 내용이_바뀌면_갱신()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("before"));
        await factory.ScanAsync();

        var before = Assert.Single(await ReadIndexAsync(factory));

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("after-and-longer"));

        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);

        var after = Assert.Single(await ReadIndexAsync(factory));

        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(before.ContentHash, after.ContentHash);
    }

    [Fact]
    public async Task 파일이_사라지면_인덱스에서_제거()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "keep.jpg", MediaFixtures.CreateOpaqueBytes("a"));
        WriteGalleryFile(factory, "remove.jpg", MediaFixtures.CreateOpaqueBytes("b"));
        await factory.ScanAsync();

        File.Delete(Path.Combine(factory.GalleryPath, "remove.jpg"));

        var result = await factory.ScanAsync();

        Assert.Equal(1, result.Removed);
        Assert.Equal(["keep.jpg"], (await ReadIndexAsync(factory)).Select(i => i.RelativePath));
    }

    [Fact]
    public async Task 파일을_옮기면_새_경로로_다시_인덱싱()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("a"));
        await factory.ScanAsync();

        System.IO.Directory.CreateDirectory(Path.Combine(factory.GalleryPath, "2026"));
        File.Move(
            Path.Combine(factory.GalleryPath, "photo.jpg"),
            Path.Combine(factory.GalleryPath, "2026", "photo.jpg"));

        var result = await factory.ScanAsync();

        // 삭제를 먼저 반영하지 않으면 이전 경로 행이 남아 중복 해시 제약에 걸림.
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(["2026/photo.jpg"], (await ReadIndexAsync(factory)).Select(i => i.RelativePath));
    }

    [Fact]
    public async Task 내용이_같은_파일은_한_번만_인덱싱()
    {
        using var factory = new ApiFactory();

        var content = MediaFixtures.CreateOpaqueBytes("same");

        WriteGalleryFile(factory, "original.jpg", content);
        WriteGalleryFile(factory, "copy/original.jpg", content);

        var result = await factory.ScanAsync();

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Single(await ReadIndexAsync(factory));
    }

    [Fact]
    public async Task 루트가_비어_있으면_인덱스를_보존()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("a"));
        await factory.ScanAsync();

        // 마운트 유실 시 bind mount 지점이 빈 디렉터리로 남는 상황.
        File.Delete(Path.Combine(factory.GalleryPath, "photo.jpg"));

        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Removed);
        Assert.Single(await ReadIndexAsync(factory));
    }

    [Fact]
    public async Task 루트가_없으면_스캔을_건너뜀()
    {
        using var factory = new ApiFactory();

        WriteGalleryFile(factory, "photo.jpg", MediaFixtures.CreateOpaqueBytes("a"));
        await factory.ScanAsync();

        System.IO.Directory.Delete(factory.GalleryPath, recursive: true);

        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Removed);
        Assert.Single(await ReadIndexAsync(factory));
    }

    private static void WriteGalleryFile(ApiFactory factory, string relativePath, byte[] content, DateTime? modifiedAt = null)
    {
        MediaFixtures.Write(Path.Combine(factory.GalleryPath, relativePath), content, modifiedAt);
    }

    private static async Task<List<MediaItem>> ReadIndexAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .MediaItems.AsNoTracking()
            .OrderBy(m => m.RelativePath)
            .ToListAsync();
    }
}
