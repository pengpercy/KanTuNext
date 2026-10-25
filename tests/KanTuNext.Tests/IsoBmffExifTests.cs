using System.Buffers.Binary;
using System.Text;
using KanTuNext.Core.Metadata;

namespace KanTuNext.Tests;

public sealed class IsoBmffExifTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-heif-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    private static byte[] U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }

    /// <summary>ftyp + meta{iinf{infe v2 Exif #1}, iloc v0 → payload} + payload (4-byte offset, "Exif\0\0", TIFF).</summary>
    private static byte[] Heif(byte[] tiff)
    {
        var payload = U32(6).Concat("Exif\0\0"u8.ToArray()).Concat(tiff).ToArray();
        var ftyp = Box("ftyp", "heic"u8.ToArray(), U32(0), "mif1heic"u8.ToArray());
        var infe = Box("infe", [2, 0, 0, 0], U16(1), U16(0), "Exif"u8.ToArray(), [0]);
        var iinf = Box("iinf", [0, 0, 0, 0], U16(1), infe);
        byte[] Iloc(long offset) => Box("iloc", [0, 0, 0, 0], [0x44, 0x00], U16(1), U16(1), U16(0), U16(1), U32(offset), U32(payload.Length));
        // meta's size doesn't depend on the offset value, so lay it out once to find where the payload lands
        var meta = Box("meta", [0, 0, 0, 0], iinf, Iloc(0));
        var offset = ftyp.Length + meta.Length;
        meta = Box("meta", [0, 0, 0, 0], iinf, Iloc(offset));
        return [.. ftyp, .. meta, .. payload];
    }

    private static byte[] TiffOf(byte[] jpeg) => jpeg[(4 + 2 + 6)..^2]; // SOI + APP1 marker/length + "Exif\0\0" … EOI

    [Fact]
    public void ReadsCaptureDateFromHeifExifItem()
    {
        var path = Path.Combine(_dir, "IMG_0001.HEIC");
        File.WriteAllBytes(path, Heif(TiffOf(ExifReaderTests.JpegWithExif("2023:08:15 09:30:00", null, 6))));
        var info = ExifReader.Read(path);
        Assert.Equal(new DateTime(2023, 8, 15, 9, 30, 0), info.Taken);
        Assert.Equal(6, info.Orientation);
    }

    [Fact]
    public void ReadsJpegXlExifBox()
    {
        var path = Path.Combine(_dir, "a.jxl");
        var tiff = TiffOf(ExifReaderTests.JpegWithExif("2024:01:02 03:04:05", null, 1));
        File.WriteAllBytes(path, [.. Box("JXL ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("Exif", U32(0), tiff)]);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), ExifReader.Read(path).Taken);
    }

    [Fact]
    public void TruncatedFileIsEmpty()
    {
        var path = Path.Combine(_dir, "bad.heic");
        File.WriteAllBytes(path, Heif(TiffOf(ExifReaderTests.JpegWithExif("2023:08:15 09:30:00", null, 1)))[..60]);
        Assert.Null(ExifReader.Read(path).Taken);
    }
}
