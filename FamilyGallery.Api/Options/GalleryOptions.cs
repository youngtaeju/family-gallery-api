using System.ComponentModel.DataAnnotations;

namespace FamilyGallery.Api.Options;

public sealed class GalleryOptions
{
    public const string SectionName = "Gallery";

    // NAS shared folder 마운트 지점. 읽기 전용.
    [Required]
    public string RootPath { get; init; } = string.Empty;
}
