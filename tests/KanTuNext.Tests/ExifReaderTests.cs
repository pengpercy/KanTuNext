using System.Buffers.Binary;
using System.Text;
using KanTuNext.Core.Metadata;

namespace KanTuNext.Tests;

public class ExifReaderTests
{
    /// <summary>Builds SOI + APP1(Exif) with IFD0 {Orientation, DateTime, ExifIFD} and ExifIFD {DateTimeOriginal}.</summary>
    internal static byte[] JpegWithExif(string? original, string? modified, ushort orientation, bool bigEndian = false)
    {
        var tiff = new byte[256];
        void U16(int at, ushort v) { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), v); else BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), v); }
        void U32(int at, uint v) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), v); else BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), v); }
        tiff[0] = tiff[1] = (byte)(bigEndian ? 'M' : 'I');
        U16(2, 42); U32(4, 8);
        // IFD0 at 8: 3 entries
        U16(8, 3);
        U16(10, 0x0112); U16(12, 3); U32(14, 1); U16(18, orientation);
        U16(22, 0x0132); U16(24, 2); U32(26, 20); U32(30, 120);
        U16(34, 0x8769); U16(36, 4); U32(38, 1); U32(42, 60);
        // Exif IFD at 60: 1 entry
        U16(60, 1);
        U16(62, 0x9003); U16(64, 2); U32(66, 20); U32(70, 160);
        if (modified is not null) Encoding.ASCII.GetBytes(modified).CopyTo(tiff, 120);
        if (original is not null) Encoding.ASCII.GetBytes(original).CopyTo(tiff, 160);

        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        jpeg.Add((byte)((app1.Length + 2) >> 8)); jpeg.Add((byte)((app1.Length + 2) & 0xFF));
        jpeg.AddRange(app1);
        jpeg.AddRange([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    [Fact]
    public void ReadsDateTimeOriginalAndOrientation()
    {
        var info = ExifReader.Parse(JpegWithExif("2016:06:28 10:20:30", "2017:01:01 00:00:00", 6));
        Assert.Equal(new DateTime(2016, 6, 28, 10, 20, 30), info.Taken);
        Assert.Equal(6, info.Orientation);
    }

    [Fact]
    public void BigEndianTiffWorks()
    {
        var info = ExifReader.Parse(JpegWithExif("2020:02:03 04:05:06", null, 3, bigEndian: true));
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), info.Taken);
        Assert.Equal(3, info.Orientation);
    }

    [Fact]
    public void FallsBackToDateTimeWhenOriginalMissing()
    {
        var info = ExifReader.Parse(JpegWithExif(null, "2019:12:31 23:59:59", 1));
        Assert.Equal(new DateTime(2019, 12, 31, 23, 59, 59), info.Taken);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    public void GarbageYieldsEmpty(byte[] data)
    {
        var info = ExifReader.Parse(data);
        Assert.Null(info.Taken);
    }

    [Fact]
    public void ZeroFilledDateIsIgnored() =>
        Assert.Null(ExifReader.Parse(JpegWithExif("0000:00:00 00:00:00", null, 1)).Taken);
}
