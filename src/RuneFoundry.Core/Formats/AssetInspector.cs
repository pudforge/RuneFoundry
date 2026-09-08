using System.Text;

namespace RuneFoundry.Core.Formats;

public enum AssetKind
{
    Unknown,
    Grp,
    Pcx,
    Png,
    Bmp,
    Palette,
    Tbl,
    Pud,
    AiScripts,
    UnitData,
    UpgradeData,
    Json,
    Text,
    Wave,
    Video,
    Binary,
}

/// <summary>
/// What the packager knows about one file: what it is, a one-line summary, and — where
/// we can draw it — pixels ready to hand to the UI.
/// </summary>
public sealed class AssetInfo
{
    public AssetKind Kind { get; init; }
    public string TypeName { get; init; } = "";
    public string Summary { get; init; } = "";
    public long SizeBytes { get; init; }

    /// <summary>Frame count for sprite sheets; 1 for stills; 0 when there is nothing to draw.</summary>
    public int FrameCount { get; init; }

    /// <summary>Set when the file parsed as its apparent type but something was off.</summary>
    public string? Warning { get; init; }

    /// <summary>Strings, for a TBL. The packager surfaces these for editing.</summary>
    public IReadOnlyList<string>? Strings { get; init; }

    /// <summary>Renders a frame to BGRA32. Null when the type has no visual form.</summary>
    public Func<int, (int Width, int Height, byte[] Bgra)>? RenderFrame { get; init; }
}

/// <summary>
/// Identifies game assets by content rather than by extension, because a mod author
/// will sooner or later hand us a .png that is really a .bmp, and because several of
/// the game's own formats share extensions with unrelated things.
/// </summary>
public static class AssetInspector
{
    public static AssetInfo Inspect(string path, GameInstall? game = null, string? gameRelativePath = null)
    {
        var info = new FileInfo(path);
        byte[] data;
        try
        {
            // A preview never needs more than the head of a huge file, but every format
            // here is small enough to read whole, and the parsers want random access.
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Unknown, TypeName = "Unreadable", Summary = ex.Message, SizeBytes = info.Length };
        }

