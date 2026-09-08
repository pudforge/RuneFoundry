using System.Diagnostics;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;

// Runs every parser over a real installation. This is how the format work is kept
// honest: if a change breaks GRP decoding for one sprite out of 272, this says so.

var root = args.FirstOrDefault(a => !a.StartsWith("--"));
GameInstall? game;
string error;

if (root is null)
{
    game = GameInstall.Detect();
    if (game is null)
    {
        Console.Error.WriteLine("Could not find Warcraft II Remastered. Pass the install folder as an argument.");
        return 2;
    }
}
else if (!GameInstall.TryOpen(root, out game, out error))
{
    Console.Error.WriteLine(error);
    return 2;
}

// --bench: the numbers the performance pass is judged against. Measured on the real
// install, because a synthetic tree says nothing about 2.5 GB in Program Files.
if (args.Contains("--bench"))
{
    var bench = Stopwatch.StartNew();

    bench.Restart();
    var files = game!.EnumerateDataFiles().ToList();
    var enumerate = bench.ElapsedMilliseconds;

    long bytes = 0;
    bench.Restart();
    foreach (var relative in files) bytes += new FileInfo(game.ResolveDataPath(relative)).Length;
    var stat = bench.ElapsedMilliseconds;

    bench.Restart();
    var library = SoundLibrary.Scan(game);
    var soundScan = bench.ElapsedMilliseconds;

    // Hashing is what Verify and install spend their time in.
    var biggest = files.OrderByDescending(f => new FileInfo(game.ResolveDataPath(f)).Length).First();
    var biggestSize = new FileInfo(game.ResolveDataPath(biggest)).Length;
    bench.Restart();
    Hashing.Sha256File(game.ResolveDataPath(biggest));
    var hashOne = bench.ElapsedMilliseconds;

    var sample = files.Where(f => f.EndsWith(".grp", StringComparison.OrdinalIgnoreCase)).ToList();
    bench.Restart();
    var frames = 0;
    foreach (var relative in sample)
    {
        try { frames += GrpFile.Load(game.ResolveDataPath(relative)).Frames.Count; }
        catch { }
    }
    var grpHeaders = bench.ElapsedMilliseconds;

    // Decoding, which is what a preview actually pays for.
    var bigGrp = sample.OrderByDescending(f => new FileInfo(game.ResolveDataPath(f)).Length).First();
    var grp = GrpFile.Load(game.ResolveDataPath(bigGrp));
    bench.Restart();
    for (var i = 0; i < grp.Frames.Count; i++) grp.DecodeFrame(i);
    var grpDecode = bench.ElapsedMilliseconds;

    var jsons = files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
    var bigJson = jsons.OrderByDescending(f => new FileInfo(game.ResolveDataPath(f)).Length).First();
    bench.Restart();
    var jsonText = File.ReadAllText(game.ResolveDataPath(bigJson));
    var parsed = System.Text.Json.JsonDocument.Parse(jsonText);
    var jsonMs = bench.ElapsedMilliseconds;

    var pngs = files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
    var bigPng = pngs.OrderByDescending(f => new FileInfo(game.ResolveDataPath(f)).Length).First();
    var pngSize = new FileInfo(game.ResolveDataPath(bigPng)).Length;
    bench.Restart();
    var pngBytes = File.ReadAllBytes(game.ResolveDataPath(bigPng));
    var pngRead = bench.ElapsedMilliseconds;

    // What Verify and install actually pay: hashing many files, not one.
    bench.Restart();
    foreach (var relative in files) Hashing.Sha256File(game.ResolveDataPath(relative));
    var hashSerial = bench.ElapsedMilliseconds;

    bench.Restart();
    System.Threading.Tasks.Parallel.ForEach(files, relative => Hashing.Sha256File(game.ResolveDataPath(relative)));
    var hashParallel = bench.ElapsedMilliseconds;

    Console.WriteLine("Baseline");
    Console.WriteLine($"  enumerate install        {files.Count,6} files   {enumerate,6} ms");
    Console.WriteLine($"  stat every file          {bytes / (1024 * 1024),6} MB      {stat,6} ms");
    Console.WriteLine($"  sound library scan       {library.Count,6} names   {soundScan,6} ms");
    Console.WriteLine($"  sha-256 largest file     {biggestSize / (1024 * 1024),6} MB      {hashOne,6} ms"
                      + $"   ({biggest})");
    Console.WriteLine($"  open every .grp header   {sample.Count,6} files   {grpHeaders,6} ms"
                      + $"   ({frames} frames)");
    Console.WriteLine($"  decode one .grp whole    {grp.Frames.Count,6} frames  {grpDecode,6} ms"
                      + $"   ({bigGrp})");
    Console.WriteLine($"  parse largest .json      {jsonText.Length / 1024,6} KB      {jsonMs,6} ms"
                      + $"   ({bigJson}, {parsed.RootElement.EnumerateObject().Count()} keys)");
    Console.WriteLine($"  sha-256 whole install    {bytes / (1024 * 1024),6} MB   {hashSerial,6} ms"
                      + $"   serial, {bytes / 1024 / Math.Max(1, hashSerial)} MB/s");
    Console.WriteLine($"  sha-256 whole install    {bytes / (1024 * 1024),6} MB   {hashParallel,6} ms"
                      + $"   parallel, {bytes / 1024 / Math.Max(1, hashParallel)} MB/s");
    Console.WriteLine($"  read largest .png        {pngSize / (1024 * 1024),6} MB      {pngRead,6} ms"
                      + $"   ({bigPng}, {pngBytes.Length / (1024 * 1024)} MB)");
    return 0;
}

