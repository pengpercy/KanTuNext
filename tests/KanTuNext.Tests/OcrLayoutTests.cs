using KanTuNext.Core.Ocr;

namespace KanTuNext.Tests;

public class OcrLayoutTests
{
    private static OcrLine L(string text, double x, double y, double w = 0.2, double h = 0.04, float c = 0.9f) =>
        new(text, new OcrRect(x, y, w, h), c);

    [Fact]
    public void JoinsSideBySideFragmentsLeftToRightAndRowsTopToBottom()
    {
        // Engine order is scrambled: right column first, second row before first
        var result = new OcrResult([
            L("世界", 0.5, 0.101), L("第二行", 0.1, 0.3), L("你好", 0.1, 0.1), L("尾", 0.6, 0.302),
        ]);
        Assert.Equal("你好 世界\n第二行 尾", result.Text);
    }

    [Fact]
    public void SlightlyOffsetLinesStillShareARow()
    {
        var rows = OcrLayout.Rows([L("a", 0.1, 0.100), L("b", 0.4, 0.112), L("c", 0.1, 0.2)]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows[0].Select(l => l.Text));
    }

    [Fact]
    public void ScorePrefersMoreConfidentText()
    {
        var weak = new OcrResult([L("营业执照", 0, 0, c: 0.3f)]);
        var strong = new OcrResult([L("营业执照", 0, 0, c: 0.95f)]);
        Assert.True(strong.Score > weak.Score);
        Assert.Equal("", OcrResult.Empty.Text);
    }
}

public class OcrTextTests
{
    [Theory]
    [InlineData(new[] { "离", "线", "看", "图", "KanTuNext", "0.1" }, "离线看图 KanTuNext 0.1")]
    [InlineData(new[] { "电", "话", "010-123", "邮", "编" }, "电话 010-123 邮编")]
    [InlineData(new[] { "左", "栏", "：", "地", "点" }, "左栏：地点")]
    [InlineData(new[] { "Offline", "image", "viewer" }, "Offline image viewer")]
    public void JoinsCjkWithoutSpaces(string[] words, string expected) => Assert.Equal(expected, OcrText.JoinWords(words));

    [Fact]
    public void CollapsesSpacesInsideAJoinedLine() =>
        Assert.Equal("支持 HEIC、AVIF 等格式", OcrText.CollapseCjkSpaces("支 持 HEIC 、 AVIF 等 格 式"));
}