        return Inspect(data, info.Length, Path.GetExtension(path), game, gameRelativePath);
    }

    public static AssetInfo Inspect(byte[] data, long size, string extension, GameInstall? game = null, string? gameRelativePath = null)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();

        if (StartsWith(data, 0x89, (byte)'P', (byte)'N', (byte)'G'))
            return new AssetInfo { Kind = AssetKind.Png, TypeName = "PNG image", Summary = DescribePng(data), SizeBytes = size, FrameCount = 1 };

        if (StartsWith(data, (byte)'B', (byte)'M'))
            return new AssetInfo { Kind = AssetKind.Bmp, TypeName = "Bitmap", Summary = "Windows bitmap", SizeBytes = size, FrameCount = 1 };

        if (StartsWith(data, (byte)'R', (byte)'I', (byte)'F', (byte)'F'))
            return new AssetInfo { Kind = AssetKind.Wave, TypeName = "WAV audio", Summary = DescribeWav(data), SizeBytes = size };

        if (extension is "webm" or "smk" or "bik" or "mp4")
            return new AssetInfo { Kind = AssetKind.Video, TypeName = extension.ToUpperInvariant() + " video", Summary = "Video clip", SizeBytes = size };

        if (PudFile.LooksLikePud(data)) return InspectPud(data, size);

        if (extension == "ppl" && data.Length == Palette.FileSize)
            return new AssetInfo { Kind = AssetKind.Palette, TypeName = "Palette", Summary = "256-colour tileset palette", SizeBytes = size };

        if (PcxFile.LooksLikePcx(data)) return InspectPcx(data, size, game, gameRelativePath);

        // GRP has no magic number, so it is tried late and only when the extension agrees
        // or nothing else claimed the file — a loose heuristic would swallow other formats.
        if ((extension == "grp" || extension.Length == 0) && GrpFile.LooksLikeGrp(data))
            return InspectGrp(data, size, game, gameRelativePath);

        if (extension == "tbl" && TblFile.LooksLikeTbl(data)) return InspectTbl(data, size);

        // ai.bin has no magic number, so it is only tried for .bin files.
        if (extension == "bin" && AiFile.LooksLikeAiFile(data)) return InspectAi(data, size);

        // The .dat tables are identified by their exact length, which is all that
        // distinguishes them — they carry no header of any kind.
        if (extension == "dat")
        {
            if (UnitDataFile.LooksLikeUnitData(data)) return InspectUnitData(data, size);
            if (UpgradeDataFile.LooksLikeUpgradeData(data)) return InspectUpgradeData(data, size);
        }

        if (extension == "json")
            return new AssetInfo { Kind = AssetKind.Json, TypeName = "JSON", Summary = $"{CountLines(data)} lines", SizeBytes = size };

        if (extension is "txt" or "ini" or "lst" or "url")
            return new AssetInfo { Kind = AssetKind.Text, TypeName = "Text", Summary = $"{CountLines(data)} lines", SizeBytes = size };

        return new AssetInfo { Kind = AssetKind.Binary, TypeName = extension.Length > 0 ? extension.ToUpperInvariant() + " file" : "Binary", Summary = "Replaced as raw bytes", SizeBytes = size };
    }

    private static AssetInfo InspectGrp(byte[] data, long size, GameInstall? game, string? relativePath)
    {
        try
        {
            var grp = GrpFile.Parse(data);
            var palette = ResolvePalette(game, relativePath);

            return new AssetInfo
            {
                Kind = AssetKind.Grp,
                TypeName = "GRP sprite sheet",
                Summary = grp.Describe(),
                SizeBytes = size,
                FrameCount = grp.Frames.Count,
                RenderFrame = frameIndex =>
                {
                    var image = grp.DecodeFrame(frameIndex);
                    return (image.Width, image.Height, image.ToBgra32(palette));
                },
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "GRP (unreadable)", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static AssetInfo InspectPcx(byte[] data, long size, GameInstall? game, string? relativePath)
    {
        try
        {
            var pcx = PcxFile.Parse(data);
            var fallback = pcx.EmbeddedPalette is null ? ResolvePalette(game, relativePath) : null;

            return new AssetInfo
            {
                Kind = AssetKind.Pcx,
                TypeName = "PCX image",
                Summary = pcx.Describe(),
                SizeBytes = size,
                FrameCount = 1,
                RenderFrame = _ => (pcx.Width, pcx.Height, pcx.ToBgra32(fallback)),
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "PCX (unreadable)", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static AssetInfo InspectTbl(byte[] data, long size)
    {
        try
        {
            var tbl = TblFile.Parse(data);
            return new AssetInfo
            {
                Kind = AssetKind.Tbl,
                TypeName = "String table",
                Summary = tbl.Describe(),
                SizeBytes = size,
                Strings = tbl.Strings,
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "TBL (unreadable)", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static AssetInfo InspectAi(byte[] data, long size)
    {
        try
        {
            var ai = AiFile.Parse(data);
            return new AssetInfo
            {
                Kind = AssetKind.AiScripts,
                TypeName = "AI scripts",
                Summary = ai.Describe(),
                SizeBytes = size,
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "BIN", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static AssetInfo InspectUnitData(byte[] data, long size)
    {
        try
        {
            var units = UnitDataFile.Parse(data);
            var real = Enumerable.Range(0, units.RecordCount).Count(i => !units.IsEmpty(i));
            return new AssetInfo
            {
                Kind = AssetKind.UnitData,
                TypeName = "Unit stats",
                Summary = $"{real} units, {units.Fields.Count} fields per unit",
                SizeBytes = size,
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "DAT", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static AssetInfo InspectUpgradeData(byte[] data, long size)
    {
        try
        {
            var upgrades = UpgradeDataFile.Parse(data);
            return new AssetInfo
            {
                Kind = AssetKind.UpgradeData,
                TypeName = "Upgrade stats",
                Summary = $"{upgrades.RecordCount} upgrades and spells",
                SizeBytes = size,
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "DAT", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static AssetInfo InspectPud(byte[] data, long size)
    {
        try
        {
            var pud = PudFile.Parse(data);
            return new AssetInfo
            {
                Kind = AssetKind.Pud,
                TypeName = "Map",
                Summary = pud.Describe(),
                SizeBytes = size,
                Warning = pud.StructureWarning,
            };
        }
        catch (Exception ex)
        {
            return new AssetInfo { Kind = AssetKind.Binary, TypeName = "PUD (unreadable)", Summary = ex.Message, SizeBytes = size };
        }
    }

    private static Palette ResolvePalette(GameInstall? game, string? relativePath)
    {
        if (game is null || relativePath is null) return Palette.Greyscale();
        try { return Palette.ForGameAsset(game, relativePath); }
        catch { return Palette.Greyscale(); }
    }

    private static bool StartsWith(byte[] data, params byte[] magic)
        => data.Length >= magic.Length && !magic.Where((b, i) => data[i] != b).Any();

    private static string DescribePng(byte[] data)
    {
        // IHDR is always the first chunk: 8-byte signature, 4-byte length, "IHDR", then w/h.
        if (data.Length < 24) return "PNG image";
        var width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        var height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        return $"{width}x{height}";
    }

    private static string DescribeWav(byte[] data)
    {
        if (data.Length < 44) return "WAV audio";
        var channels = BitConverter.ToUInt16(data, 22);
        var rate = BitConverter.ToUInt32(data, 24);
        var bits = BitConverter.ToUInt16(data, 34);
        if (rate is 0 or > 384000) return "WAV audio";
        return $"{rate} Hz, {bits}-bit, {(channels == 1 ? "mono" : channels == 2 ? "stereo" : channels + " ch")}";
    }

    private static int CountLines(byte[] data)
        => Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 1 << 20)).Count(c => c == '\n') + 1;
}
