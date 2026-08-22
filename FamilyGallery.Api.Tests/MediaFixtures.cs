using System;
using System.IO;
using System.Text;

namespace FamilyGallery.Api.Tests;

// 실제 메타데이터 파싱 경로용 JPEG 바이트 조립. 바이너리 자산 보관 방지.
public static class MediaFixtures
{
    // EXIF 태그 번호.
    private const ushort OrientationTag = 274;

    private const ushort ExifIfdPointerTag = 34665;

    private const ushort DateTimeOriginalTag = 36867;

    private const ushort OffsetTimeOriginalTag = 36881;

    private const ushort PixelXDimensionTag = 40962;

    private const ushort PixelYDimensionTag = 40963;

    // TIFF 값 타입.
    private const ushort AsciiType = 2;

    private const ushort ShortType = 3;

    private const ushort LongType = 4;

    /// <summary>지정한 해상도와 EXIF를 갖는 JPEG 생성. capturedAt이 null이면 EXIF 세그먼트 자체를 넣지 않음.</summary>
    public static byte[] CreateJpeg(
        int width,
        int height,
        DateTime? capturedAt = null,
        string? utcOffset = null,
        ushort orientation = 1,
        byte filler = 0x00)
    {
        using var jpeg = new MemoryStream();

        WriteMarker(jpeg, 0xD8);

        if (capturedAt is not null)
        {
            WriteExifSegment(jpeg, capturedAt.Value, utcOffset, orientation, width, height);
        }

        WriteStartOfFrame(jpeg, width, height);

        // 내용 해시를 서로 다르게 만들기 위한 주석 세그먼트.
        WriteComment(jpeg, filler);

        WriteMarker(jpeg, 0xD9);

        return jpeg.ToArray();
    }

    // 메타데이터 추출 실패 시 mtime 대체 경로 검증용.
    public static byte[] CreateOpaqueBytes(string seed)
    {
        return Encoding.UTF8.GetBytes($"not-a-real-media-file:{seed}");
    }

    public static void Write(string path, byte[] content, DateTime? lastWriteTimeUtc = null)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        if (lastWriteTimeUtc is not null)
        {
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc.Value);
        }
    }

    private static void WriteMarker(Stream stream, byte marker)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
    }

    // 해상도는 SOF0에 실림. MetadataExtractor의 JpegDirectory가 이 값을 읽음.
    private static void WriteStartOfFrame(Stream stream, int width, int height)
    {
        WriteMarker(stream, 0xC0);
        WriteBigEndianUInt16(stream, 11);
        stream.WriteByte(8);
        WriteBigEndianUInt16(stream, (ushort)height);
        WriteBigEndianUInt16(stream, (ushort)width);
        stream.WriteByte(1);

        // 컴포넌트 1개: 식별자, 샘플링 계수, 양자화 테이블 번호.
        stream.WriteByte(1);
        stream.WriteByte(0x11);
        stream.WriteByte(0);
    }

    private static void WriteComment(Stream stream, byte filler)
    {
        WriteMarker(stream, 0xFE);
        WriteBigEndianUInt16(stream, 3);
        stream.WriteByte(filler);
    }

    private static void WriteExifSegment(
        Stream stream,
        DateTime capturedAt,
        string? utcOffset,
        ushort orientation,
        int width,
        int height)
    {
        var tiff = BuildTiff(capturedAt, utcOffset, orientation, width, height);

        WriteMarker(stream, 0xE1);
        WriteBigEndianUInt16(stream, (ushort)(2 + 6 + tiff.Length));
        stream.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
        stream.Write(tiff);
    }

    private static byte[] BuildTiff(
        DateTime capturedAt,
        string? utcOffset,
        ushort orientation,
        int width,
        int height)
    {
        // EXIF DateTimeOriginal은 오프셋을 담지 않는 고정 폭 문자열.
        var capturedBytes = Encoding.ASCII.GetBytes(capturedAt.ToString("yyyy:MM:dd HH:mm:ss") + "\0");
        var offsetBytes = utcOffset is null ? [] : Encoding.ASCII.GetBytes(utcOffset + "\0");

        const uint ifd0Offset = 8;
        const uint ifd0Size = 2 + (2 * 12) + 4;

        var subIfdOffset = ifd0Offset + ifd0Size;
        var subEntryCount = utcOffset is null ? 3 : 4;
        var subIfdSize = (uint)(2 + (subEntryCount * 12) + 4);

        var capturedOffset = subIfdOffset + subIfdSize;
        var offsetTimeOffset = capturedOffset + (uint)capturedBytes.Length;

        using var tiff = new MemoryStream();
        var writer = new BinaryWriter(tiff);

        // 리틀엔디언 TIFF 헤더.
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(ifd0Offset);

        writer.Write((ushort)2);
        WriteEntry(writer, OrientationTag, ShortType, 1, orientation);
        WriteEntry(writer, ExifIfdPointerTag, LongType, 1, subIfdOffset);
        writer.Write(0u);

        writer.Write((ushort)subEntryCount);
        WriteEntry(writer, DateTimeOriginalTag, AsciiType, (uint)capturedBytes.Length, capturedOffset);

        if (utcOffset is not null)
        {
            WriteEntry(writer, OffsetTimeOriginalTag, AsciiType, (uint)offsetBytes.Length, offsetTimeOffset);
        }

        WriteEntry(writer, PixelXDimensionTag, LongType, 1, (uint)width);
        WriteEntry(writer, PixelYDimensionTag, LongType, 1, (uint)height);
        writer.Write(0u);

        writer.Write(capturedBytes);
        writer.Write(offsetBytes);
        writer.Flush();

        return tiff.ToArray();
    }

    // SHORT 단일 값은 4바이트 값 필드에 직접 기록.
    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);

        if (type == ShortType && count == 1)
        {
            writer.Write((ushort)value);
            writer.Write((ushort)0);
            return;
        }

        writer.Write(value);
    }

    // JPEG 세그먼트 길이는 빅엔디언.
    private static void WriteBigEndianUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }
}
