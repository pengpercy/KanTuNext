using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KanTuNext.Core.Metadata;

/// <summary>One decoded tag, ready to show: which group it belongs to, its name and its formatted value.</summary>
public sealed record ExifEntry(ExifGroup Group, string Name, string Value);

/// <summary>Groups of the details panel, in display order.</summary>
public enum ExifGroup
{
    Camera,
    Exposure,
    Image,
    Time,
    Location,
    Other,
}

/// <summary>
/// Every EXIF field worth showing, decoded from a TIFF block (<see cref="ExifReader.ReadTiff"/>): IFD0, the Exif
/// IFD and the GPS IFD. The headline values (camera, lens, exposure) are also kept as properties for the panel's
/// summary card. Maker notes and thumbnails are skipped.
/// </summary>
public sealed class ExifDetails
{
    public string? Make { get; private set; }
    public string? Model { get; private set; }
    public string? Lens { get; private set; }
    public int? Iso { get; private set; }
    public double? FNumber { get; private set; }
    public double? ExposureTime { get; private set; }
    public double? FocalLength { get; private set; }
    public double? ExposureBias { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public int? PixelWidth { get; private set; }
    public int? PixelHeight { get; private set; }
    public List<ExifEntry> Entries { get; } = [];

    /// <summary>"Apple iPhone 15 Pro", without repeating the maker when the model already names it.</summary>
    public string? Camera => Model is null ? Make
        : Make is null || Model.StartsWith(Make.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? Model
        : $"{Make} {Model}";

    private const ushort IfdExif = 0x8769, IfdGps = 0x8825;

    public static ExifDetails Parse(ReadOnlySpan<byte> tiff)
    {
        var details = new ExifDetails();
        if (tiff.Length < 8 || !(tiff[0] == 'I' && tiff[1] == 'I' || tiff[0] == 'M' && tiff[1] == 'M')) return details;
        var reader = new Reader(tiff.ToArray(), tiff[0] == 'I');
        var ifd0 = reader.Ifd(reader.U32(4));
        var exif = ifd0.TryGetValue(IfdExif, out var e) ? reader.Ifd(e.FirstU32(reader)) : [];
        var gps = ifd0.TryGetValue(IfdGps, out var g) ? reader.Ifd(g.FirstU32(reader)) : [];
        details.Decode(reader, ifd0, exif, gps);
        return details;
    }

    private void Decode(Reader r, Dictionary<ushort, Field> ifd0, Dictionary<ushort, Field> exif, Dictionary<ushort, Field> gps)
    {
        string? Text(Dictionary<ushort, Field> ifd, ushort tag) => ifd.TryGetValue(tag, out var f) ? f.Text(r) : null;
        double? Number(Dictionary<ushort, Field> ifd, ushort tag) => ifd.TryGetValue(tag, out var f) ? f.Number(r, 0) : null;
        int? Int(Dictionary<ushort, Field> ifd, ushort tag) => Number(ifd, tag) is { } v ? (int)v : null;
        void Add(ExifGroup group, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Entries.Add(new ExifEntry(group, name, value.Trim()));
        }

        // ---- Camera
        Make = Text(ifd0, 0x010F);
        Model = Text(ifd0, 0x0110);
        Lens = Text(exif, 0xA434);
        Add(ExifGroup.Camera, "制造商", Make);
        Add(ExifGroup.Camera, "型号", Model);
        Add(ExifGroup.Camera, "镜头制造商", Text(exif, 0xA433));
        Add(ExifGroup.Camera, "镜头型号", Lens);
        if (exif.TryGetValue(0xA432, out var spec) && spec.Count >= 4)
            Add(ExifGroup.Camera, "镜头规格", LensSpecification(spec.Number(r, 0), spec.Number(r, 1), spec.Number(r, 2), spec.Number(r, 3)));
        Add(ExifGroup.Camera, "机身序列号", Text(exif, 0xA431));
        Add(ExifGroup.Camera, "镜头序列号", Text(exif, 0xA435));
        Add(ExifGroup.Camera, "机主", Text(exif, 0xA430));
        Add(ExifGroup.Camera, "软件", Text(ifd0, 0x0131));

        // ---- Exposure
        ExposureTime = Number(exif, 0x829A);
        FNumber = Number(exif, 0x829D);
        Iso = Int(exif, 0x8827);
        FocalLength = Number(exif, 0x920A);
        ExposureBias = Number(exif, 0x9204);
        Add(ExifGroup.Exposure, "曝光时间", ExposureTime is { } t ? Shutter(t) + " 秒" : null);
        Add(ExifGroup.Exposure, "光圈", FNumber is { } f ? Aperture(f) : null);
        Add(ExifGroup.Exposure, "感光度", Iso is { } iso ? $"ISO {iso}" : null);
        Add(ExifGroup.Exposure, "焦距", FocalLength is { } fl ? $"{Num(fl)} mm" : null);
        Add(ExifGroup.Exposure, "等效焦距", Int(exif, 0xA405) is { } f35 and > 0 ? $"{f35} mm（35mm 等效）" : null);
        Add(ExifGroup.Exposure, "曝光补偿", ExposureBias is { } bias ? Bias(bias) : null);
        Add(ExifGroup.Exposure, "曝光程序", Lookup(Int(exif, 0x8822), ExposurePrograms));
        Add(ExifGroup.Exposure, "曝光模式", Lookup(Int(exif, 0xA402), ["自动曝光", "手动曝光", "自动包围曝光"]));
        Add(ExifGroup.Exposure, "测光模式", MeteringMode(Int(exif, 0x9207)));
        Add(ExifGroup.Exposure, "闪光灯", Int(exif, 0x9209) is { } flash ? Flash(flash) : null);
        Add(ExifGroup.Exposure, "白平衡", Lookup(Int(exif, 0xA403), ["自动", "手动"]));
        Add(ExifGroup.Exposure, "光源", LightSource(Int(exif, 0x9208)));
        Add(ExifGroup.Exposure, "最大光圈", Number(exif, 0x9205) is { } apex ? Aperture(Math.Pow(2, apex / 2)) : null);
        Add(ExifGroup.Exposure, "亮度", Number(exif, 0x9203) is { } bv ? $"{Num(bv)} EV" : null);
        Add(ExifGroup.Exposure, "对焦距离", Number(exif, 0x9206) is { } d and > 0 ? (d >= 4e9 ? "无穷远" : $"{Num(d)} m") : null);
        Add(ExifGroup.Exposure, "对焦范围", Lookup(Int(exif, 0xA40C), ["未知", "微距", "近景", "远景"]));
        Add(ExifGroup.Exposure, "数码变焦", Number(exif, 0xA404) is { } zoom and > 0 and not 1 ? $"{Num(zoom)}×" : null);
        Add(ExifGroup.Exposure, "场景类型", Lookup(Int(exif, 0xA406), ["标准", "风景", "人像", "夜景"]));
        Add(ExifGroup.Exposure, "对比度", Lookup(Int(exif, 0xA408), ["标准", "柔和", "强烈"]));
        Add(ExifGroup.Exposure, "饱和度", Lookup(Int(exif, 0xA409), ["标准", "低", "高"]));
        Add(ExifGroup.Exposure, "锐度", Lookup(Int(exif, 0xA40A), ["标准", "柔和", "强烈"]));
        Add(ExifGroup.Exposure, "增益控制", Lookup(Int(exif, 0xA407), ["无", "低增益提升", "高增益提升", "低增益降低", "高增益降低"]));
        Add(ExifGroup.Exposure, "感光元件", Lookup(Int(exif, 0xA217), SensingMethods));

        // ---- Image
        PixelWidth = Int(exif, 0xA002) ?? Int(ifd0, 0x0100);
        PixelHeight = Int(exif, 0xA003) ?? Int(ifd0, 0x0101);
        Add(ExifGroup.Image, "方向", Lookup(Int(ifd0, 0x0112), Orientations));
        Add(ExifGroup.Image, "色彩空间", Int(exif, 0xA001) switch { 1 => "sRGB", 0xFFFF => "未校准", null => null, var v => v.ToString() });
        Add(ExifGroup.Image, "分辨率", Resolution(Number(ifd0, 0x011A), Number(ifd0, 0x011B), Int(ifd0, 0x0128)));
        if (ifd0.TryGetValue(0x0102, out var bits))
            Add(ExifGroup.Image, "位深", string.Join(" / ", Enumerable.Range(0, Math.Min(bits.Count, 4)).Select(i => bits.Number(r, i)).Distinct()) + " 位");
        Add(ExifGroup.Image, "压缩", Compression(Int(ifd0, 0x0103)));
        Add(ExifGroup.Image, "色彩模式", Lookup(Int(ifd0, 0x0106), ["白为零", "黑为零", "RGB", "调色板", "透明遮罩", "CMYK", "YCbCr"]));
        Add(ExifGroup.Image, "每像素通道", Int(ifd0, 0x0115)?.ToString());
        Add(ExifGroup.Image, "处理方式", Lookup(Int(exif, 0xA401), ["常规", "自定义"]));
        Add(ExifGroup.Image, "Exif 版本", Version(exif, 0x9000, r));

        // ---- Time
        Add(ExifGroup.Time, "拍摄时间", Date(Text(exif, 0x9003), Text(exif, 0x9291), Text(exif, 0x9011)));
        Add(ExifGroup.Time, "数字化时间", Date(Text(exif, 0x9004), Text(exif, 0x9292), Text(exif, 0x9012)));
        Add(ExifGroup.Time, "修改时间", Date(Text(ifd0, 0x0132), Text(exif, 0x9290), Text(exif, 0x9010)));

        // ---- Location
        Latitude = Coordinate(r, gps, 0x0002, Text(gps, 0x0001), "S");
        Longitude = Coordinate(r, gps, 0x0004, Text(gps, 0x0003), "W");
        if (Latitude is { } lat) Add(ExifGroup.Location, "纬度", Dms(lat, "北纬", "南纬"));
        if (Longitude is { } lon) Add(ExifGroup.Location, "经度", Dms(lon, "东经", "西经"));
        if (Number(gps, 0x0006) is { } alt)
        {
            var below = gps.TryGetValue(0x0005, out var altRef) && altRef.Number(r, 0) == 1;
            Add(ExifGroup.Location, "海拔", $"{(below ? "-" : "")}{Num(alt, 1)} m");
        }
        if (Number(gps, 0x0011) is { } direction)
            Add(ExifGroup.Location, "拍摄朝向", $"{Num(direction, 1)}°（{(Text(gps, 0x0010) == "M" ? "磁北" : "真北")}）");
        if (Number(gps, 0x000D) is { } speed)
            Add(ExifGroup.Location, "移动速度", $"{Num(speed, 1)} {Text(gps, 0x000C) switch { "M" => "英里/时", "N" => "节", _ => "公里/时" }}");
        if (Number(gps, 0x001F) is { } error) Add(ExifGroup.Location, "定位误差", $"{Num(error, 1)} m");
        if (gps.TryGetValue(0x0007, out var stamp) && stamp.Count >= 3)
        {
            var time = $"{stamp.Number(r, 0):00}:{stamp.Number(r, 1):00}:{Math.Floor(stamp.Number(r, 2) ?? 0):00}";
            Add(ExifGroup.Location, "GPS 时间", $"{Text(gps, 0x001D)?.Replace(':', '-')} {time} UTC".Trim());
        }

        // ---- Description and rights
        Add(ExifGroup.Other, "描述", Text(ifd0, 0x010E));
        Add(ExifGroup.Other, "作者", Text(ifd0, 0x013B));
        Add(ExifGroup.Other, "版权", Text(ifd0, 0x8298));
        Add(ExifGroup.Other, "用户注释", exif.TryGetValue(0x9286, out var comment) ? UserComment(comment.Bytes(r)) : null);
        Add(ExifGroup.Other, "图像 ID", Text(exif, 0xA420));
    }

    // ---- Formatting

    private static string Num(double v, int decimals = 2) =>
        Math.Round(v, decimals).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>1/125, 0.5, 2, 30 — fractions of a second as photographers write them.</summary>
    public static string Shutter(double seconds)
    {
        if (seconds <= 0) return "0";
        if (seconds >= 0.3) return Num(seconds, 1);
        return $"1/{Math.Round(1 / seconds):0}";
    }

    public static string Aperture(double f) => $"ƒ/{Num(f, 1)}";

    public static string Bias(double ev) =>
        Math.Abs(ev) < 0.005 ? "0 EV" : (ev > 0 ? "+" : "−") + Num(Math.Abs(ev)) + " EV";

    private static string? LensSpecification(double? minFocal, double? maxFocal, double? minF, double? maxF)
    {
        if (minFocal is not { } a || maxFocal is not { } b) return null;
        var focal = Math.Abs(a - b) < 0.01 ? $"{Num(a)} mm" : $"{Num(a)}–{Num(b)} mm";
        var aperture = minF is { } x and > 0 ? maxF is { } y and > 0 && Math.Abs(x - y) > 0.01 ? $"ƒ/{Num(x, 1)}–{Num(y, 1)}" : $"ƒ/{Num(x, 1)}" : null;
        return aperture is null ? focal : $"{focal} {aperture}";
    }

    private static string? Lookup(int? value, string[] names) =>
        value is null ? null : value >= 0 && value < names.Length ? names[value.Value] : value.ToString();

    private static readonly string[] ExposurePrograms =
        ["未定义", "手动", "程序自动", "光圈优先", "快门优先", "创意程序（景深优先）", "动作程序（快门优先）", "人像模式", "风景模式"];

    private static readonly string[] Orientations =
        ["", "正常", "水平翻转", "旋转 180°", "垂直翻转", "水平翻转后逆时针旋转 90°", "顺时针旋转 90°", "水平翻转后顺时针旋转 90°", "逆时针旋转 90°"];

    private static readonly string[] SensingMethods =
        ["", "未定义", "单芯片彩色区域传感器", "双芯片彩色区域传感器", "三芯片彩色区域传感器", "彩色顺序区域传感器", "", "三线传感器", "彩色顺序线性传感器"];

    private static string? MeteringMode(int? mode) => mode switch
    {
        null => null,
        0 => "未知",
        1 => "平均测光",
        2 => "中央重点测光",
        3 => "点测光",
        4 => "多点测光",
        5 => "评价测光（矩阵）",
        6 => "局部测光",
        255 => "其他",
        _ => mode.ToString(),
    };

    private static string? LightSource(int? source) => source switch
    {
        null => null,
        0 => "未知",
        1 => "日光",
        2 => "荧光灯",
        3 => "钨丝灯",
        4 => "闪光灯",
        9 => "晴天",
        10 => "阴天",
        11 => "阴影",
        12 => "日光色荧光灯",
        13 => "日白色荧光灯",
        14 => "冷白色荧光灯",
        15 => "白色荧光灯",
        17 => "标准光源 A",
        18 => "标准光源 B",
        19 => "标准光源 C",
        20 => "D55",
        21 => "D65",
        22 => "D75",
        23 => "D50",
        24 => "ISO 影棚钨丝灯",
        255 => "其他",
        _ => source.ToString(),
    };

    /// <summary>Flash bits: 0 fired, 3–4 mode, 5 no flash unit, 6 red-eye reduction.</summary>
    public static string Flash(int value)
    {
        if ((value & 0x20) != 0) return "无闪光灯";
        var parts = new List<string> { (value & 1) != 0 ? "已闪光" : "未闪光" };
        var mode = (value >> 3) & 3;
        if (mode == 1) parts.Add("强制闪光");
        else if (mode == 2) parts.Add("关闭闪光");
        else if (mode == 3) parts.Add("自动");
        if ((value & 0x40) != 0) parts.Add("防红眼");
        return string.Join("，", parts);
    }

    private static string? Compression(int? value) => value switch
    {
        null => null,
        1 => "无压缩",
        5 => "LZW",
        6 or 7 => "JPEG",
        8 or 32946 => "Deflate",
        32773 => "PackBits",
        _ => value.ToString(),
    };

    private static string? Resolution(double? x, double? y, int? unit)
    {
        if (x is not { } h) return null;
        var suffix = unit == 3 ? "像素/厘米" : "dpi";
        return y is { } v && Math.Abs(h - v) > 0.01 ? $"{Num(h)} × {Num(v)} {suffix}" : $"{Num(h)} {suffix}";
    }

    /// <summary>"0232" → "2.32".</summary>
    private static string? Version(Dictionary<ushort, Field> ifd, ushort tag, Reader r)
    {
        if (!ifd.TryGetValue(tag, out var field)) return null;
        var text = Encoding.ASCII.GetString(field.Bytes(r)).Trim('\0');
        return text.Length == 4 && text.All(char.IsDigit) ? $"{int.Parse(text[..2], CultureInfo.InvariantCulture)}.{text[2..]}" : text;
    }

    private static string? Date(string? value, string? subSeconds, string? offset)
    {
        if (value is null || !DateTime.TryParseExact(value.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)) return null;
        var text = time.ToString("yyyy年M月d日 HH:mm:ss", CultureInfo.InvariantCulture);
        if (subSeconds?.Trim() is { Length: > 0 } sub && sub.All(char.IsDigit)) text += "." + sub;
        if (offset?.Trim() is { Length: > 0 } zone) text += $"（UTC{zone}）";
        return text;
    }

    private static double? Coordinate(Reader r, Dictionary<ushort, Field> gps, ushort tag, string? reference, string negative)
    {
        if (!gps.TryGetValue(tag, out var field) || field.Count < 3) return null;
        if (field.Number(r, 0) is not { } d || field.Number(r, 1) is not { } m || field.Number(r, 2) is not { } s) return null;
        var value = d + m / 60 + s / 3600;
        if (double.IsNaN(value)) return null;
        return string.Equals(reference?.Trim(), negative, StringComparison.OrdinalIgnoreCase) ? -value : value;
    }

    /// <summary>31.2293 → 北纬 31°13′45.48″.</summary>
    public static string Dms(double value, string positive, string negative)
    {
        var abs = Math.Abs(value);
        var degrees = (int)abs;
        var minutes = (int)((abs - degrees) * 60);
        var seconds = (abs - degrees - minutes / 60.0) * 3600;
        return $"{(value < 0 ? negative : positive)} {degrees}°{minutes}′{Num(seconds)}″";
    }

    /// <summary>UserComment: an 8-byte character code ("ASCII\0\0\0", "UNICODE\0", "JIS"…) then the text.</summary>
    private static string? UserComment(byte[] data)
    {
        if (data.Length <= 8) return null;
        var code = Encoding.ASCII.GetString(data, 0, 8).TrimEnd('\0', ' ');
        var body = data.AsSpan(8);
        var text = code == "UNICODE"
            ? (body.Length > 1 && body[0] == 0 ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(body)
            : Encoding.UTF8.GetString(body);
        text = text.Trim('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    // ---- TIFF structure

    /// <summary>A directory entry: tag type, value count and where the value lives (inline or at an offset).</summary>
    private readonly record struct Field(ushort Type, int Count, int ValueAt)
    {
        private static int Size(ushort type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };

        public int Length => Size(Type) * Count;

        public uint FirstU32(Reader r) => Type == 3 ? r.U16(ValueAt) : r.U32(ValueAt);

        public byte[] Bytes(Reader r) => r.Slice(ValueAt, Length).ToArray();

        public string? Text(Reader r)
        {
            if (Type is not (2 or 7 or 1)) return null;
            var bytes = r.Slice(ValueAt, Length);
            var end = bytes.IndexOf((byte)0);
            if (end >= 0) bytes = bytes[..end];
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length == 0 ? null : text;
        }

        public double? Number(Reader r, int index)
        {
            if (index >= Count) return null;
            var at = ValueAt + index * Size(Type);
            return Type switch
            {
                1 or 7 => r.Byte(at),
                6 => (sbyte)r.Byte(at),
                3 => r.U16(at),
                8 => (short)r.U16(at),
                4 => r.U32(at),
                9 => (int)r.U32(at),
                5 => r.U32(at + 4) is var den and not 0 ? r.U32(at) / (double)den : null,
                10 => (int)r.U32(at + 4) is var sden and not 0 ? (int)r.U32(at) / (double)sden : null,
                _ => null,
            };
        }
    }

    private sealed class Reader(byte[] tiff, bool little)
    {
        private readonly byte[] _tiff = tiff;

        public byte Byte(int at) => at >= 0 && at < _tiff.Length ? _tiff[at] : (byte)0;

        public ushort U16(int at) => at < 0 || at + 2 > _tiff.Length ? (ushort)0
            : little ? BinaryPrimitives.ReadUInt16LittleEndian(_tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(_tiff.AsSpan(at));

        public uint U32(int at) => at < 0 || at + 4 > _tiff.Length ? 0
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(_tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(_tiff.AsSpan(at));

        public ReadOnlySpan<byte> Slice(int at, int length) =>
            at < 0 || length < 0 || at >= _tiff.Length ? default : _tiff.AsSpan(at, Math.Min(length, _tiff.Length - at));

        public Dictionary<ushort, Field> Ifd(uint offset)
        {
            var fields = new Dictionary<ushort, Field>();
            if (offset == 0 || offset + 2 > _tiff.Length) return fields;
            int count = U16((int)offset);
            for (var i = 0; i < count && i < 512; i++)
            {
                var entry = (int)offset + 2 + i * 12;
                if (entry + 12 > _tiff.Length) break;
                var type = U16(entry + 2);
                var n = U32(entry + 4);
                if (n > int.MaxValue / 8) continue;
                var field = new Field(type, (int)n, entry + 8);
                // Values over four bytes live elsewhere; the entry holds their offset
                if (field.Length > 4) field = field with { ValueAt = (int)U32(entry + 8) };
                if (field.ValueAt < 0 || field.ValueAt + field.Length > _tiff.Length) continue;
                fields.TryAdd(U16(entry), field);
            }
            return fields;
        }
    }
}
