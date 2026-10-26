using System.Buffers.Binary;
using System.Text;
using KanTuNext.Core.Metadata;

namespace KanTuNext.Tests;

public class ExifDetailsTests
{
    /// <summary>Little-endian TIFF writer: IFDs of (tag, type, values) with out-of-line data appended at the end.</summary>
    private sealed class TiffBuilder
    {
        public readonly List<(ushort Tag, ushort Type, byte[] Data, int Count)> Ifd0 = [], Exif = [], Gps = [];

        public static (ushort, ushort, byte[], int) Ascii(ushort tag, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            return (tag, 2, bytes, bytes.Length);
        }

        public static (ushort, ushort, byte[], int) Short(ushort tag, ushort value) =>
            (tag, 3, BitConverter.GetBytes(value), 1);

        public static (ushort, ushort, byte[], int) Rational(ushort tag, params (uint Num, uint Den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8), values[i].Num);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8 + 4), values[i].Den);
            }
            return (tag, 5, bytes, values.Length);
        }

        public static (ushort, ushort, byte[], int) SRational(ushort tag, int num, int den)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, num);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), den);
            return (tag, 10, bytes, 1);
        }

        public byte[] Build()
        {
            var ifd0 = Ifd0.ToList();
            // Pointers to the sub-IFDs are filled in once their offsets are known
            if (Exif.Count > 0) ifd0.Add((0x8769, 4, new byte[4], 1));
            if (Gps.Count > 0) ifd0.Add((0x8825, 4, new byte[4], 1));
            var size = (List<(ushort, ushort, byte[], int)> d) => 2 + d.Count * 12 + 4;
            var at0 = 8;
            var atExif = at0 + size(ifd0);
            var atGps = atExif + size(Exif);
            var dataAt = atGps + size(Gps);
            var data = new List<byte>();
            var tiff = new byte[dataAt];
            "II"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), (uint)at0);
            void Write(int at, List<(ushort Tag, ushort Type, byte[] Data, int Count)> entries)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), (ushort)entries.Count);
                for (var i = 0; i < entries.Count; i++)
                {
                    var (tag, type, bytes, count) = entries[i];
                    if (tag == 0x8769) bytes = BitConverter.GetBytes((uint)atExif);
                    if (tag == 0x8825) bytes = BitConverter.GetBytes((uint)atGps);
                    var e = at + 2 + i * 12;
                    BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(e), tag);
                    BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(e + 2), type);
                    BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(e + 4), (uint)count);
                    if (bytes.Length <= 4) bytes.CopyTo(tiff, e + 8);
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(e + 8), (uint)(dataAt + data.Count));
                        data.AddRange(bytes);
                    }
                }
            }
            Write(at0, ifd0);
            Write(atExif, Exif);
            Write(atGps, Gps);
            return [.. tiff, .. data];
        }
    }

    private static ExifDetails Sample()
    {
        var b = new TiffBuilder();
        b.Ifd0.Add(TiffBuilder.Ascii(0x010F, "Apple"));
        b.Ifd0.Add(TiffBuilder.Ascii(0x0110, "iPhone 15 Pro"));
        b.Ifd0.Add(TiffBuilder.Short(0x0112, 6));
        b.Exif.Add(TiffBuilder.Rational(0x829A, (1, 120)));
        b.Exif.Add(TiffBuilder.Rational(0x829D, (178, 100)));
        b.Exif.Add(TiffBuilder.Short(0x8827, 80));
        b.Exif.Add(TiffBuilder.SRational(0x9204, -1, 3));
        b.Exif.Add(TiffBuilder.Rational(0x920A, (686, 100)));
        b.Exif.Add(TiffBuilder.Short(0x9209, 0x18));
        b.Exif.Add(TiffBuilder.Short(0xA405, 24));
        b.Exif.Add(TiffBuilder.Ascii(0xA434, "iPhone 15 Pro back triple camera 6.86mm f/1.78"));
        b.Exif.Add(TiffBuilder.Ascii(0x9003, "2024:05:01 09:30:15"));
        b.Exif.Add(TiffBuilder.Ascii(0x9011, "+08:00"));
        b.Gps.Add(TiffBuilder.Ascii(0x0001, "N"));
        b.Gps.Add(TiffBuilder.Rational(0x0002, (31, 1), (13, 1), (4548, 100)));
        b.Gps.Add(TiffBuilder.Ascii(0x0003, "W"));
        b.Gps.Add(TiffBuilder.Rational(0x0004, (121, 1), (30, 1), (0, 1)));
        b.Gps.Add(TiffBuilder.Short(0x0005, 1));
        b.Gps.Add(TiffBuilder.Rational(0x0006, (125, 10)));
        return ExifDetails.Parse(b.Build());
    }

    private static string Value(ExifDetails d, string name) => d.Entries.Single(e => e.Name == name).Value;

    [Fact]
    public void ReadsCameraAndExposure()
    {
        var d = Sample();
        Assert.Equal("Apple iPhone 15 Pro", d.Camera);
        Assert.Equal(80, d.Iso);
        Assert.Equal("1/120 秒", Value(d, "曝光时间"));
        Assert.Equal("ƒ/1.8", Value(d, "光圈"));
        Assert.Equal("6.86 mm", Value(d, "焦距"));
        Assert.Equal("24 mm（35mm 等效）", Value(d, "等效焦距"));
        Assert.Equal("−0.33 EV", Value(d, "曝光补偿"));
        Assert.Equal("未闪光，自动", Value(d, "闪光灯"));
        Assert.Equal("顺时针旋转 90°", Value(d, "方向"));
        Assert.Equal("2024年5月1日 09:30:15（UTC+08:00）", Value(d, "拍摄时间"));
    }

    [Fact]
    public void CameraNameDoesNotRepeatTheMaker()
    {
        var b = new TiffBuilder();
        b.Ifd0.Add(TiffBuilder.Ascii(0x010F, "Canon"));
        b.Ifd0.Add(TiffBuilder.Ascii(0x0110, "Canon EOS R5"));
        Assert.Equal("Canon EOS R5", ExifDetails.Parse(b.Build()).Camera);
        Assert.Equal("Apple iPhone 15 Pro", Sample().Camera);
    }

    [Fact]
    public void ReadsGpsWithHemispheres()
    {
        var d = Sample();
        Assert.Equal(31.229300, d.Latitude!.Value, 5);
        Assert.Equal(-121.5, d.Longitude!.Value, 5);
        Assert.Equal("北纬 31°13′45.48″", Value(d, "纬度"));
        Assert.Equal("西经 121°30′0″", Value(d, "经度"));
        Assert.Equal("-12.5 m", Value(d, "海拔"));
    }

    [Theory]
    [InlineData(1.0 / 4000, "1/4000")]
    [InlineData(0.5, "0.5")]
    [InlineData(30, "30")]
    public void FormatsShutterSpeeds(double seconds, string expected) => Assert.Equal(expected, ExifDetails.Shutter(seconds));

    [Fact]
    public void GarbageGivesNoEntries()
    {
        Assert.Empty(ExifDetails.Parse([1, 2, 3]).Entries);
        Assert.Empty(ExifDetails.Parse("II*\0\xFF\xFF\xFF\x7F"u8.ToArray()).Entries);
    }

    [Fact]
    public void ReadsExifFromPngChunk()
    {
        var b = new TiffBuilder();
        b.Ifd0.Add(TiffBuilder.Ascii(0x010F, "Sony"));
        var tiff = b.Build();
        var png = new List<byte>(MetadataChunks.PngSignature.ToArray());
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            png.AddRange(length);
            png.AddRange(Encoding.ASCII.GetBytes(type));
            png.AddRange(data);
            png.AddRange(new byte[4]); // CRC, not checked
        }
        Chunk("IHDR", new byte[13]);
        Chunk("eXIf", tiff);
        Chunk("IEND", []);
        var path = Path.Combine(Path.GetTempPath(), $"kantunext-exif-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png.ToArray());
        try
        {
            Assert.Equal(tiff, ExifReader.ReadTiff(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsIccV2Description()
    {
        var icc = new byte[200];
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(128), 1);
        "desc"u8.CopyTo(icc.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(136), 150);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(140), 40);
        "desc"u8.CopyTo(icc.AsSpan(150));
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(158), 11);
        "Display P3\0"u8.CopyTo(icc.AsSpan(162));
        Assert.Equal("Display P3", MetadataChunks.IccDescription(icc));
    }
}
