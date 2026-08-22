using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Endpoints;
using Xunit;

namespace FamilyGallery.Api.Tests;

// 목록 응답은 인덱스 전체를 대상으로 함. 테스트마다 별도 fixture로 데이터를 격리.
public sealed class MediaEndpointsTests
{
    private const string Username = "media-user";

    private const string Password = "familypass1";

    // MediaEndpoints의 값과 동일. 어긋나면 보정·기본값 검증이 무력화됨.
    private const int DefaultPageSize = 50;

    private const int MaxPageSize = 200;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    // 촬영일시가 같은 항목이 페이지 경계에 걸리도록 구성.
    private static readonly DateTime SharedCapturedAt = new(2026, 3, 15, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task 목록_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().GetAsync("/media", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 상세_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().GetAsync("/media/1", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 목록_촬영일시_내림차순으로_반환()
    {
        using var factory = new ApiFactory();

        await factory.AddMediaAsync(
            ("oldest.jpg", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("newest.jpg", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("middle.jpg", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var client = await CreateAuthorizedClientAsync(factory);
        var page = await ReadPageAsync(client, "/media");

        Assert.Equal(["newest.jpg", "middle.jpg", "oldest.jpg"], page.Items.Select(i => i.FileName));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task 촬영일시가_같아도_커서_페이징에_중복이나_누락이_없음()
    {
        using var factory = new ApiFactory();

        // 전부 동일한 촬영일시. Id 보조 키가 없으면 경계에서 어긋남.
        var ids = await factory.AddMediaAsync(
            Enumerable.Range(0, 5)
                .Select(index => ($"same-{index}.jpg", SharedCapturedAt))
                .ToArray());

        var client = await CreateAuthorizedClientAsync(factory);
        var collected = new List<int>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var url = cursor is null ? "/media?limit=2" : $"/media?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await ReadPageAsync(client, url);

            collected.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(ids.OrderByDescending(id => id), collected);
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    [Fact]
    public async Task limit_미지정이면_기본_페이지_크기()
    {
        using var factory = new ApiFactory();

        // 기본값과 상한을 가리려면 최대 페이지 크기를 넘는 데이터가 필요.
        await SeedAsync(factory, MaxPageSize + 1);

        var client = await CreateAuthorizedClientAsync(factory);
        var page = await ReadPageAsync(client, "/media");

        Assert.Equal(DefaultPageSize, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task limit은_허용_범위로_보정()
    {
        using var factory = new ApiFactory();

        await SeedAsync(factory, MaxPageSize + 1);

        var client = await CreateAuthorizedClientAsync(factory);

        Assert.Single((await ReadPageAsync(client, "/media?limit=0")).Items);

        var clamped = await ReadPageAsync(client, "/media?limit=9999");

        Assert.Equal(MaxPageSize, clamped.Items.Count);
        Assert.NotNull(clamped.NextCursor);
    }

    [Theory]
    [InlineData("not-valid-base64!!")]
    [InlineData("YWJjZGVm")]
    [InlineData("")]
    public async Task 잘못된_커서면_400(string cursor)
    {
        using var factory = new ApiFactory();

        await factory.AddMediaAsync(("photo.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);
        var response = await client.GetAsync($"/media?cursor={Uri.EscapeDataString(cursor)}", TestToken);

        // 빈 커서는 커서 미지정과 같게 취급.
        var expected = cursor.Length == 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task 상세_없는_id면_404()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var response = await client.GetAsync("/media/9999", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 상세_등록된_항목을_반환()
    {
        using var factory = new ApiFactory();

        var ids = await factory.AddMediaAsync(("2026/photo.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);
        var item = await client.GetFromJsonAsync<MediaItemResponse>($"/media/{ids[0]}", TestToken);

        Assert.NotNull(item);
        Assert.Equal(ids[0], item.Id);
        Assert.Equal("photo.jpg", item.FileName);
        Assert.Equal(SharedCapturedAt, item.CapturedAt);
    }

    [Fact]
    public async Task 응답에_NAS_경로와_해시가_노출되지_않음()
    {
        using var factory = new ApiFactory();

        var ids = await factory.AddMediaAsync(("2026/03/secret-folder/photo.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);

        foreach (var url in new[] { "/media", $"/media/{ids[0]}" })
        {
            var json = await client.GetStringAsync(url, TestToken);

            Assert.DoesNotContain("secret-folder", json, StringComparison.Ordinal);
            Assert.DoesNotContain("relativePath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contentHash", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Task SeedAsync(ApiFactory factory, int count)
    {
        return factory.AddMediaAsync(
            Enumerable.Range(0, count)
                .Select(index => ($"item-{index}.jpg", SharedCapturedAt))
                .ToArray());
    }

    private static async Task<HttpClient> CreateAuthorizedClientAsync(ApiFactory factory)
    {
        await factory.AddUserAsync(Username, Password);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(Username, Password), TestToken);

        response.EnsureSuccessStatusCode();

        var payload = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestToken))!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        return client;
    }

    private static async Task<MediaListResponse> ReadPageAsync(HttpClient client, string url)
    {
        var page = await client.GetFromJsonAsync<MediaListResponse>(url, TestToken);

        Assert.NotNull(page);

        return page;
    }
}
