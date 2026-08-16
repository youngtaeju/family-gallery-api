using System;

namespace FamilyGallery.Api.Data.Entities;

public class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    // 원문 미저장. 조회는 해시 비교.
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User? User { get; set; }
}
