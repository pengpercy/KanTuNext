using KanTuNext.Core.Metadata;

namespace KanTuNext.Tests;

public sealed class ImageDimensionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-size-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private (int, int) Read(byte[] data)
    {
        var path = Path.Combine(_dir, "f");
        File.WriteAllBytes(path, data);
        return ImageDimensions.Read(path);
    }

    [Fact]
    public void Png() => Assert.Equal((300, 200), Read(FolderScannerTests.Png(300, 200)));

    [Fact]
    public void JpegSkipsSegmentsToTheFrameHeader() =>
        // APP0 (length 4), then SOF0: precision, height 480, width 640
        Assert.Equal((640, 480), Read([0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 17, 8, 0x01, 0xE0, 0x02, 0x80, 0, 0, 0, 0, 0, 0, 0]));

    [Fact]
    public void Gif() => Assert.Equal((16, 32), Read([.. "GIF89a"u8, 16, 0, 32, 0, 0, 0]));

    [Fact]
    public void WebPExtended() =>
        Assert.Equal((1024, 768), Read([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0x03, 0, 0xFF, 0x02, 0]));

    [Fact]
    public void UnknownIsZero() => Assert.Equal((0, 0), Read([1, 2, 3]));
}
