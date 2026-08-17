using System.ComponentModel.DataAnnotations;

namespace FamilyGallery.Api.Options;

public sealed class GalleryOptions
{
    public const string SectionName = "Gallery";

    // NAS shared folder 마운트 지점. 원본 트리와 업로드 스테이징·휴지통의 공통 루트.
    [Required]
    public string RootPath { get; init; } = string.Empty;
}
