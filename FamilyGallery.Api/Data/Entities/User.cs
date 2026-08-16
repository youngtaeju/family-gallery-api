using System;
using System.Collections.Generic;

namespace FamilyGallery.Api.Data.Entities;

// 모든 사용자가 동일한 viewer 권한. role 컬럼 없음.
public class User
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