Console.WriteLine($"Game:  {game!.Root}");
Console.WriteLine($"Elevated: {RuneFoundry.Core.GameLauncher.IsElevated()}");
Console.WriteLine($"Can launch as desktop user: {RuneFoundry.Core.GameLauncher.CanStartAsDesktopUser(out var deelevationWhy)}"
                  + (deelevationWhy.Length > 0 ? "  (" + deelevationWhy + ")" : ""));
Console.WriteLine($"Data:  {game.DataRoot}");
Console.WriteLine();

var stopwatch = Stopwatch.StartNew();
var byKind = new Dictionary<AssetKind, int>();
var failures = new List<string>();
var warnings = new List<string>();
var framesDecoded = 0;
var framesFailed = 0;
var total = 0;

foreach (var relativePath in game.EnumerateDataFiles().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
{
    total++;
    var fullPath = game.ResolveDataPath(relativePath);

    AssetInfo info;
    try
    {
        info = AssetInspector.Inspect(fullPath, game, relativePath);
    }
    catch (Exception ex)
    {
        failures.Add($"{relativePath}: inspector threw {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    byKind[info.Kind] = byKind.GetValueOrDefault(info.Kind) + 1;

    if (info.TypeName.Contains("unreadable", StringComparison.OrdinalIgnoreCase))
        failures.Add($"{relativePath}: {info.TypeName} — {info.Summary}");
    if (info.Warning is not null)
        warnings.Add($"{relativePath}: {info.Warning}");

    // Decoding every frame of every sprite is the part that actually exercises the RLE path.
    if (info.RenderFrame is not null && info.FrameCount > 0)
    {
        for (var frame = 0; frame < info.FrameCount; frame++)
        {
            try
            {
                var (width, height, pixels) = info.RenderFrame(frame);
                if (pixels.Length != width * height * 4)
                    failures.Add($"{relativePath} frame {frame}: {pixels.Length} bytes for {width}x{height}");
                else if (pixels.All(b => b == 0) && info.Kind == AssetKind.Grp)
                    framesFailed++;   // decoded to nothing; counted, not fatal — some frames are genuinely blank
                else
                    framesDecoded++;
            }
            catch (Exception ex)
            {
                failures.Add($"{relativePath} frame {frame}: {ex.GetType().Name}: {ex.Message}");
                framesFailed++;
            }
        }
    }
}

stopwatch.Stop();

Console.WriteLine($"Scanned {total} files in {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine();
Console.WriteLine("By kind:");
foreach (var (kind, count) in byKind.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {kind,-10} {count,5}");

Console.WriteLine();
Console.WriteLine($"Sprite frames decoded: {framesDecoded}  (empty or failed: {framesFailed})");

// Sound names that the install answers with more than one file. The unit table asks for
// names, so a mod that overrides the wrong copy of a duplicated name changes a file the
// game never plays — and nothing about that looks broken until someone listens for it.
var sounds = SoundLibrary.Scan(game);
Console.WriteLine();
Console.WriteLine($"Sounds: {sounds.Count} names");

if (sounds.Collisions.Count > 0)
{
    var ambiguous = sounds.AmbiguousCollisions.ToList();
    Console.WriteLine($"  duplicate names: {sounds.Collisions.Count} "
                      + $"({ambiguous.Count} the rule could not decide on their own merits)");

    foreach (var collision in sounds.Collisions.OrderBy(c => c.FileName).Take(12))
        Console.WriteLine($"    {(collision.Ambiguous ? "?" : " ")} {collision.FileName}"
                          + $" -> {collision.Chosen}  (ignored: {string.Join(", ", collision.Ignored)})");

    if (sounds.Collisions.Count > 12)
        Console.WriteLine($"    ... and {sounds.Collisions.Count - 12} more");
}
else
{
    Console.WriteLine("  no duplicate names.");
}

if (warnings.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Warnings ({warnings.Count}):");
    foreach (var warning in warnings.Take(15)) Console.WriteLine($"  {warning}");
    if (warnings.Count > 15) Console.WriteLine($"  ... and {warnings.Count - 15} more");
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"FAILURES ({failures.Count}):");
    foreach (var failure in failures.Take(30)) Console.WriteLine($"  {failure}");
    if (failures.Count > 30) Console.WriteLine($"  ... and {failures.Count - 30} more");
    return 1;
}

Console.WriteLine();
Console.WriteLine("No parse failures.");
return 0;
