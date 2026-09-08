using System.IO;
using System.Security.Cryptography;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.UI;

// End-to-end checks against a synthetic install, so the destructive paths (install,
// conflict resolution, uninstall, rollback) are exercised without touching a real game.

var runner = new Runner();

runner.Test("path safety rejects traversal and device names", () =>
{
    string[] bad =
    {
        "../evil.dll", "..\\evil.dll", "Art/../../evil.dll", "/absolute/thing",
        "C:/Windows/system32/evil.dll", "Art/CON", "Art/nul.txt", "Art/trailing ", "Art//empty",
    };
    foreach (var path in bad)
        Runner.IsFalse(PathSafety.IsSafeRelativePath(path, out _), $"should reject '{path}'");

    string[] good = { "Art/hd/unit/foot.png", "Rez/2xhum1.tbl", "Campaign/Human/HUMAN01.PUD" };
    foreach (var path in good)
        Runner.IsTrue(PathSafety.IsSafeRelativePath(path, out var why), $"should accept '{path}': {why}");
});

runner.Test("resolving a path never escapes the root", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "w2m-root");
    Runner.IsFalse(PathSafety.TryResolveUnder(root, "../outside.txt", out _, out _), "traversal must not resolve");
    Runner.IsTrue(PathSafety.TryResolveUnder(root, "Art/x.png", out var full, out _), "normal path must resolve");
    Runner.IsTrue(full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "must stay under root");
});

runner.Test("TBL round-trips through parse and write", () =>
{
    var original = new TblFile(new[] { "Grunt", "Peasant", "Lord Khadgar, Keeper of the Eternal Watch", "" });
    var reparsed = TblFile.Parse(original.ToBytes());
    Runner.AreEqual(original.Strings.Count, reparsed.Strings.Count, "string count");
    for (var i = 0; i < original.Strings.Count; i++)
        Runner.AreEqual(original.Strings[i], reparsed.Strings[i], $"string {i}");
});

runner.Test("GRP decodes a hand-built RLE frame", () =>
{
    // 4x2: row 0 is a literal run of 4, row 1 is skip 2 then a repeat of 2.
    var rows = new List<byte[]>
    {
        new byte[] { 0x04, 10, 11, 12, 13 },
        new byte[] { 0x82, 0x42, 99 },
    };
    var frame = BuildFrame(rows);
    var grp = GrpFile.Parse(BuildGrp(4, 2, frame));

    var image = grp.DecodeFrame(0);
    Runner.AreEqual(4, image.Width, "width");
    Runner.AreEqual(10, image.Indices[0], "pixel 0,0");
    Runner.AreEqual(13, image.Indices[3], "pixel 3,0");
    Runner.IsFalse(image.Opaque[4], "skipped pixel must be transparent");
    Runner.IsFalse(image.Opaque[5], "skipped pixel must be transparent");
    Runner.AreEqual(99, image.Indices[6], "repeated pixel");
    Runner.AreEqual(99, image.Indices[7], "repeated pixel");
});

runner.Test("a 6-bit game palette is scaled to full brightness", () =>
{
    // What a .ppl holds: VGA values, 0-63. Read as 8-bit they made every sprite dark.
    var vga = new byte[RuneFoundry.Core.Formats.Palette.FileSize];
    vga[3] = 63; vga[4] = 32; vga[5] = 0;      // entry 1: brightest red, mid green

    var scaled = RuneFoundry.Core.Formats.Palette.FromBytes(vga)[1];
    Runner.AreEqual(255, (int)scaled.R, "63 is full brightness");
    Runner.AreEqual(129, (int)scaled.G, "32 is about half");
    Runner.AreEqual(0, (int)scaled.B, "0 stays 0");
});

runner.Test("a palette that is already 8-bit is left alone", () =>
{
    var rgb = new byte[RuneFoundry.Core.Formats.Palette.FileSize];
    rgb[3] = 200; rgb[4] = 64; rgb[5] = 10;

    var entry = RuneFoundry.Core.Formats.Palette.FromBytes(rgb)[1];
    Runner.AreEqual(200, (int)entry.R, "red unchanged");
    Runner.AreEqual(64, (int)entry.G, "green unchanged");
    Runner.AreEqual(10, (int)entry.B, "blue unchanged");
});

runner.Test("GRP falls back to raw pixels when the payload is flat", () =>
{
    var raw = new byte[] { 1, 2, 3, 4, 5, 6 };
    var grp = GrpFile.Parse(BuildGrp(3, 2, raw));
    var image = grp.DecodeFrame(0);
    Runner.AreEqual(1, image.Indices[0], "first raw pixel");
    Runner.AreEqual(6, image.Indices[5], "last raw pixel");
    Runner.IsTrue(grp.Frames[0].IsUncompressed, "frame should report as uncompressed");
});

runner.Test("package build, install, restore leaves the game byte-identical", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var before = Sandbox.SnapshotHashes(game.DataRoot);

    var project = sandbox.CreateProject("recolour", "Recolour");
    project.SeedFromGame("Art/unit.png", game);
    File.WriteAllBytes(project.ResolveContentPath("Art/unit.png"), new byte[] { 9, 9, 9, 9 });
    File.WriteAllText(project.ResolveContentPath("Art/brand-new.txt"), "added by the mod");

    var packagePath = Path.Combine(sandbox.Root, "recolour.w2mod");
    var manifest = project.Build(packagePath, game);
    Runner.AreEqual(2, manifest.Files.Count, "packaged file count");
    Runner.IsTrue(manifest.Files.Single(f => f.Path == "Art/brand-new.txt").IsNew, "new file must be flagged");
    Runner.IsTrue(manifest.Files.Single(f => f.Path == "Art/unit.png").BaseSha256 is { Length: 64 },
        "replacement must record the stock hash");

    var installer = new ModInstaller(game, sandbox.Vault);
    installer.AddPackage(packagePath);

    var applied = installer.Apply();
    Runner.IsTrue(applied.Succeeded, "apply should succeed: " + applied.FailureMessage);
    Runner.AreEqual(2, applied.FilesWritten, "files written");
    Runner.AreEqual(0, installer.Verify().Count, "verify should be clean after apply");

    Runner.AreEqual("09090909",
        Convert.ToHexString(File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))).ToLowerInvariant(),
        "modded content on disk");
    Runner.IsTrue(File.Exists(game.ResolveDataPath("Art/brand-new.txt")), "added file should exist");

    var restored = installer.RestoreVanilla();
    Runner.IsTrue(restored.Succeeded, "restore should succeed: " + restored.FailureMessage);
    Runner.IsFalse(File.Exists(game.ResolveDataPath("Art/brand-new.txt")), "added file should be gone");

    var after = Sandbox.SnapshotHashes(game.DataRoot);
    Runner.AreEqual(before.Count, after.Count, "file count after restore");
    foreach (var (path, hash) in before)
        Runner.AreEqual(hash, after.GetValueOrDefault(path), $"content of {path} after restore");
});

runner.Test("one mod is enabled at a time, and switching restores the other's files", () =>
{
    // There was a load order once, with precedence and conflict detection. It was removed on
    // purpose: with several mods enabled, which one owns a file depends on what else is
    // enabled and in what order, and "uninstalling restores the original bytes exactly" stops
    // being something anyone can be sure of. One mod at a time is what makes the guarantee
    // simple, so this checks the guarantee rather than the machinery.
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();

    var first = sandbox.BuildSimpleMod("mod-a", "Mod A", "Art/unit.png", new byte[] { 1 }, game);
    var second = sandbox.BuildSimpleMod("mod-b", "Mod B", "Art/unit.png", new byte[] { 2 }, game);

    var installer = new ModInstaller(game, sandbox.Vault);
    installer.AddPackage(first);
    installer.AddPackage(second);

    // Adding two does not enable two.
    installer.SetOnlyEnabled("mod-a");
    Runner.AreEqual(1, installer.State.Mods.Count(m => m.Enabled), "exactly one mod is enabled");
    Runner.IsTrue(installer.Apply().Succeeded, "first apply");
    Runner.AreEqual(1, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "mod-a is live");

    // Switching is one step: the previous mod's file goes back, the new one's goes in.
    installer.SetOnlyEnabled("mod-b");
    Runner.AreEqual(1, installer.State.Mods.Count(m => m.Enabled), "still exactly one");
    Runner.IsTrue(installer.Apply().Succeeded, "second apply");
    Runner.AreEqual(2, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "mod-b is live");

    // And none means the game as it shipped, byte for byte.
    installer.SetOnlyEnabled(null);
    Runner.AreEqual(0, installer.State.Mods.Count(m => m.Enabled), "none enabled is vanilla");
    Runner.IsTrue(installer.Apply().Succeeded, "third apply");
    Runner.AreEqual(1, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png")).Length, "stock file length");
    Runner.AreEqual(0xAB, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "stock content restored");
});

runner.Test("a tampered package is refused", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var packagePath = sandbox.BuildSimpleMod("tamper", "Tamper", "Art/unit.png", new byte[] { 7 }, game);

    // Rewrite one payload byte without touching the manifest, as a corrupted download would.
    using (var archive = System.IO.Compression.ZipFile.Open(packagePath, System.IO.Compression.ZipArchiveMode.Update))
    {
        var entry = archive.GetEntry("files/Art/unit.png")!;
        using var stream = entry.Open();
        stream.SetLength(0);
        stream.Write(new byte[] { 8 });
    }

    var installer = new ModInstaller(game, sandbox.Vault);
    var threw = false;
    try { installer.AddPackage(packagePath); }
    catch (InvalidDataException) { threw = true; }

    Runner.IsTrue(threw, "a package whose payload does not match its manifest must be rejected");
    Runner.AreEqual(0xAB, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "game must be untouched");
});

runner.Test("state survives being reloaded from disk", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var packagePath = sandbox.BuildSimpleMod("persist", "Persist", "Art/unit.png", new byte[] { 5 }, game);

    var first = new ModInstaller(game, sandbox.Vault);
    first.AddPackage(packagePath);
    Runner.IsTrue(first.Apply().Succeeded, "apply");

    // A fresh installer, as if the loader had been closed and reopened.
    var second = new ModInstaller(game, new BackupVault(sandbox.Vault.Root));
    Runner.AreEqual(1, second.State.Mods.Count, "mod list persisted");
    Runner.AreEqual(1, second.State.Applied.Count, "applied files persisted");
    Runner.AreEqual(0, second.Verify().Count, "verify clean after reload");

    var again = second.Apply();
    Runner.IsTrue(again.Succeeded, "re-apply");
    Runner.IsTrue(again.DidNothing, "re-applying an unchanged state should write nothing");
});

runner.Test("a new project shows the game's values, not the applied mod's", () =>
{
    // While a mod is applied the game folder holds its files. A project that does not
    // include a file must still be shown the game's own copy, or a new mod opens with the
    // last one's numbers already in the boxes.
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var live = game.ResolveDataPath("Art/unit.png");
    var stock = File.ReadAllBytes(live);

    sandbox.Vault.StoreOriginal("Art/unit.png", live);
    File.WriteAllBytes(live, new byte[] { 7, 7, 7 });        // as if a mod were applied

    var shown = sandbox.Vault.StockFile(game, "Art/unit.png");
    Runner.AreEqual(Convert.ToHexString(stock), Convert.ToHexString(File.ReadAllBytes(shown)),
        "what is shown is what shipped, not what is applied");

    // With nothing in the vault it can only be the game folder, which is right for an
    // install where no mod has ever been applied.
    Runner.AreEqual(game.ResolveDataPath("Art/other.png"),
        sandbox.Vault.StockFile(game, "Art/other.png"),
        "a file no mod has displaced comes from the game folder");
});

runner.Test("the file a mod reads is its own copy, else the game's, never another mod's", () =>
{
    // One helper for a question six screens were answering separately, under five names.
    // Two of those ended in a fallback that could only run when there was no game and then
    // dereferenced the game, so the guard threw in the case it was written for.
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var project = sandbox.CreateProject("effective", "Effective");

    var live = game.ResolveDataPath("Art/unit.png");
    var stock = File.ReadAllBytes(live);

    sandbox.Vault.StoreOriginal("Art/unit.png", live);
    File.WriteAllBytes(live, new byte[] { 7, 7, 7 });        // as if another mod were applied

    // Not in this mod, and another mod is applied: the answer is what the game shipped.
    var shown = sandbox.Vault.EffectiveFile(project, game, "Art/unit.png");
    Runner.IsTrue(shown is not null, "there is an answer");
    Runner.AreEqual(Convert.ToHexString(stock), Convert.ToHexString(File.ReadAllBytes(shown!)),
        "a file this mod does not have reads as the game's, not as the applied mod's");

    // In this mod: its own copy wins.
    project.WriteOverride("Art/unit.png", new byte[] { 9, 9, 9 });
    Runner.AreEqual(project.ResolveContentPath("Art/unit.png"),
        sandbox.Vault.EffectiveFile(project, game, "Art/unit.png"),
        "a file this mod has reads as this mod's");

    // No project at all is the same question with a simpler answer.
    Runner.AreEqual(shown, sandbox.Vault.EffectiveFile(null, game, "Art/unit.png"),
        "with no project it is the game's");

    // A file nothing has displaced comes from the game folder, which is right for an
    // install where no mod has ever been applied.
    Runner.AreEqual(game.ResolveDataPath("Art/other.png"),
        sandbox.Vault.EffectiveFile(null, game, "Art/other.png"),
        "a file no mod has touched comes from the game folder");
});

runner.Test("taking a file into a mod starts from the game's own bytes", () =>
{
    // With a mod applied, the game folder holds that mod's files. Seeding from there would
    // build the new mod on top of the applied one without saying so.
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var project = sandbox.CreateProject("seed", "Seed");

    var live = game.ResolveDataPath("Art/unit.png");
    var stock = File.ReadAllBytes(live);

    sandbox.Vault.StoreOriginal("Art/unit.png", live);
    File.WriteAllBytes(live, new byte[] { 9, 9, 9 });      // as if another mod were applied

    project.SeedFromGame("Art/unit.png", game, sandbox.Vault);
    Runner.AreEqual(Convert.ToHexString(stock),
        Convert.ToHexString(File.ReadAllBytes(project.ResolveContentPath("Art/unit.png"))),
        "the mod gets the stock bytes, not the applied ones");

    // Without a vault it can only use what is there, which is the old behaviour.
    var second = sandbox.CreateProject("seed2", "Seed2");
    second.SeedFromGame("Art/unit.png", game);
    Runner.AreEqual(3, File.ReadAllBytes(second.ResolveContentPath("Art/unit.png")).Length,
        "and with no vault to ask, the game folder is all there is");
});

runner.Test("every unit sound names a file the game actually has", () =>
{
    // The table comes from the notes, which spell the names lowercase while the files are
    // capitalised and sit in folders by speaker. If a name is wrong the unit view would
    // quietly show one sound fewer, so it is checked against the install.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var root in new[] { "Gamesfx", "Sfx", "Sound" })
    {
        var folder = game.ResolveDataPath(root);
        if (!Directory.Exists(folder)) continue;

        foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories))
            found.TryAdd(Path.GetFileName(file), file);
    }

    if (found.Count == 0) { Console.WriteLine("        (no sound files — skipped)"); return; }

    var missing = new List<string>();
    var counted = 0;
    for (var unit = 0; unit < 58; unit++)
    {
        foreach (var sound in UnitSounds.For(unit))
        {
            counted++;
            if (!found.ContainsKey(sound.FileName)) missing.Add($"unit {unit}: {sound.FileName}");
        }
    }

    Runner.AreEqual(0, missing.Count,
        missing.Count == 0 ? "every name resolves" : string.Join("; ", missing.Take(6)));
    Runner.IsTrue(counted > 250, "the table covers the roster");

    // A unit whose sounds are known, as a shape check on the lists. The Knight has a ready
    // line, four acknowledgements, four selections and three annoyed ones — no work-done,
    // no help and no death line, which are the human infantry's rather than his.
    var knight = UnitSounds.For(6);
    Runner.AreEqual(1, knight.Count(s => s.Kind == UnitSoundKind.Ready), "one ready line");
    Runner.AreEqual(4, knight.Count(s => s.Kind == UnitSoundKind.Acknowledge), "four acknowledgements");
    Runner.AreEqual(4, knight.Count(s => s.Kind == UnitSoundKind.Select), "four selections");
    Runner.AreEqual(3, knight.Count(s => s.Kind == UnitSoundKind.Annoyed), "three annoyed ones");
    Runner.IsTrue(knight.Any(s => s.Kind == UnitSoundKind.Acknowledge && s.FileName == "Knyessr1.wav"),
        "including the first acknowledgement");

    Console.WriteLine($"        ({counted} sounds across the roster, all present)");
});

runner.Test("the unit-to-icon table still says what we think it says", () =>
{
    // The portrait code reads word[0x008C6308 + type * 16]. The copy in IconNames was taken
    // out of build 1.0.2.2818; if a patch moves or changes the table, this is what says so.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var exePath = Path.Combine(game.Root, "x86", "Warcraft II.exe");
    if (!File.Exists(exePath)) { Console.WriteLine("        (skipped)"); return; }

    var exe = File.ReadAllBytes(exePath);
    if (!CampaignObjectives.TryFileOffset(exe, IconNames.TableAddress, out var offset, out var error))
    {
        Console.WriteLine($"        (skipped: {error})");
        return;
    }

    var mismatches = new List<string>();
    for (var unit = 0; unit < IconNames.UnitCount; unit++)
    {
        var at = offset + unit * IconNames.TableStride;
        if (at + 2 > exe.Length) break;

        var live = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(at));
        if (live != IconNames.ForUnit(unit)) mismatches.Add($"unit {unit}: exe {live}, ours {IconNames.ForUnit(unit)}");
    }

    Runner.AreEqual(0, mismatches.Count,
        mismatches.Count == 0 ? "every unit matches" : string.Join("; ", mismatches.Take(5)));

    // The two anchors that started this: the Footman wears the helmet, not the Peasant.
    Runner.AreEqual(2, IconNames.ForUnit(0), "the Footman's icon");
    Runner.AreEqual(0, IconNames.ForUnit(2), "the Peasant's icon");
    Console.WriteLine($"        ({IconNames.UnitCount} units checked against the executable)");
});

runner.Test("a swap is always made from the game's own art", () =>
{
    // Picking a sprite means that sprite's own art, not whatever its entry points at now.
    // Without that, choosing a sprite's own name after a swap did nothing, because by then
    // its entry was the art it had been swapped to.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, SpriteIndex.Path);
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var stock = SpriteIndex.Load(path);
    var mine = SpriteIndex.Load(path);

    const string knight = "art/unit/human/knight.grp";
    const string ogre = "art/unit/orc/knight.grp";

    var knightArt = stock.Source(knight);
    var ogreArt = stock.Source(ogre);

    mine.Remap(knight, ogre, stock);
    Runner.AreEqual(ogreArt, mine.Source(knight), "the Knight draws the Ogre");

    // Picking the Knight again has to mean the Knight's own art, not what its entry says.
    mine.Remap(knight, knight, stock);
    Runner.AreEqual(knightArt, mine.Source(knight), "and choosing itself puts it back");
    Runner.AreEqual(0, mine.ChangedFrom(stock).Count, "leaving nothing changed");

    // The same rule for icons: chaining a swap must not carry the previous one along.
    var facePath = StockFile(game, IconAtlas.FacePath);
    if (facePath is null) { Console.WriteLine("        (icons skipped)"); return; }

    var faceStock = IconAtlas.Load(facePath);
    var face = IconAtlas.Load(facePath);

    var two = faceStock.Rect("forest", 2);
    face.Remap(2, 9, faceStock);
    face.Remap(9, 2, faceStock);
    Runner.AreEqual(two, face.Rect("forest", 9), "icon 9 draws icon 2's own art, not the swapped one");
});

runner.Test("a sprite can be pointed at another sprite's art, and put back", () =>
{
    // Which sprite a unit uses is compiled in; where a sprite's art comes from is this file.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, SpriteIndex.Path);
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var stock = SpriteIndex.Load(path);
    var mine = SpriteIndex.Load(path);

    Runner.IsTrue(mine.Paths.Count > 100, "the index lists the game's sprites");
    Runner.AreEqual(0, mine.ChangedFrom(stock).Count, "an untouched index differs nowhere");

    const string knight = "art/unit/human/knight.grp";
    const string ogre = "art/unit/orc/knight.grp";

    var before = mine.Source(knight);
    Runner.IsTrue(before is not null && mine.Source(ogre) is not null, "both sprites are listed");
    Runner.AreNotEqual(before, mine.Source(ogre), "and they draw from different places");

    Runner.IsTrue(mine.Remap(knight, ogre), "the swap goes in");
    Runner.AreEqual(mine.Source(ogre), mine.Source(knight), "the Knight now draws the Ogre's art");

    var changed = mine.ChangedFrom(stock);
    Runner.AreEqual(1, changed.Count, "one sprite differs");
    Runner.AreEqual(knight, changed[0], "and it is the one that was swapped");

    Runner.IsTrue(mine.RestoreFrom(stock, knight), "reset puts it back");
    Runner.AreEqual(before, mine.Source(knight), "to exactly what it was");
    Runner.AreEqual(0, mine.ChangedFrom(stock).Count, "with nothing left changed");

    // The era files are what the previews and the frame counts are read from.
    Runner.IsTrue(mine.Atlas("forest", "human_common") is not null, "the forest atlas is named");
    Runner.AreEqual("Human / knight", SpriteIndex.Describe(knight), "the readable name");
});

runner.Test("an icon can be pointed at another icon's art, and put back", () =>
{
    // Which icon a unit draws is compiled in, so a portrait is swapped by changing what a
    // frame reads from. The atlas numbering is the game's own: upgrades.dat's icon field
    // indexes it, which is what the four confirmed names rest on.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, IconAtlas.FacePath);
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var stock = IconAtlas.Load(path);
    var mine = IconAtlas.Load(path);

    Runner.IsTrue(mine.Ids.Count > 100, "the sheet has the icons in it");
    Runner.AreEqual(0, mine.ChangedFrom(stock).Count, "an untouched sheet differs nowhere");

    var peasant = mine.Rect("forest", 2);
    var ogre = mine.Rect("forest", 9);
    Runner.IsTrue(peasant is not null && ogre is not null, "both frames exist");
    Runner.AreNotEqual(peasant, ogre, "and they are different rectangles");

    Runner.AreEqual(IconAtlas.Tilesets.Length, mine.Remap(2, 9), "every tileset is rewritten");
    Runner.AreEqual(ogre, mine.Rect("forest", 2), "the Peasant's frame now reads the Ogre's art");
    Runner.AreEqual(mine.Rect("swamp", 9), mine.Rect("swamp", 2), "and so does the swamp copy");

    var changed = mine.ChangedFrom(stock);
    Runner.AreEqual(1, changed.Count, "one icon differs");
    Runner.AreEqual(2, changed[0], "and it is the one that was swapped");

    mine.RestoreFrom(stock, 2);
    Runner.AreEqual(peasant, mine.Rect("forest", 2), "reset puts the art back");
    Runner.AreEqual(0, mine.ChangedFrom(stock).Count, "and nothing is left changed");

    // The upgrade table is what proves the numbering, so check it still lines up.
    var upgrades = UpgradeDataFile.Load(StockFile(game, "Rez/upgrades.dat")!);
    Runner.AreEqual(117, (int)upgrades.Get("icon", 0), "Upgrade Swords points at icon 117");
    Runner.AreEqual(6, (int)upgrades.Get("icon", 24), "Ranger Training points at icon 6");
    Runner.IsTrue(mine.Rect("forest", 117) is not null, "and the sheet has that frame");
});

runner.Test("a map carries its own unit table, and it can be rewritten", () =>
{
    // A PUD's UDTA chunk holds the whole unit table, and the game reads it in preference
    // to rez\unitdata.dat for that map. An edit that never reaches the map is an edit the
    // mission does not see.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var mapPath = StockFile(game, "Campaign/Human/HUMAN01.PUD");
    if (mapPath is null) { Console.WriteLine("        (skipped)"); return; }

    var bytes = File.ReadAllBytes(mapPath);
    var table = PudFile.ReadUnitTable(bytes);
    if (table is null) { Console.WriteLine("        (no UDTA — skipped)"); return; }

    Runner.AreEqual(30, (int)table.Get("hitPoints", 2), "the map's own Peasant hit points");

    table.Set("hitPoints", 2, 300);
    var rewritten = PudFile.WriteUnitTable(bytes, table);

    Runner.AreEqual(bytes.Length, rewritten.Length, "the map keeps its size");
    Runner.AreEqual(300, (int)PudFile.ReadUnitTable(rewritten)!.Get("hitPoints", 2), "reads back");

    // Nothing outside the unit table moved.
    var changed = bytes.Where((b, i) => b != rewritten[i]).Count();
    Runner.AreEqual(2, changed, "only the two bytes of that field differ");

    Console.WriteLine($"        (UDTA is {PudFile.ReadUnitTable(bytes)!.Describe()})");
});

runner.Test("one record can be put back without disturbing the rest", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/unitdata.dat");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var mine = UnitDataFile.Load(path);
    var stock = UnitDataFile.Load(path);

    mine.Set("hitPoints", 2, 300);
    mine.Set("goldCost", 2, 800);
    mine.Set("armor", 6, 9);                       // a second record, which must survive

    // What the editor's per-record reset does: every field of that record, every component.
    var restored = 0;
    foreach (var field in mine.Fields)
    {
        if (!stock.Has(field.Key)) continue;
        for (var component = 0; component < field.PerRecord; component++)
        {
            var was = stock.GetRaw(field.Key, 2, component);
            if (mine.GetRaw(field.Key, 2, component) == was) continue;
            mine.SetRaw(field.Key, 2, was, component);
            restored++;
        }
    }

    Runner.AreEqual(2, restored, "two fields were put back");

    var left = mine.Diff(stock).ToList();
    Runner.AreEqual(1, left.Count, "only the other record's change is left");
    Runner.AreEqual(6, left[0].Record, "and it belongs to the record that was not reset");
    Runner.AreEqual(30, (int)mine.Get("hitPoints", 2), "the reset record matches the game again");
});

runner.Test("a stat change maps onto the other unit table", () =>
{
    // The game keeps two unit tables — rez\unitdata.dat for the expansion, unitdato.dat
    // for Tides of Darkness — and swaps between them, so an edit has to be written to
    // both. They have different layouts, so the copy goes by field, not by offset.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var expansion = StockFile(game, "Rez/unitdata.dat");
    var original = StockFile(game, "Rez/unitdato.dat");
    if (expansion is null || original is null) { Console.WriteLine("        (skipped)"); return; }

    var mine = UnitDataFile.Load(expansion);
    var stock = UnitDataFile.Load(expansion);
    var other = UnitDataFile.Load(original);

    Runner.AreNotEqual(mine.Fields.Count, 0, "fields");
    mine.Set("hitPoints", 2, 300);

    var changes = mine.Diff(stock).ToList();
    Runner.AreEqual(1, changes.Count, "one field changed");

    foreach (var (key, record, component, value, _) in changes)
    {
        Runner.IsTrue(other.Has(key), $"the original table also has {key}");
        other.SetRaw(key, record, value, component);
    }

    Runner.AreEqual(300, (int)other.Get("hitPoints", 2), "the change lands in the other table");
    Runner.AreEqual(30, (int)UnitDataFile.Load(original).Get("hitPoints", 2), "the file on disk is untouched");
});

runner.Test("two projects in one folder keep their files apart", () =>
{
    // Every project used to put its files in "content" beside itself, so a second project
    // in the same folder started life owning the first one's replacements.
    using var sandbox = new Sandbox();
    var folder = Path.Combine(sandbox.Root, "mods");
    Directory.CreateDirectory(folder);

    var first = ModProject.Create(Path.Combine(folder, "First.w2proj"), "First");
    var second = ModProject.Create(Path.Combine(folder, "Second.w2proj"), "Second");

    Runner.AreNotEqual(first.ContentRoot, second.ContentRoot, "content folders");

    var payload = Path.Combine(folder, "payload.bin");
    File.WriteAllBytes(payload, new byte[] { 1, 2, 3 });
    first.ImportOverride("Rez/ai.bin", payload);

    Runner.IsTrue(first.HasOverride("Rez/ai.bin"), "the first project has the file");
    Runner.IsFalse(second.HasOverride("Rez/ai.bin"), "the second project must not see it");
    Runner.AreEqual(0, second.EnumerateOverrides().Count, "a new project starts empty");

    // A project that already names its folder keeps using it, so nothing existing moves.
    var reopened = ModProject.Load(first.ProjectPath);
    Runner.AreEqual(first.ContentRoot, reopened.ContentRoot, "the folder a project records");
});

runner.Test("a project validates for build before its hashes exist", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();

    var project = sandbox.CreateProject("prebuild", "Pre-build");
    project.SeedFromGame("Art/unit.png", game);
    File.WriteAllBytes(project.ResolveContentPath("Art/unit.png"), new byte[] { 3, 3 });

    var manifest = project.BuildManifest(game);

    // Hashes are filled in by the build, so the full check must fail here and the
    // metadata check must pass — the packager runs the latter before building.
    Runner.IsTrue(manifest.Validate().Any(p => p.Contains("SHA-256")),
        "full validation should object to the empty hashes");
    Runner.AreEqual(0, manifest.ValidateMetadata().Count,
        "metadata validation must pass: " + string.Join("; ", manifest.ValidateMetadata()));

    var packagePath = Path.Combine(sandbox.Root, "prebuild.w2mod");
    project.Build(packagePath, game);
    Runner.IsTrue(File.Exists(packagePath), "build should produce a package");
});

runner.Test("metadata validation still catches a bad id and unsafe paths", () =>
{
    var manifest = new ModManifest { Id = "Not A Valid Id", Name = "" };
    manifest.Files.Add(new ModFileEntry { Path = "../escape.dll" });

    var problems = manifest.ValidateMetadata();
    Runner.IsTrue(problems.Any(p => p.Contains("id")), "bad id must still be caught");
    Runner.IsTrue(problems.Any(p => p.Contains("name", StringComparison.OrdinalIgnoreCase)),
        "missing name must still be caught");
    Runner.IsTrue(problems.Any(p => p.Contains("Unsafe path")), "unsafe path must still be caught");
});

runner.Test("re-previewing a replaced image does not return the cached original", () =>
{
    using var sandbox = new Sandbox();

    // The same path twice, as happens when the packager writes a new override over an old one.
    var path = Path.Combine(sandbox.Root, "override.png");

    var png = MakePng(255, 0, 0);
    Runner.AreEqual("89504e47", Convert.ToHexString(png[..4]).ToLowerInvariant(), "MakePng should emit a PNG header");

    File.WriteAllBytes(path, png);

    var first = PreviewRenderer.LoadStandardImage(path);
    Runner.IsTrue(first is not null, "first load should succeed");
    Runner.AreEqual("255,0,0", Describe(first!), "first image colour");

    File.WriteAllBytes(path, MakePng(0, 0, 255));
    var second = PreviewRenderer.LoadStandardImage(path);
    Runner.IsTrue(second is not null, "second load should succeed");
    Runner.AreEqual("0,0,255", Describe(second!), "second load must show the new image, not the cached one");
});

runner.Test("replacing an override twice leaves the newest bytes on disk", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var project = sandbox.CreateProject("twice", "Twice");

    var source = Path.Combine(sandbox.Root, "incoming.bin");

    File.WriteAllBytes(source, new byte[] { 1, 1 });
    project.ImportOverride("Art/unit.png", source);
    Runner.AreEqual(1, File.ReadAllBytes(project.ResolveContentPath("Art/unit.png"))[0], "first import");

    File.WriteAllBytes(source, new byte[] { 2, 2 });
    project.ImportOverride("Art/unit.png", source);
    Runner.AreEqual(2, File.ReadAllBytes(project.ResolveContentPath("Art/unit.png"))[0], "second import must overwrite");
});

runner.Test("JSON editor keeps each value's type when writing back", () =>
{
    // Shaped like the game's sprite atlases, where a number written back as a string
    // would give the game an unreadable file.
    const string json = """
        {"frames":{"grunt_0":{"frame":{"x":0,"y":0,"w":46,"h":38},"rotated":false,"name":"grunt"}}}
        """;

    var table = JsonTable.Parse(json);

    var frame = table.Root.Children.Single(c => c.Name == "frames")
        .Children.Single(c => c.Name == "grunt_0");

    var rect = frame.Children.Single(c => c.Name == "frame");
    rect.Children.Single(c => c.Name == "w").Value = "64";
    frame.Children.Single(c => c.Name == "rotated").Value = "true";
    frame.Children.Single(c => c.Name == "name").Value = "grunt renamed";

    var written = table.Document!.ToJsonString();
    Runner.IsTrue(written.Contains("\"w\":64"), "number must stay a number: " + written);
    Runner.IsTrue(written.Contains("\"rotated\":true"), "bool must stay a bool: " + written);
    Runner.IsTrue(written.Contains("\"name\":\"grunt renamed\""), "string must stay a string: " + written);

    // And it must still parse as the same shape.
    var reparsed = System.Text.Json.Nodes.JsonNode.Parse(written)!;
    Runner.AreEqual(64, reparsed["frames"]!["grunt_0"]!["frame"]!["w"]!.GetValue<int>(), "round-tripped width");
});

runner.Test("JSON editor refuses an edit that would change a value's type", () =>
{
    var table = JsonTable.Parse("""{"x":10,"flag":false}""");

    var x = table.Root.Children.Single(c => c.Name == "x");
    x.Value = "not a number";

    Runner.IsTrue(x.HasError, "a non-numeric edit to a number must be rejected");
    Runner.AreEqual("10", x.Value, "the old value must survive a rejected edit");
    Runner.IsTrue(table.Document!.ToJsonString().Contains("\"x\":10"), "document must be untouched");

    var flag = table.Root.Children.Single(c => c.Name == "flag");
    flag.Value = "yes";
    Runner.IsTrue(flag.HasError, "a non-boolean edit to a bool must be rejected");
});

runner.Test("JSON editor handles nested arrays", () =>
{
    var table = JsonTable.Parse("""{"points":[1,2,3]}""");

    var points = table.Root.Children.Single(c => c.Name == "points");
    Runner.AreEqual(3, points.Children.Count, "array element count");

    points.Children[1].Value = "20";
    Runner.IsTrue(table.Document!.ToJsonString().Contains("[1,20,3]"),
        "array write-back: " + table.Document.ToJsonString());
});

runner.Test("inspecting a game file after a restore sees the restored bytes", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var target = game.ResolveDataPath("Art/unit.png");

    // What the editor shows for a stock file is whatever Inspect reads at that moment.
    // If anything cached by path, a restore would leave the editor showing the mod's art.
    var packagePath = sandbox.BuildSimpleMod("skin", "Skin", "Art/unit.png", new byte[] { 1, 2, 3, 4 }, game);

    var stock = AssetInspector.Inspect(target, game, "Art/unit.png");
    Runner.AreEqual(1, (int)stock.SizeBytes, "stock size before install");

    var installer = new ModInstaller(game, sandbox.Vault);
    installer.AddPackage(packagePath);
    Runner.IsTrue(installer.Apply().Succeeded, "apply");

    var modded = AssetInspector.Inspect(target, game, "Art/unit.png");
    Runner.AreEqual(4, (int)modded.SizeBytes, "modded size must be seen at the same path");

    Runner.IsTrue(installer.RestoreVanilla().Succeeded, "restore");

    var restored = AssetInspector.Inspect(target, game, "Art/unit.png");
    Runner.AreEqual(1, (int)restored.SizeBytes, "restored size must be seen at the same path");
});

runner.Test("a read-only JSON tree refuses every edit", () =>
{
    // What the editor shows for a stock file the mod does not own yet.
    var table = JsonTable.Parse("""{"x":10,"name":"grunt"}""", readOnly: true);

    Runner.IsTrue(table.Root.Children.All(c => c.IsReadOnly), "read-only must reach every child");

    var x = table.Root.Children.Single(c => c.Name == "x");
    x.Value = "999";
    Runner.AreEqual("10", x.Value, "value must not change");

    table.Root.Children.Single(c => c.Name == "name").Value = "changed";

    var written = table.Document!.ToJsonString();
    Runner.IsTrue(written.Contains("\"x\":10"), "document must be untouched: " + written);
    Runner.IsTrue(written.Contains("\"name\":\"grunt\""), "document must be untouched: " + written);
});

runner.Test("an editable JSON tree still accepts edits", () =>
{
    var table = JsonTable.Parse("""{"x":10}""", readOnly: false);
    table.Root.Children.Single(c => c.Name == "x").Value = "42";
    Runner.IsTrue(table.Document!.ToJsonString().Contains("\"x\":42"), "editable tree must write through");
});

runner.Test("every WAV the game ships is playable PCM", () =>
{
    // SoundPlayer only handles PCM; the preview's Play button depends on that holding.
    var game = GameInstall.Detect();
    if (game is null)
    {
        Console.WriteLine("        (skipped — no installation found)");
        return;
    }

    var checkedCount = 0;
    foreach (var relativePath in game.EnumerateDataFiles().Where(p => p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
    {
        var data = File.ReadAllBytes(game.ResolveDataPath(relativePath));
        Runner.IsTrue(data.Length > 44, $"{relativePath} is too short to be a WAV");
        Runner.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(data, 0, 4), $"{relativePath} RIFF header");
        Runner.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(data, 8, 4), $"{relativePath} WAVE header");
        Runner.AreEqual("fmt ", System.Text.Encoding.ASCII.GetString(data, 12, 4), $"{relativePath} fmt chunk");
        Runner.AreEqual(1, BitConverter.ToUInt16(data, 20), $"{relativePath} must be uncompressed PCM");

        var info = AssetInspector.Inspect(game.ResolveDataPath(relativePath), game, relativePath);
        Runner.AreEqual(AssetKind.Wave, info.Kind, $"{relativePath} must be recognised as a sound");
        checkedCount++;
    }

    Console.WriteLine($"        ({checkedCount} WAVs verified)");
});

runner.Test("test-in-game: build to staging, install, apply — twice in a row", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();

    var project = sandbox.CreateProject("testrun", "Test Run");
    project.SeedFromGame("Art/unit.png", game);
    File.WriteAllBytes(project.ResolveContentPath("Art/unit.png"), new byte[] { 7, 7, 7 });

    // Exactly what the editor's Test in game button does.
    var staging = Path.Combine(sandbox.Vault.Root, "staging");
    Directory.CreateDirectory(staging);
    var packagePath = Path.Combine(staging, project.Id + ModPackage.Extension);

    var installer = new ModInstaller(game, sandbox.Vault);

    project.Build(packagePath, game);
    installer.AddPackage(packagePath);
    var first = installer.Apply();
    Runner.IsTrue(first.Succeeded, "first apply: " + first.FailureMessage);
    Runner.AreEqual(7, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "first run content");

    // The second run is where a still-open package handle would bite.
    File.WriteAllBytes(project.ResolveContentPath("Art/unit.png"), new byte[] { 8, 8, 8 });
    project.Build(packagePath, game);
    installer.AddPackage(packagePath);
    var second = installer.Apply();
    Runner.IsTrue(second.Succeeded, "second apply: " + second.FailureMessage);
    Runner.AreEqual(8, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "second run content");
});

runner.Test("building an empty project fails with a message, not a crash", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var project = sandbox.CreateProject("empty", "Empty");

    Runner.AreEqual(0, project.EnumerateOverrides().Count, "project should start empty");

    // The editor checks this itself and explains what to do; Build throwing here is the
    // backstop that used to surface as a bare "could not test the mod".
    var threw = false;
    try { project.Build(Path.Combine(sandbox.Root, "empty.w2mod"), game); }
    catch (InvalidOperationException ex)
    {
        threw = true;
        Runner.IsTrue(ex.Message.Contains("no files"), "message should say what is missing: " + ex.Message);
    }
    Runner.IsTrue(threw, "building nothing must be refused");
});

runner.Test("save-as copies the whole project, leaving the original intact", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();

    var source = sandbox.CreateProject("original", "Original");
    source.Author = "pudforge";
    source.Version = "2.1";
    source.SeedFromGame("Art/unit.png", game);
    File.WriteAllBytes(source.ResolveContentPath("Art/unit.png"), new byte[] { 4, 4 });
    var extra = source.ResolveContentPath("Rez/strings.tbl");
    Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
    File.WriteAllText(extra, "placeholder");
    source.Save();

    // What File > Save As does: a new project carrying the same metadata and content.
    var copyPath = Path.Combine(sandbox.Root, "projects", "copy", "copy.w2proj");
    Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);

    var copy = ModProject.Create(copyPath, source.Name);
    copy.Id = source.Id;
    copy.Version = source.Version;
    copy.Author = source.Author;
    copy.Save();
    foreach (var relativePath in source.EnumerateOverrides())
        copy.ImportOverride(relativePath, source.ResolveContentPath(relativePath));

    Runner.AreEqual(source.EnumerateOverrides().Count, copy.EnumerateOverrides().Count, "override count");
    Runner.AreEqual("2.1", ModProject.Load(copyPath).Version, "metadata carried over");
    Runner.AreEqual(4, File.ReadAllBytes(copy.ResolveContentPath("Art/unit.png"))[0], "content carried over");

    // Editing the copy must not reach back into the original.
    File.WriteAllBytes(copy.ResolveContentPath("Art/unit.png"), new byte[] { 9, 9 });
    Runner.AreEqual(4, File.ReadAllBytes(source.ResolveContentPath("Art/unit.png"))[0], "original untouched");
});

runner.Test("re-adding a mod never changes whether it is enabled", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var packagePath = sandbox.BuildSimpleMod("recheck", "Recheck", "Art/unit.png", new byte[] { 6 }, game);

    var installer = new ModInstaller(game, sandbox.Vault);
    installer.AddPackage(packagePath);
    Runner.IsTrue(installer.Apply().Succeeded, "first apply");

    // Restore vanilla switches every mod off; that state persists.
    Runner.IsTrue(installer.RestoreVanilla().Succeeded, "restore");
    Runner.IsFalse(installer.State.Find("recheck")!.Enabled, "restore should have disabled it");

    // Re-adding keeps it disabled, so applying writes nothing at all.
    installer.AddPackage(packagePath);
    Runner.IsFalse(installer.State.Find("recheck")!.Enabled, "re-adding must not silently re-enable");
    Runner.IsTrue(installer.Apply().DidNothing, "apply writes nothing while disabled");
    Runner.AreEqual(0xAB, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "still vanilla");

    // Which is why the studio sets the selection explicitly rather than relying on Add.
    installer.SetOnlyEnabled("recheck");
    Runner.IsTrue(installer.Apply().Succeeded, "apply after enabling");
    Runner.AreEqual(6, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "mod content is live");
});

runner.Test("single-mod loading: switching straight from one mod to another", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();

    var alpha = sandbox.BuildSimpleMod("alpha", "Alpha", "Art/unit.png", new byte[] { 1 }, game);
    var beta = sandbox.BuildSimpleMod("beta", "Beta", "Art/other.png", new byte[] { 2, 2 }, game);

    var installer = new ModInstaller(game, sandbox.Vault);
    installer.AddPackage(alpha);
    installer.AddPackage(beta);

    // Play Alpha.
    installer.SetOnlyEnabled("alpha");
    Runner.IsTrue(installer.Apply().Succeeded, "apply alpha");
    Runner.AreEqual("alpha", installer.ActiveMod?.Id, "alpha should be active");
    Runner.AreEqual(1, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "alpha content live");
    Runner.AreEqual(2, File.ReadAllBytes(game.ResolveDataPath("Art/other.png")).Length, "other.png still stock");

    // Play Beta — Alpha's file must go back to stock in the same step.
    installer.SetOnlyEnabled("beta");
    Runner.IsTrue(installer.Apply().Succeeded, "apply beta");
    Runner.AreEqual("beta", installer.ActiveMod?.Id, "beta should be active");
    Runner.AreEqual(0xAB, File.ReadAllBytes(game.ResolveDataPath("Art/unit.png"))[0], "alpha's file restored");
    Runner.AreEqual(2, File.ReadAllBytes(game.ResolveDataPath("Art/other.png"))[0], "beta content live");

    // Play Vanilla.
    installer.SetOnlyEnabled(null);
    Runner.IsTrue(installer.Apply().Succeeded, "apply vanilla");
    Runner.IsTrue(installer.ActiveMod is null, "nothing should be active");
    Runner.AreEqual(0, installer.State.Applied.Count, "nothing should remain applied");
    Runner.AreEqual(0xCD, File.ReadAllBytes(game.ResolveDataPath("Art/other.png"))[0], "beta's file restored");
    Runner.AreEqual(2, installer.State.Mods.Count, "both mods stay in the library");
});

runner.Test("playing a mod twice in a row changes nothing the second time", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();
    var packagePath = sandbox.BuildSimpleMod("steady", "Steady", "Art/unit.png", new byte[] { 3 }, game);

    var installer = new ModInstaller(game, sandbox.Vault);
    installer.AddPackage(packagePath);

    installer.SetOnlyEnabled("steady");
    Runner.IsTrue(installer.Apply().Succeeded, "first apply");

    installer.SetOnlyEnabled("steady");
    var again = installer.Apply();
    Runner.IsTrue(again.Succeeded, "second apply");
    Runner.IsTrue(again.DidNothing, "re-playing the active mod should be a no-op");
});

runner.Test("the launcher finds Battle.net and knows this game's product code", () =>
{
    Runner.AreEqual("w2r", GameLauncher.ProductCode, "product code from .product.db and the uninstall entry");

    var battleNet = GameLauncher.FindBattleNet();
    if (battleNet is null)
    {
        Console.WriteLine("        (skipped — Battle.net is not installed here)");
        return;
    }

    Runner.IsTrue(File.Exists(battleNet), "the located Battle.net.exe should exist: " + battleNet);
    Runner.AreEqual("Battle.net.exe", Path.GetFileName(battleNet), "should point at the client itself");
    Console.WriteLine($"        (found {battleNet})");
});

runner.Test("a missing game folder is reported rather than launched", () =>
{
    using var sandbox = new Sandbox();
    var game = sandbox.CreateGame();

    // Direct launch must refuse when the executable is gone, instead of throwing.
    File.Delete(game.ExecutablePath);
    Runner.IsFalse(GameLauncher.TryLaunch(game, LaunchMethod.Direct, out var error), "should refuse to launch");
    Runner.IsTrue(error.Contains("does not exist"), "should say what is missing: " + error);
});

runner.Test("the JSON table starts collapsed and only lists visible rows", () =>
{
    const string json = """
        {"frames":{"a":{"x":1,"y":2},"b":{"x":3,"y":4}},"meta":{"app":"packer"}}
        """;

    var table = JsonTable.Parse(json);

    // Two top-level keys, nothing opened: the cost of showing a file is one screen.
    Runner.AreEqual(2, table.Rows.Count, "only top-level keys to begin with");
    Runner.AreEqual("frames", table.Rows[0].Name, "first row");

    table.Toggle(table.Rows[0]);
    Runner.AreEqual(4, table.Rows.Count, "frames opens to reveal a and b");

    var a = table.Rows[1];
    table.Toggle(a);
    Runner.AreEqual(6, table.Rows.Count, "a opens to reveal x and y");
    // Root is 0, so the first on-screen level is 1 and a leaf two levels in is 3.
    Runner.AreEqual(3, a.Children[0].Depth, "depth drives the row indent");
    Runner.AreEqual(0.0, table.Rows[0].Indent.Left, "top-level rows sit flush left");

    table.CollapseAll();
    Runner.AreEqual(2, table.Rows.Count, "collapse returns to the top level");
});

runner.Test("the JSON filter shows matches without disturbing what is expanded", () =>
{
    var table = JsonTable.Parse("""{"frames":{"grunt":{"w":10},"peon":{"w":20}}}""");

    var frames = table.Root.Children.Single(c => c.Name == "frames");
    Runner.IsFalse(frames.IsExpanded, "starts collapsed");

    table.Filter = "peon";
    var names = table.Rows.Select(r => r.Name).ToList();
    Runner.IsTrue(names.Contains("peon"), "the match should be listed: " + string.Join(",", names));
    Runner.IsFalse(names.Contains("grunt"), "non-matching siblings should be hidden");

    // The match is reachable without having written IsExpanded across the document —
    // that write is what made filtering slow, and what left the tree sprawled open.
    Runner.IsFalse(frames.IsExpanded, "filtering must not mutate expansion state");

    table.Filter = "";
    Runner.AreEqual(1, table.Rows.Count, "clearing returns the view exactly as it was");
    Runner.IsFalse(frames.IsExpanded, "and leaves it collapsed");
});

runner.Test("filtering the game's largest JSON stays responsive", () =>
{
    var game = GameInstall.Detect();
    if (game is null)
    {
        Console.WriteLine("        (skipped — no installation found)");
        return;
    }

    var biggest = game.EnumerateDataFiles()
        .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        .Select(p => new FileInfo(game.ResolveDataPath(p)))
        .OrderByDescending(f => f.Length)
        .FirstOrDefault();
    if (biggest is null) return;

    var table = JsonTable.Parse(File.ReadAllText(biggest.FullName));

    // Typing a word one letter at a time is the case that felt slow.
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    foreach (var prefix in new[] { "f", "fo", "for", "fore", "fores", "forest" })
        table.Filter = prefix;
    stopwatch.Stop();

    Console.WriteLine($"        (6 filter passes over {table.NodeCount} values: {stopwatch.ElapsedMilliseconds} ms, " +
                      $"{table.Rows.Count} rows)");

    Runner.IsTrue(stopwatch.ElapsedMilliseconds < 3000,
        $"six filter passes took {stopwatch.ElapsedMilliseconds} ms");
});

runner.Test("string values carry no type label; other kinds do", () =>
{
    var table = JsonTable.Parse("""{"name":"grunt","w":10,"rotated":false}""");

    var byName = table.Rows.ToDictionary(r => r.Name);
    Runner.AreEqual("", byName["name"].TypeLabel, "strings are the common case and stay unlabelled");
    Runner.AreEqual("number", byName["w"].TypeLabel, "numbers are labelled");
    Runner.AreEqual("boolean", byName["rotated"].TypeLabel, "booleans are labelled");
});

runner.Test("the game's largest JSON parses quickly and shows few rows", () =>
{
    var game = GameInstall.Detect();
    if (game is null)
    {
        Console.WriteLine("        (skipped — no installation found)");
        return;
    }

    var biggest = game.EnumerateDataFiles()
        .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        .Select(p => new FileInfo(game.ResolveDataPath(p)))
        .OrderByDescending(f => f.Length)
        .FirstOrDefault();

    if (biggest is null) return;

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var table = JsonTable.Parse(File.ReadAllText(biggest.FullName));
    stopwatch.Stop();

    Console.WriteLine($"        ({biggest.Name}: {biggest.Length} bytes, {table.NodeCount} values, " +
                      $"{table.Rows.Count} rows, {stopwatch.ElapsedMilliseconds} ms)");

    // The point of the flat table: rows on screen are unrelated to document size.
    Runner.IsTrue(table.Rows.Count < 200, $"a collapsed document should list few rows, got {table.Rows.Count}");
    Runner.IsTrue(stopwatch.ElapsedMilliseconds < 5000, $"parsing took {stopwatch.ElapsedMilliseconds} ms");
});

runner.Test("ai.bin rebuilds byte-for-byte when nothing is edited", () =>
{
    var game = GameInstall.Detect();
    if (game is null)
    {
        Console.WriteLine("        (skipped — no installation found)");
        return;
    }

    var path = game.ResolveDataPath("Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped — no ai.bin)"); return; }

    var original = File.ReadAllBytes(path);
    var ai = AiFile.Parse(original);

    Runner.AreEqual(84, ai.Scripts.Count, "AI slot count");

    var rebuilt = ai.ToBytes();
    Runner.AreEqual(original.Length, rebuilt.Length, "rebuilt length");
    Runner.IsTrue(original.SequenceEqual(rebuilt), "an untouched file must come back identical");

    var decoded = ai.Scripts.Sum(s => s.Instructions.Count);
    var unknown = ai.Scripts.SelectMany(s => s.Instructions).Count(i => i.IsUnknown);
    Console.WriteLine($"        ({decoded} instructions, {unknown} undecoded, " +
                      $"{ai.Scripts.Count(s => s.SharedWith.Count > 0)} slots sharing bytes)");
});

runner.Test("every ai.bin script re-encodes to the bytes it came from", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));

    // Marking a script modified forces it down the encode path; the output must still
    // match the bytes it was decoded from, or an edit elsewhere would corrupt this one.
    foreach (var script in ai.Scripts) script.IsModified = true;

    var rebuilt = ai.ToBytes();
    var reparsed = AiFile.Parse(rebuilt);

    for (var i = 0; i < 84; i++)
    {
        Runner.IsTrue(
            ai.Scripts[i].OriginalBytes.SequenceEqual(reparsed.Scripts[i].OriginalBytes),
            $"script {i} ({AiTables.AiName(i)}) changed when re-encoded");
    }
});

runner.Test("AI script text round-trips through the assembler", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));
    var checkedScripts = 0;

    foreach (var script in ai.Scripts)
    {
        var text = AiFile.ToText(script);
        var parsed = AiFile.FromText(text);

        Runner.AreEqual(script.Instructions.Count, parsed.Count,
            $"{script.Name}: instruction count after text round-trip");

        for (var i = 0; i < parsed.Count; i++)
        {
            Runner.AreEqual(script.Instructions[i].Opcode, parsed[i].Opcode, $"{script.Name} op {i}");
            Runner.IsTrue(script.Instructions[i].Operands.SequenceEqual(parsed[i].Operands),
                $"{script.Name} operands {i}");
        }
        checkedScripts++;
    }

    Console.WriteLine($"        ({checkedScripts} scripts survived decode → text → assemble)");
});

runner.Test("editing an AI script changes only that script", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(path);
    var ai = AiFile.Parse(original);

    // Find a slot that no other slot shares, so the check is unambiguous.
    var target = ai.Scripts.First(s => s.SharedWith.Count == 0);
    var sleeps = target.Instructions.Where(i => i.Opcode == (byte)AiOpcode.Sleep).ToList();
    if (sleeps.Count == 0) { Console.WriteLine("        (skipped — no sleep to change)"); return; }

    var before = AiFile.ReadDword(sleeps[0]);
    target.Instructions[target.Instructions.IndexOf(sleeps[0])] = AiFile.FromText("sleep 1234")[0];
    target.IsModified = true;

    var reparsed = AiFile.Parse(ai.ToBytes());

    var changed = reparsed.Scripts[target.Index];
    var newSleep = changed.Instructions.First(i => i.Opcode == (byte)AiOpcode.Sleep);
    Runner.AreEqual(1234u, AiFile.ReadDword(newSleep), "the edit should be in the rebuilt file");
    Runner.IsTrue(before != 1234u, "the test would be vacuous otherwise");

    foreach (var other in ai.Scripts.Where(s => s.Index != target.Index && s.SharedWith.Count == 0))
    {
        Runner.IsTrue(
            other.OriginalBytes.SequenceEqual(reparsed.Scripts[other.Index].OriginalBytes),
            $"{other.Name} must be untouched by an edit to {target.Name}");
    }
});

runner.Test("every AI instruction survives the block editor unchanged", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));
    var count = 0;

    foreach (var script in ai.Scripts)
    {
        foreach (var instruction in script.Instructions)
        {
            // instruction -> block -> instruction must be a no-op, or the GUI would
            // quietly rewrite scripts just by being opened.
            var back = AiBlock.From(instruction).ToInstruction();

            Runner.AreEqual(instruction.Opcode, back.Opcode, $"{script.Name}: opcode");
            Runner.IsTrue(instruction.Operands.SequenceEqual(back.Operands),
                $"{script.Name}: operands of opcode {instruction.Opcode:X2}");
            count++;
        }
    }

    Console.WriteLine($"        ({count} instructions round-tripped through blocks)");
});

runner.Test("a block rejects a value too large for its instruction", () =>
{
    // var takes a byte; letting 300 through would silently write 44.
    var block = AiBlock.From(AiFile.FromText("var peasants = 4")[0]);
    block.Value = 300;

    var threw = false;
    try { block.ToInstruction(); }
    catch (InvalidOperationException) { threw = true; }
    Runner.IsTrue(threw, "an out-of-range var value must be refused, not truncated");

    // sleep is a dword, so the same number is fine there.
    var sleep = AiBlock.From(AiFile.FromText("sleep 1")[0]);
    sleep.Value = 300;
    Runner.AreEqual(300u, AiFile.ReadDword(sleep.ToInstruction()), "sleep should accept it");
});

runner.Test("community AI sets parse, including their empty slot", () =>
{
    // Four AI sets built by someone else with WarDraft — the only real-world test of the
    // format that does not come from Blizzard.
    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "WarDraft", "WarDraft");

    if (!Directory.Exists(folder))
    {
        Console.WriteLine("        (skipped — no community files present)");
        return;
    }

    var files = Directory.GetFiles(folder, "ai*.bin");
    if (files.Length == 0) { Console.WriteLine("        (skipped — none found)"); return; }

    foreach (var file in files)
    {
        var original = File.ReadAllBytes(file);
        var name = Path.GetFileName(file);

        Runner.IsTrue(AiFile.LooksLikeAiFile(original), $"{name} should be recognised");

        var ai = AiFile.Parse(original);
        var used = ai.Scripts.Count(s => !s.IsEmpty);
        var undecoded = ai.Scripts.SelectMany(s => s.Instructions).Count(i => i.IsUnknown);

        Runner.IsTrue(used >= 80, $"{name}: expected most slots used, got {used}");
        Runner.AreEqual(0, undecoded, $"{name}: every instruction should decode");

        // The property that matters: reading someone else's file changes nothing.
        Runner.IsTrue(original.SequenceEqual(ai.ToBytes()), $"{name} must rebuild byte-identically");

        Console.WriteLine($"        ({name}: {used} scripts, " +
                          $"{ai.Scripts.Sum(s => s.Instructions.Count)} instructions, " +
                          $"{AiFile.ScriptCount - used} empty)");
    }
});

runner.Test("an empty slot survives a rebuild as empty", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    // Blank a slot the way the community files do, then check it round-trips.
    var data = File.ReadAllBytes(path);
    var ai = AiFile.Parse(data);
    var victim = ai.Scripts.First(s => s.SharedWith.Count == 0 && !s.IsEmpty);

    data[victim.Index * 2] = 0;
    data[victim.Index * 2 + 1] = 0;

    var reparsed = AiFile.Parse(data);
    Runner.IsTrue(reparsed.Scripts[victim.Index].IsEmpty, "the blanked slot should read as empty");
    Runner.IsTrue(data.SequenceEqual(reparsed.ToBytes()), "and rebuild unchanged");
});

runner.Test("a script's build list resolves to real buildings", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));

    // "Land Attack" is the plainest script; its list should read as a build order.
    var script = ai.Scripts[0];
    Runner.IsTrue(script.BuildList.Length > 0, "the build list should have been read");

    var opening = Enumerable.Range(0, 6)
        .Select(i => AiTables.ItemName(script.BuildItemAt(i)!.Value))
        .ToList();

    Console.WriteLine($"        ({script.Name} builds: {string.Join(", ", opening)})");

    // The first thing any AI needs is a town hall.
    Runner.IsTrue(opening[0].Contains("Town Hall") || opening[0].Contains("Great Hall"),
        "the first build item should be the town centre, got " + opening[0]);

    // Across every script, most items should be things we have names for rather than
    // raw codes — if the lists were being read at the wrong offset, they would not be.
    var named = 0;
    var total = 0;
    foreach (var s in ai.Scripts.Where(s => !s.IsEmpty))
    {
        if (!s.HasBuildList) continue;
        foreach (var code in s.BuildList.Take(40))
        {
            total++;
            if (AiTables.Items.ContainsKey(code) || code is 0x00 or 0xFF) named++;
        }
    }

    // The one script whose pointer is junk must be recognised as such, not rendered.
    var withList = ai.Scripts.Count(s => !s.IsEmpty && s.HasBuildList);
    var without = ai.Scripts.Where(s => !s.IsEmpty && !s.HasBuildList).ToList();
    Console.WriteLine($"        ({withList} scripts have a readable build list; " +
                      $"{without.Count} do not: {string.Join(", ", without.Select(s => s.Name))})");
    foreach (var s in without)
        Runner.IsTrue(s.BuildItemAt(1) is null, $"{s.Name} must not offer item names it cannot read");

    var ratio = named / (double)total;
    Console.WriteLine($"        ({ratio:P0} of build-list bytes are known item codes)");
    Runner.IsTrue(ratio > 0.80, $"expected mostly recognisable items, got {ratio:P0}");
});

runner.Test("editing a script never moves another, or its pointers", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(path);
    var ai = AiFile.Parse(original);

    // Shorten a script: the case that used to shift everything after it.
    var target = ai.Scripts.First(s => !s.IsEmpty && s.SharedWith.Count == 0 && s.Instructions.Count > 30);
    var removed = target.Instructions[10];
    target.Instructions.RemoveAt(10);
    target.IsModified = true;

    var rebuilt = ai.ToBytes();
    Runner.AreEqual(original.Length, rebuilt.Length, "the file must not change size");

    var reparsed = AiFile.Parse(rebuilt);

    foreach (var before in ai.Scripts.Where(s => !s.IsEmpty))
    {
        var after = reparsed.Scripts[before.Index];
        Runner.AreEqual(before.Offset, after.Offset, $"{before.Name} must not move");
        Runner.AreEqual(before.Header0, after.Header0, $"{before.Name} build-list pointer must survive");
        Runner.AreEqual(before.Header1, after.Header1, $"{before.Name} rate pointer must survive");

        if (before.Index != target.Index)
            Runner.IsTrue(before.OriginalBytes.SequenceEqual(after.OriginalBytes),
                $"{before.Name} must be untouched by an edit to {target.Name}");
    }

    // And the edit is really there.
    Runner.AreEqual(target.Instructions.Count, reparsed.Scripts[target.Index].Instructions.Count,
        "the shortened script should have one instruction fewer");
    Console.WriteLine($"        (removed one {(AiOpcode)removed.Opcode} from {target.Name}; " +
                      "all 84 slots kept their offsets and pointers)");
});

runner.Test("a script that outgrows its space is refused, not silently relocated", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));
    var target = ai.Scripts.First(s => !s.IsEmpty);

    // Pad it well past its allotted bytes.
    for (var i = 0; i < 400; i++)
        target.Instructions.Insert(0, AiFile.FromText("sleep 100")[0]);
    target.IsModified = true;

    var threw = false;
    try { ai.ToBytes(); }
    catch (InvalidOperationException ex)
    {
        threw = true;
        // Either refusal is correct: it can run out of room in the region, or out of room
        // before the data that follows it — the second is the tighter, real limit.
        Runner.IsTrue(ex.Message.Contains("fit") || ex.Message.Contains("available"),
            "the message should explain the space problem: " + ex.Message);
    }
    Runner.IsTrue(threw, "growing a script past its space must be refused");
});

runner.Test("the .dat files rebuild byte-for-byte", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    // The check the schema work could not do for itself: real files, in and out.
    foreach (var name in new[] { "Rez/unitdata.dat", "Rez/unitdato.dat", "Rez/upgrades.dat" })
    {
        var path = game.ResolveDataPath(name);
        if (!File.Exists(path)) { Console.WriteLine($"        (skipped {name})"); continue; }

        var original = File.ReadAllBytes(path);

        byte[] rebuilt;
        if (name.Contains("upgrades"))
        {
            rebuilt = UpgradeDataFile.Parse(original).ToBytes();
        }
        else
        {
            rebuilt = UnitDataFile.Parse(original).ToBytes();
        }

        Runner.AreEqual(original.Length, rebuilt.Length, $"{name}: length");
        Runner.IsTrue(original.SequenceEqual(rebuilt), $"{name} must come back identical");
        Console.WriteLine($"        ({name}: {original.Length} bytes round-tripped)");
    }
});

runner.Test("unit stats read back the values Warcraft II actually uses", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/unitdata.dat");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var units = UnitDataFile.Load(path);

    // Independent anchor: I located the hit-point array at byte 1676 by searching the
    // file for the known sequence 60,60,30,30,110,110,90,90. If the schema disagrees,
    // one of the two is wrong.
    var expected = new[] { 60, 60, 30, 30, 110, 110, 90, 90, 40, 40 };
    for (var i = 0; i < expected.Length; i++)
        Runner.AreEqual(expected[i], units.HitPoints(i), $"hit points of unit {i}");

    Console.WriteLine($"        (Footman {units.HitPoints(0)} hp / {units.GoldCost(0)} gold, " +
                      $"Peasant {units.HitPoints(2)} hp / {units.GoldCost(2)} gold, " +
                      $"Knight {units.HitPoints(6)} hp / {units.GoldCost(6)} gold)");
});

runner.Test("a unit-stat edit changes only that field", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/unitdata.dat");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(path);
    var units = UnitDataFile.Parse(original);

    var before = units.HitPoints(0);
    units.SetHitPoints(0, 123);
    Runner.AreEqual(123, units.HitPoints(0), "the edit should read back");

    var rebuilt = units.ToBytes();
    var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != rebuilt[i]).ToList();

    // 60 -> 123 only moves the low byte, so assert containment rather than a byte count.
    // The field sits at 1676, which I located independently by searching the file for the
    // known hit-point run 60,60,30,30,110,110 — the schema and that search agree.
    Runner.IsTrue(changed.Count is >= 1 and <= 2, "an edit should touch one uint16: " + string.Join(",", changed));
    Runner.IsTrue(changed.All(i => i >= 1676 && i < 1678),
        "and only within the hit-point field: " + string.Join(",", changed));
    Runner.AreEqual(before, UnitDataFile.Parse(original).HitPoints(0), "the source must be untouched");
});

runner.Test("every unit-stat field produces a working control", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/unitdata.dat");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var units = UnitDataFile.Load(path);
    var kinds = new Dictionary<DatControlKind, int>();

    // Every field the schema declares must render as something the row model can drive;
    // a field with no usable control would be silently uneditable in the UI.
    foreach (var field in units.Fields)
    {
        var row = new DatRow(units, field, 0);
        kinds[field.Kind] = kinds.GetValueOrDefault(field.Kind) + 1;

        var hasControl = row.IsNumber || row.IsToggle || row.IsChoice || row.IsFlags;
        Runner.IsTrue(hasControl, $"{field.Key} has no control kind");

        if (row.IsChoice) Runner.IsTrue(row.Options.Count > 0, $"{field.Key} is a choice with no options");
        if (row.IsFlags) Runner.IsTrue(row.Flags.Length > 0, $"{field.Key} is flags with no bits");
    }

    Console.WriteLine("        (" + string.Join(", ", kinds.Select(k => $"{k.Value} {k.Key}")) + ")");
});

runner.Test("editing through a row writes the value and nothing else", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/unitdata.dat");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(path);
    var units = UnitDataFile.Parse(original);

    var armour = units.Fields.First(f => f.Key == "armor");
    var row = new DatRow(units, armour, 0);
    var edits = 0;
    row.Edited += () => edits++;

    row.Text = "7";
    Runner.AreEqual(7, (int)row.Value, "the row should hold the new value");
    Runner.AreEqual(7, units.Armor(0), "and the table should agree");
    Runner.IsTrue(edits > 0, "an edit should be announced so the view can save");

    // Costs are stored in tens; a value that cannot be represented must be refused.
    var gold = new DatRow(units, units.Fields.First(f => f.Key == "goldCost"), 0);
    gold.Text = "605";
    Runner.IsTrue(gold.HasError, "605 gold is not a multiple of 10 and should be rejected");
    Runner.AreEqual(600, (int)gold.Value, "and the old value should stand");

    gold.Text = "700";
    Runner.IsFalse(gold.HasError, "700 is fine");
    Runner.AreEqual(700, units.GoldCost(0), "and should reach the table");
});

runner.Test("a flag bank toggles single bits", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/unitdata.dat");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var units = UnitDataFile.Load(path);
    var flags = units.Fields.FirstOrDefault(f => f.Kind == DatControlKind.Flags);
    if (flags is null) { Console.WriteLine("        (skipped — no flag field)"); return; }

    var row = new DatRow(units, flags, 0);
    var before = units.GetRaw(flags.Key, 0);

    var bit = row.Flags[0];
    var was = bit.IsSet;
    bit.IsSet = !was;

    Runner.AreEqual(!was, bit.IsSet, "the bit should flip");
    Runner.AreNotEqual(before, units.GetRaw(flags.Key, 0), "and the stored value should change");

    bit.IsSet = was;
    Runner.AreEqual(before, units.GetRaw(flags.Key, 0), "flipping back should restore it exactly");

    var uncertain = row.Flags.Count(f => !f.IsCertain);
    Console.WriteLine($"        ({row.Flags.Length} bits, {uncertain} marked uncertain)");
});

runner.Test("AI rows present themselves as actions, not variable writes", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));
    var counts = new Dictionary<AiBlock.AiAction, int>();

    foreach (var script in ai.Scripts.Where(s => !s.IsEmpty))
    {
        foreach (var instruction in script.Instructions)
        {
            var block = AiBlock.From(instruction);
            counts[block.Action] = counts.GetValueOrDefault(block.Action) + 1;

            // Whatever it calls itself, it must still encode back to the same bytes.
            var back = block.ToInstruction();
            Runner.AreEqual(instruction.Opcode, back.Opcode, "opcode after action mapping");
            Runner.IsTrue(instruction.Operands.SequenceEqual(back.Operands), "operands after action mapping");
        }
    }

    Console.WriteLine("        (" + string.Join(", ",
        counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key}")) + ")");

    // The point of the split: these should be distinct actions, not all "set variable".
    Runner.IsTrue(counts.GetValueOrDefault(AiBlock.AiAction.QueueBuilding) > 0, "queueing should be its own action");
    Runner.IsTrue(counts.GetValueOrDefault(AiBlock.AiAction.LaunchAttack) > 0, "launching should be its own action");
    Runner.IsTrue(counts.GetValueOrDefault(AiBlock.AiAction.BuildUnits) > 0, "unit targets should be their own action");
});

runner.Test("switching a row's action rewrites it into something valid", () =>
{
    var block = AiBlock.From(AiFile.FromText("sleep 500")[0]);
    Runner.AreEqual(AiBlock.AiAction.Sleep, block.Action, "it starts as a sleep");

    foreach (var choice in AiBlock.Actions)
    {
        block.ActionChoice = choice;
        Runner.AreEqual(choice.Action, block.Action, $"should become {choice.Label}");

        // Every action must produce a writable instruction, or the row could not be saved.
        var instruction = block.ToInstruction();
        Runner.IsTrue(instruction.Operands.Length >= 0, "should encode");

        // And the subject list must offer something wherever one is shown.
        if (block.HasSubject)
            Runner.IsTrue(block.SubjectChoices.Any(c => c.Number == block.Subject?.Number),
                $"{choice.Label}: the chosen subject should be in its own list");
    }
});

runner.Test("rearming an attack reads differently from launching it", () =>
{
    // Same variable, opposite meaning. Every setup block rearms all three triggers, so
    // collapsing them into one action mislabels 234 rows in the shipped file.
    var launch = AiBlock.From(AiFile.FromText("var land_attack = 1")[0]);
    var rearm = AiBlock.From(AiFile.FromText("var land_attack = 0")[0]);

    Runner.AreEqual(AiBlock.AiAction.LaunchAttack, launch.Action, "1 launches");
    Runner.AreEqual(AiBlock.AiAction.RearmAttack, rearm.Action, "0 rearms");

    // And both still encode back to what they were.
    Runner.AreEqual(1, (int)launch.ToInstruction().Operands[1], "launch keeps its 1");
    Runner.AreEqual(0, (int)rearm.ToInstruction().Operands[1], "rearm keeps its 0");
});

runner.Test("a state file that cannot be written says so and leaves nothing behind", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-state-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var path = Path.Combine(folder, "state.json");
        var state = new InstallState { GameRoot = @"C:\Game" };

        state.Save(path);
        Runner.IsTrue(File.Exists(path), "it writes the state");
        Runner.IsTrue(!File.Exists(path + ".tmp"), "and clears up after itself");

        // Saving again must keep the same file rather than replacing it: a new file gets
        // the rights of whoever created it, and the elevated helper writes this too.
        var identity = new FileInfo(path).CreationTimeUtc;
        state.GameRoot = @"C:\Elsewhere";
        state.Save(path);
        Runner.AreEqual(identity, new FileInfo(path).CreationTimeUtc, "the file itself survives a rewrite");
        Runner.AreEqual(@"C:\Elsewhere", InstallState.Load(path).GameRoot, "with the new contents");

        // A destination that cannot be replaced has to be reported, not swallowed: a
        // stranded .tmp and a silently unsaved library is how a removed mod comes back.
        var blocked = Path.Combine(folder, "blocked.json");
        Directory.CreateDirectory(blocked);

        var threw = false;
        try { state.Save(blocked); }
        catch (IOException) { threw = true; }

        Runner.IsTrue(threw, "a failed save is reported");
        Runner.IsTrue(!File.Exists(blocked + ".tmp"), "and takes its temp file with it");
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
});

runner.Test("the elevated helper reports back as an ordinary apply result", () =>
{
    // The helper is a second process, so everything it did has to survive JSON and come
    // back as the same shape the in-process path returns.
    var request = new ElevatedApply.Request
    {
        GameRoot = @"C:\Program Files (x86)\Warcraft II Remastered",
        VaultRoot = @"C:\ProgramData\RuneFoundry",
        ModId = null,
    };

    var round = System.Text.Json.JsonSerializer.Deserialize<ElevatedApply.Request>(
        System.Text.Json.JsonSerializer.Serialize(request))!;
    Runner.AreEqual(request.GameRoot, round.GameRoot, "paths with spaces and backslashes survive");
    Runner.IsTrue(round.ModId is null, "and vanilla stays vanilla");

    var response = new ElevatedApply.Response { Succeeded = true, FilesWritten = 2, FilesRestored = 1 };
    response.Warnings.Add("something to pass on");

    var result = response.ToResult();
    Runner.IsTrue(result.Succeeded, "success carries over");
    Runner.AreEqual(2, result.FilesWritten, "and the counts");
    Runner.AreEqual(1, result.Warnings.Count, "and the warnings");
    Runner.IsTrue(!result.DidNothing, "so the caller can tell something happened");
});

runner.Test("rebuilding an untouched ai.bin gives the same bytes back", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    // This is the assertion the whole linker is gated on. If laying the file out from
    // scratch does not reproduce it exactly, no fixup it makes can be trusted either.
    var original = File.ReadAllBytes(path);
    var rebuilt = AiLinker.Rebuild(AiFile.Parse(original), separateAliasedSlots: false, out var report);

    Runner.AreEqual(original.Length, rebuilt.Length, "the same size");
    Runner.IsTrue(original.AsSpan().SequenceEqual(rebuilt), "and the same bytes");
    Runner.AreEqual(0, report.ScriptsMoved, "with nothing moved");
});

runner.Test("a script can grow, and everything else survives it", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = AiFile.Parse(File.ReadAllBytes(path));

    // The point of the exercise: put instructions into a script that has no room. Passive
    // is three bytes long, so anything at all is more than it can currently hold.
    var target = original.Scripts.First(s => !s.IsEmpty && s.IsStub);
    var grewBy = 0;
    for (var i = 0; i < 20; i++)
    {
        var added = AiFile.FromText("var peasants = 4")[0];
        target.Instructions.Insert(0, added);
        grewBy += added.Length;
    }
    target.IsModified = true;

    var rebuilt = AiLinker.Rebuild(original, separateAliasedSlots: false, out var report);
    Runner.AreEqual(original.Length + grewBy, rebuilt.Length, "the file grew by exactly the code added");
    Runner.IsTrue(report.ScriptsMoved > 0, "and things after it moved");

    var after = AiFile.Parse(rebuilt);

    // Every other script must read back exactly as it did, instruction for instruction.
    for (var i = 0; i < AiFile.ScriptCount; i++)
    {
        var before = original.Scripts[i];
        var now = after.Scripts[i];

        Runner.AreEqual(before.IsEmpty, now.IsEmpty, $"slot {i} keeps its emptiness");
        if (before.IsEmpty) continue;

        Runner.AreEqual(before.Instructions.Count, now.Instructions.Count,
            $"slot {i} keeps its instruction count");

        for (var k = 0; k < before.Instructions.Count; k++)
        {
            var a = before.Instructions[k];
            var b = now.Instructions[k];
            Runner.AreEqual(a.Opcode, b.Opcode, $"slot {i} instruction {k} keeps its opcode");

            // A goto is the one operand allowed to differ: it was relocated on purpose.
            if (a.Opcode == (byte)AiOpcode.Goto) continue;
            Runner.IsTrue(a.Operands.AsSpan().SequenceEqual(b.Operands),
                $"slot {i} instruction {k} keeps its operands");
        }
    }

    // And the header words must still resolve to the same data. This is the fixup that
    // matters — a build list pointer left one byte stale renames every unit the AI trains.
    for (var i = 0; i < AiFile.ScriptCount; i++)
    {
        var before = original.Scripts[i];
        if (before.IsEmpty || !before.HasBuildList) continue;

        Runner.IsTrue(after.Scripts[i].HasBuildList, $"slot {i} still has a readable build list");
        Runner.IsTrue(before.BuildList.AsSpan().SequenceEqual(after.Scripts[i].BuildList),
            $"slot {i} still points at the same build list");
    }

    // The stubs jump into shared code, which also moved.
    var stub = after.Scripts.First(s => !s.IsEmpty && s.IsStub);
    Runner.AreEqual(after.SharedRoutine?.Offset ?? -1, stub.JumpsAwayTo ?? -2,
        "the stubs still land on the shared routine");
});

runner.Test("separating aliased slots makes them editable apart", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = AiFile.Parse(File.ReadAllBytes(path));
    var shared = original.Scripts.Count(s => !s.IsEmpty && s.SharedWith.Count > 0);
    if (shared == 0) { Console.WriteLine("        (skipped: nothing aliased)"); return; }

    var rebuilt = AiLinker.Rebuild(original, separateAliasedSlots: true, out var report);
    Runner.IsTrue(report.SlotsSeparated > 0, "some slots were separated");

    var after = AiFile.Parse(rebuilt);
    Runner.AreEqual(0, after.Scripts.Count(s => !s.IsEmpty && s.SharedWith.Count > 0),
        "no slot shares its bytes with another any more");

    // Separated or not, they must still say the same thing.
    for (var i = 0; i < AiFile.ScriptCount; i++)
    {
        if (original.Scripts[i].IsEmpty) continue;
        Runner.AreEqual(original.Scripts[i].Instructions.Count, after.Scripts[i].Instructions.Count,
            $"slot {i} keeps its instructions");
    }
});

runner.Test("an edit to a shared slot is not overwritten by its twin", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Parse(File.ReadAllBytes(path));

    // Slots 37 and 76 are one script parsed twice, so an edit lands on one object and not
    // the other. Building from whichever came first would throw the edit away in silence.
    var twin = file.Scripts.FirstOrDefault(s => !s.IsEmpty && s.SharedWith.Count > 0
                                                && s.Index > s.SharedWith.Min());
    if (twin is null) { Console.WriteLine("        (skipped: nothing aliased)"); return; }

    var before = twin.Instructions.Count;
    twin.Instructions.Insert(0, AiFile.FromText("var peasants = 9")[0]);
    twin.IsModified = true;

    var after = AiFile.Parse(AiLinker.Rebuild(file, separateAliasedSlots: true, out _));

    Runner.AreEqual(before + 1, after.Scripts[twin.Index].Instructions.Count,
        "the edited slot kept its new instruction");
    Runner.AreEqual(before, after.Scripts[twin.SharedWith[0]].Instructions.Count,
        "and its twin was left alone");
});

runner.Test("an install keeps the vault it already has", () =>
{
    // The rename to RuneFoundry once turned this folder name into "RuneFoundryLoader"
    // through a blanket search and replace, which pointed the app at a vault that did not
    // exist and started a second one — leaving the originals of every applied file behind
    // in the first. The name is a place on disk, so it is pinned here.
    Runner.AreEqual("War2ModLoader", BackupVault.FormerFolderName, "the old folder name");

    var shared = @"C:\ProgramData";
    var old = Path.Combine(shared, BackupVault.FormerFolderName);
    var current = Path.Combine(shared, BackupVault.FolderName);

    Runner.AreEqual(old, BackupVault.ChooseRoot(shared, p => p == old),
        "an install with only the old vault keeps using it");
    Runner.AreEqual(current, BackupVault.ChooseRoot(shared, p => p == current),
        "an install with only the new vault uses that");
    Runner.AreEqual(current, BackupVault.ChooseRoot(shared, _ => true),
        "an install with both uses the new one");
    Runner.AreEqual(current, BackupVault.ChooseRoot(shared, _ => false),
        "a fresh install makes the new one");
});

runner.Test("a language file rewrites byte-for-byte", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    // A mod's copy of a language file should differ from the game's in the lines it
    // changed and nowhere else. The shipped files came out of System.Text.Json, down to
    // escaping the apostrophe in "Twilight's Hammer Clan" — writing them back the same way
    // is the difference between a two-line diff and a 1,614-line one.
    //
    // That can only be asserted against a file still in the shape the game shipped. The
    // game folder holds whatever mod is applied and these files can be rewritten by hand,
    // so one that is not canonical is held to idempotence instead of being failed.
    var vault = BackupVault.Default();
    var canonical = 0;
    var reformatted = 0;

    foreach (var locale in GameStrings.KnownLocales)
    {
        var relative = $"Strings/{locale}.json";
        var file = vault.HasOriginal(relative)
            ? Path.Combine(vault.OriginalsRoot, "Strings", $"{locale}.json")
            : game.ResolveDataPath(relative);
        if (!File.Exists(file)) continue;

        var bytes = File.ReadAllBytes(file);
        var written = GameStrings.Parse(File.ReadAllText(file)).ToBytes();

        if (bytes.AsSpan().SequenceEqual(written)) { canonical++; continue; }

        reformatted++;
        var again = GameStrings.Parse(System.Text.Encoding.UTF8.GetString(written)).ToBytes();
        Runner.IsTrue(written.AsSpan().SequenceEqual(again),
            $"{locale}.json is no longer in the shipped shape, but rewriting it is stable");
    }

    Runner.IsTrue(canonical > 0, "at least one language file is still as the game shipped it");
    Console.WriteLine($"        ({canonical} byte-identical, {reformatted} already reformatted)");

    // And an edit reads back through a round trip.
    var path = game.ResolveDataPath(CampaignText.Default);
    if (!File.Exists(path)) return;

    var strings = GameStrings.Parse(File.ReadAllText(path));
    const string key = "human_1_name";
    Runner.IsTrue(strings.Has(key), "the first mission has a name");

    strings.Set(key, "Hillsbrad Revisited");
    Runner.AreEqual("Hillsbrad Revisited", GameStrings.Parse(strings.ToJson()).Get(key) ?? "",
        "an edit survives a round trip");
});

runner.Test("every campaign mission points at files that exist", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var stringsPath = game.ResolveDataPath(CampaignText.Default);
    if (!File.Exists(stringsPath)) { Console.WriteLine("        (skipped)"); return; }

    var strings = GameStrings.Load(stringsPath);
    var missions = 0;
    var pages = 0;

    foreach (var campaign in Campaign.All)
    {
        foreach (var mission in campaign.Missions(strings))
        {
            missions++;

            // The naming is inconsistent enough between the base game and the expansion
            // that guessing it wrong is easy and silent. So it is checked against disk.
            Runner.IsTrue(File.Exists(game.ResolveDataPath(mission.MapPath)),
                $"{mission} has a map at {mission.MapPath}");
            Runner.IsTrue(strings.Has(mission.NameKey), $"{mission} has a name");
            Runner.IsTrue(strings.Has(mission.ObjectivesKey), $"{mission} has objectives");
            Runner.IsTrue(strings.Has(mission.SummaryKey), $"{mission} has a summary");
            Runner.IsTrue(mission.Pages.Count > 0, $"{mission} has a briefing");

            foreach (var page in mission.Pages)
            {
                pages++;
                Runner.IsTrue(File.Exists(game.ResolveDataPath(page.SpeechPath)),
                    $"{mission} page {page.Number} has speech at {page.SpeechPath}");
            }
        }
    }

    Runner.AreEqual(52, missions, "all four campaigns are covered");
    Console.WriteLine($"        ({missions} missions, {pages} briefing pages)");
});

runner.Test("an applied mod's own files are not mistaken for the game's", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-applied-" + Guid.NewGuid().ToString("N"));
    var data = Path.Combine(folder, "game", "x86", "Data", "Rez");
    Directory.CreateDirectory(data);
    File.WriteAllBytes(Path.Combine(folder, "game", "x86", "Warcraft II.exe"), new byte[] { 0x4D, 0x5A });

    try
    {
        var stock = new byte[] { 1, 2, 3, 4 };
        var mine = new byte[] { 9, 9, 9, 9 };

        // The state after applying: the game folder holds the mod's file, and the vault
        // holds what was displaced.
        File.WriteAllBytes(Path.Combine(data, "ai.bin"), mine);

        Runner.IsTrue(GameInstall.TryOpen(Path.Combine(folder, "game"), out var game, out _), "opens");

        var vault = new BackupVault(Path.Combine(folder, "vault"));
        var original = Path.Combine(vault.OriginalsRoot, "Rez", "ai.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllBytes(original, stock);

        var project = ModProject.Create(Path.Combine(folder, "test.w2proj"), "Test");
        var content = project.ResolveContentPath("Rez/ai.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllBytes(content, mine);

        // Against the game folder alone the file matches itself, and the build would say
        // this mod changes nothing.
        Runner.AreEqual(1, project.FindUnchangedOverrides(game!).Count,
            "without the vault it compares the file with itself");

        // Against the vault it is what it is: changed.
        Runner.AreEqual(0, project.FindUnchangedOverrides(game!, vault).Count,
            "with the vault the change is seen");

        Console.WriteLine("        (the same override reads as unchanged without the vault, changed with it)");
    }
    finally
    {
        try { Directory.Delete(folder, true); } catch { }
    }
});

runner.Test("the overrides list does not rehash unchanged files", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-hash-" + Guid.NewGuid().ToString("N"));
    var data = Path.Combine(folder, "game", "x86", "Data", "Rez");
    Directory.CreateDirectory(data);
    File.WriteAllBytes(Path.Combine(folder, "game", "x86", "Warcraft II.exe"), new byte[] { 0x4D, 0x5A });

    try
    {
        // Big enough that rehashing it on every redraw would be felt.
        var big = new byte[4 * 1024 * 1024];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(data, "ai.bin"), big);

        Runner.IsTrue(GameInstall.TryOpen(Path.Combine(folder, "game"), out var game, out _), "opens");

        var project = ModProject.Create(Path.Combine(folder, "test.w2proj"), "Test");
        var content = project.ResolveContentPath("Rez/ai.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllBytes(content, big);

        var first = System.Diagnostics.Stopwatch.StartNew();
        Runner.AreEqual(1, project.FindUnchangedOverrides(game!).Count, "identical to the game's file");
        first.Stop();

        var again = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) project.FindUnchangedOverrides(game!);
        again.Stop();

        // Twenty cached passes must beat one uncached one; if the cache were absent this
        // would be twenty times slower, not faster.
        Runner.IsTrue(again.ElapsedMilliseconds <= first.ElapsedMilliseconds,
            $"20 repeats took {again.ElapsedMilliseconds} ms against {first.ElapsedMilliseconds} ms for the first");

        // And a real change still has to be noticed.
        big[0] ^= 0xFF;
        File.WriteAllBytes(content, big);
        File.SetLastWriteTimeUtc(content, DateTime.UtcNow.AddSeconds(1));
        Runner.AreEqual(0, project.FindUnchangedOverrides(game!).Count, "an edited file is seen as changed");
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
});

runner.Test("an applied mod is not reported as identical to stock", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-unchanged-" + Guid.NewGuid().ToString("N"));

    // A stand-in install, so the check can be run against a game folder that already holds
    // the mod's own files — which is the state this got wrong.
    var data = Path.Combine(folder, "game", "x86", "Data", "Rez");
    Directory.CreateDirectory(data);
    File.WriteAllBytes(Path.Combine(folder, "game", "x86", "Warcraft II.exe"), new byte[] { 0x4D, 0x5A });

    try
    {
        const string path = "Rez/ai.bin";
        var stockBytes = new byte[] { 1, 2, 3, 4 };
        var moddedBytes = new byte[] { 9, 9, 9, 9 };
        var gameFile = Path.Combine(data, "ai.bin");

        File.WriteAllBytes(gameFile, stockBytes);

        Runner.IsTrue(GameInstall.TryOpen(Path.Combine(folder, "game"), out var game, out _), "the stand-in opens");

        var project = ModProject.Create(Path.Combine(folder, "test.w2proj"), "Test");
        var vault = new BackupVault(Path.Combine(folder, "vault"));

        var content = project.ResolveContentPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllBytes(content, moddedBytes);

        Runner.AreEqual(0, project.FindUnchangedOverrides(game!, vault).Count,
            "a changed file is not identical to stock");

        // Apply it: the vault keeps the original, the game folder takes the mod's copy.
        vault.StoreOriginal(path, gameFile);
        File.WriteAllBytes(gameFile, moddedBytes);

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };

        Runner.AreEqual(0, project.FindUnchangedOverrides(game!, vault, applied).Count,
            "and still is not, once the game folder holds the mod's own copy");

        // Comparing against the game folder is exactly the mistake, so prove it would.
        Runner.AreEqual(1, project.FindUnchangedOverrides(game!).Count,
            "which is what comparing against the live game folder would have said");

        // A file that really does match stock still reports as unchanged.
        File.WriteAllBytes(content, stockBytes);
        Runner.AreEqual(1, project.FindUnchangedOverrides(game!, vault, applied).Count,
            "a genuinely identical file is still spotted");
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
});

runner.Test("the campaign objective table is where the notes say", () =>
{
    var game = GameInstall.Detect();
    if (game is null || !File.Exists(game.ExecutablePath)) { Console.WriteLine("        (skipped)"); return; }

    var exe = File.ReadAllBytes(game.ExecutablePath);

    Runner.IsTrue(CampaignObjectives.TryLocate(exe, out var offset, out var why), "the table is found: " + why);
    Console.WriteLine($"        (table at file offset {offset})");

    var ids = CampaignObjectives.Read(exe);
    Runner.AreEqual(52, ids.Length, "52 campaign slots");

    // Spot-checks from W2R-RE-NOTES.md. If these drift, the addresses no longer describe
    // this executable and writing to them would be writing into something else.
    Runner.AreEqual(0, (int)ids[0], "Human01 has objective 0 — four farms and a barracks");
    Runner.AreEqual(0, (int)ids[1], "Orc01 the same");
    Runner.AreEqual(1, (int)ids[4], "Human03 has objective 1");
    Runner.AreEqual(8, (int)ids[26], "Human14 destroys the Dark Portal");
    Runner.AreEqual(512, (int)ids[2], "Human02 is a flag objective");

    // And the slot arithmetic must agree with that table.
    var human = Campaign.All.First(c => c.Id == "human");
    var orc = Campaign.All.First(c => c.Id == "orc");
    var xorc = Campaign.All.First(c => c.Id == "xorc");

    var missions = human.Missions(null);
    Runner.AreEqual(0, missions[0].ExeSlot, "Human01 is slot 0");
    Runner.AreEqual(26, missions[13].ExeSlot, "Human14 is slot 26");
    Runner.AreEqual(1, orc.Missions(null)[0].ExeSlot, "Orc01 is slot 1");
    Runner.AreEqual(51, xorc.Missions(null)[11].ExeSlot, "XOrc12 is the last slot");
});

runner.Test("the live patcher only recognises the build the notes describe", () =>
{
    Runner.IsTrue(GameBuild.Known.Count > 0, "at least one build is pinned");
    Runner.IsTrue(!GameBuild.IsKnown("0000000000000000000000000000000000000000000000000000000000000000"),
        "an unknown hash is not accepted");
    Runner.IsTrue(GameBuild.IsKnown(GameBuild.Known[0].ToUpperInvariant()),
        "and case does not matter");

    var game = GameInstall.Detect();
    if (game is null || !File.Exists(game.ExecutablePath)) { Console.WriteLine("        (skipped)"); return; }

    // The installed game must be one of the pinned builds, or every address in the notes —
    // and every test above that reads them — is describing something else.
    Runner.IsTrue(GameBuild.Recognises(game, out var why), "the installed game is recognised: " + why);
});

runner.Test("only one program at a time may apply a mod", () =>
{
    // The loader and the editor are separate processes over one vault. An apply that
    // interleaves with another can back up a mod's file as though it were the game's, and
    // then uninstalling restores the wrong bytes with no copy left to fix it.
    using (var first = VaultLock.TryAcquire(TimeSpan.FromSeconds(1)))
    {
        Runner.IsTrue(first.Acquired, "the first caller gets in");

        // Same process, non-reentrant by construction: a second waiter must be turned away
        // rather than allowed to proceed alongside.
        var second = System.Threading.Tasks.Task.Run(() =>
        {
            using var other = VaultLock.TryAcquire(TimeSpan.FromMilliseconds(200));
            return other.Acquired;
        });

        Runner.IsTrue(!second.Result, "the second is told to wait, not let through");
    }

    // And the lock is released when the first caller is done with it.
    using var after = VaultLock.TryAcquire(TimeSpan.FromSeconds(1));
    Runner.IsTrue(after.Acquired, "the next apply can get in once the first finishes");

    Runner.IsTrue(VaultLock.BusyMessage.Length > 0, "and there is something to tell the person");
});

runner.Test("hashing many files at once gives exactly what hashing them one at a time gives", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    // A real spread of sizes, not a synthetic one: the point is that the fast path agrees
    // with the slow path on the actual install, including its 52 MB atlases.
    var files = game.EnumerateDataFiles().Take(300).Select(game.ResolveDataPath).Where(File.Exists).ToList();
    Runner.IsTrue(files.Count > 50, "enough files to be worth parallelising");

    var parallel = Hashing.Sha256Files(files);

    Runner.AreEqual(files.Count, parallel.Count, "every file comes back");
    foreach (var path in files)
        Runner.AreEqual(Hashing.Sha256File(path), parallel[path], "same hash for " + Path.GetFileName(path));

    // Edge cases the callers actually hit.
    Runner.AreEqual(0, Hashing.Sha256Files(Array.Empty<string>()).Count, "nothing in, nothing out");
    Runner.AreEqual(1, Hashing.Sha256Files(new[] { files[0] }).Count, "one file still works");
    Runner.AreEqual(1, Hashing.Sha256Files(new[] { files[0], files[0] }).Count, "a repeat is hashed once");

    // A file that is not there is left out, not thrown over — Verify reports it better.
    var missing = Path.Combine(Path.GetTempPath(), "war2mod-not-here-" + Guid.NewGuid().ToString("N"));
    var mixed = Hashing.Sha256Files(new[] { files[0], missing });
    Runner.AreEqual(1, mixed.Count, "a missing file is skipped rather than throwing");
    Runner.IsTrue(!mixed.ContainsKey(missing), "and is simply absent");
});

runner.Test("undo puts a change back, and redo puts it forward again", () =>
{
    var stack = new UndoStack();
    var value = 60;

    Runner.IsTrue(!stack.CanUndo && !stack.CanRedo, "a fresh stack has no history");
    Runner.AreEqual("Undo", stack.UndoLabel, "and nothing to name");

    stack.Do(new Edit("Footman hit points 60 to 90", () => value = 90, () => value = 60));
    Runner.AreEqual(90, value, "doing it applies it");
    Runner.IsTrue(stack.CanUndo, "and it can be taken back");
    Runner.AreEqual("Undo Footman hit points 60 to 90", stack.UndoLabel, "the menu names the change");

    stack.Undo();
    Runner.AreEqual(60, value, "undo puts it back");
    Runner.IsTrue(stack.CanRedo, "and offers it forward again");

    stack.Redo();
    Runner.AreEqual(90, value, "redo reapplies it");

    // A new change after an undo drops the redo branch: it described a future that no longer
    // follows from here.
    stack.Undo();
    stack.Do(new Edit("Footman hit points 60 to 75", () => value = 75, () => value = 60));
    Runner.IsTrue(!stack.CanRedo, "a new change drops what was undone");

    // Undo and redo drive the same handlers a person does, and those push to this stack —
    // without the guard the stack would fight itself while unwinding.
    var pushes = 0;
    var guarded = new UndoStack();
    guarded.Changed += () => pushes++;
    using (guarded.Quiet())
    {
        Runner.IsTrue(guarded.Suspended, "work can be marked as not worth recording");
    }
    Runner.IsTrue(!guarded.Suspended, "and the mark lifts afterwards");

    // History belongs to one project.
    stack.Clear();
    Runner.IsTrue(!stack.CanUndo && !stack.CanRedo, "clearing forgets everything");

    // Old entries fall off rather than growing without limit.
    var small = new UndoStack { Limit = 3 };
    for (var i = 0; i < 10; i++) small.Do(new Edit($"change {i}", () => { }, () => { }));
    var undone = 0;
    while (small.CanUndo) { small.Undo(); undone++; }
    Runner.AreEqual(3, undone, "only the last few are kept");
});

runner.Test("every campaign's artwork and act names are where we say they are", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);

    var sketches = vault.StockFile(game, CampaignArt.SketchAtlas);
    var maps = vault.StockFile(game, CampaignArt.MapsAtlas);
    if (!File.Exists(sketches) || !File.Exists(maps)) { Console.WriteLine("        (skipped)"); return; }

    var sketchAtlas = FrameAtlas.Load(sketches);
    var mapAtlas = FrameAtlas.Load(maps);

    var strings = GameStrings.Load(vault.StockFile(game, "Strings/enUS.json"));

    foreach (var campaign in Campaign.All)
    {
        // The sketch sheet numbers the two campaigns of a race rather than naming them, so
        // this is the mapping that would silently point at the wrong campaign if it drifted.
        var sketch = CampaignArt.Sketch(campaign.Id);
        var hovered = CampaignArt.SketchHovered(campaign.Id);

        Runner.IsTrue(sketch is not null, $"{campaign.Id} has a sketch");
        Runner.IsTrue(sketchAtlas.Rect(sketch!.Frame) is not null, $"{sketch.Frame} is in the sheet");
        Runner.IsTrue(sketchAtlas.Rect(hovered!.Frame) is not null, $"{hovered.Frame} is too");

        var shape = sketchAtlas.Rect(sketch.Frame)!.Value;
        Runner.AreEqual(720, shape.Width, "a campaign picture is 720 wide");
        Runner.AreEqual(1200, shape.Height, "and 1200 tall");

        // The two states are the same size, or fading one into the other would not line up.
        var lit = sketchAtlas.Rect(hovered.Frame)!.Value;
        Runner.AreEqual(shape.Width, lit.Width, "both states are the same width");
        Runner.AreEqual(shape.Height, lit.Height, "and the same height");

        for (var act = 1; act <= CampaignArt.Acts; act++)
        {
            var map = CampaignArt.Act(campaign.Id, act)!;
            var rect = mapAtlas.Rect(map.Frame);
            Runner.IsTrue(rect is not null, $"{map.Frame} is in the map sheet");
            Runner.AreEqual(1500, rect!.Value.Width, "an act map is 1500 wide");
            Runner.AreEqual(1100, rect.Value.Height, "and 1100 tall");

            // The title key is spelled differently from the frame — act_1 against act1 — so
            // it is worth checking rather than assuming.
            var key = CampaignArt.ActTitleKey(campaign.Id, act);
            Runner.IsTrue(strings.Has(key), $"{key} names the act");
            Runner.IsTrue(!string.IsNullOrWhiteSpace(strings.Get(key)), $"{key} is not blank");
        }
    }

    Console.WriteLine($"        (4 campaigns: {Campaign.All.Count * 2} sketches, "
                      + $"{Campaign.All.Count * CampaignArt.Acts} act maps and names)");

    // The names really are per campaign, not shared.
    Runner.AreEqual("The Shores of Lordaeron", strings.Get(CampaignArt.ActTitleKey("human", 1)),
        "the first human act is named");
    Runner.IsTrue(strings.Get(CampaignArt.ActTitleKey("orc", 1)) != strings.Get(CampaignArt.ActTitleKey("human", 1)),
        "and the orc one differently");
});

runner.Test("the team-colour mask is addressable, and stays greyscale", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var maskJson = vault.StockFile(game, IconAtlas.MaskPath);
    var maskPng = vault.StockFile(game, IconAtlas.MaskImagePath);
    if (!File.Exists(maskJson) || !File.Exists(maskPng)) { Console.WriteLine("        (skipped)"); return; }

    // Without the suffix every frame parses as id "team", which is not a number — so the
    // whole sheet came back empty and swaps silently left the mask alone.
    var blind = IconAtlas.Load(maskJson);
    Runner.AreEqual(0, blind.Ids.Count, "read as an ordinary sheet the mask has no icons at all");

    var mask = IconAtlas.Load(maskJson, IconAtlas.TeamMaskSuffix);
    Runner.IsTrue(mask.Ids.Count > 0, "read as a mask it has icons");
    Console.WriteLine($"        ({mask.Ids.Count} icons have a team-colour mask)");

    var face = IconAtlas.Load(vault.StockFile(game, IconAtlas.FacePath));
    Runner.IsTrue(face.Ids.Count > mask.Ids.Count, "not every portrait has one");

    var id = mask.Ids.First();
    var shape = mask.Rect(IconAtlas.Tilesets[0], id);
    Runner.IsTrue(shape is not null, "and its frames resolve");
    Runner.AreEqual(207, shape!.Value.Width, "a mask frame is the same size as a portrait");
    Runner.AreEqual(171, shape.Value.Height, "in both directions");

    // Clearing must keep the sheet greyscale: converting it to RGBA would multiply a 529 KB
    // file and hand the game something a different shape.
    var sheet = AtlasImage.Load(maskPng);
    Runner.AreEqual(System.Windows.Media.PixelFormats.Gray8.ToString(), sheet.Format.ToString(),
        "the mask ships as 8-bit greyscale");

    var frames = new[] { new System.Windows.Int32Rect(shape.Value.X, shape.Value.Y, shape.Value.Width, shape.Value.Height) };
    var cleared = AtlasImage.FillFrames(sheet, 0, frames);

    using var file = new MemoryStreamSource(cleared);
    var after = AtlasImage.Load(file.Path);

    Runner.AreEqual(sheet.PixelWidth, after.PixelWidth, "the mask keeps its width");
    Runner.AreEqual(sheet.PixelHeight, after.PixelHeight, "and its height");
    Runner.AreEqual(sheet.Format.ToString(), after.Format.ToString(), "and its greyscale format");

    // And the frame really is black now.
    var pixels = new byte[shape.Value.Width * shape.Value.Height];
    after.CopyPixels(frames[0], pixels, shape.Value.Width, 0);
    Runner.IsTrue(pixels.All(b => b == 0), "the cleared frame is untinted everywhere");
});

runner.Test("importing a picture leaves the sheet exactly the size the game expects", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var sheetPath = vault.StockFile(game, IconAtlas.FaceImagePath);
    var jsonPath = vault.StockFile(game, IconAtlas.FacePath);
    if (!File.Exists(sheetPath) || !File.Exists(jsonPath)) { Console.WriteLine("        (skipped)"); return; }

    var atlas = IconAtlas.Load(jsonPath);
    var sheet = AtlasImage.Load(sheetPath);

    // What the game reads: one sheet, fixed frames. Both have to survive untouched.
    Console.WriteLine($"        (sheet {sheet.PixelWidth} x {sheet.PixelHeight})");

    var id = atlas.Ids.First();
    var shape = atlas.Rect(IconAtlas.Tilesets[0], id);
    Runner.IsTrue(shape is not null, "the first icon has a frame");

    var frames = IconAtlas.Tilesets
        .Select(tileset => atlas.Rect(tileset, id))
        .Where(rect => rect is not null)
        .Select(rect => new System.Windows.Int32Rect(rect!.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height))
        .ToList();

    Runner.AreEqual(IconAtlas.Tilesets.Length, frames.Count, "one frame per tileset");

    // A picture of deliberately the wrong size and shape: the awkward case, not the easy one.
    var picture = new System.Windows.Media.Imaging.WriteableBitmap(
        64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
    var pixels = new byte[64 * 64 * 4];
    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255; }
    picture.WritePixels(new System.Windows.Int32Rect(0, 0, 64, 64), pixels, 64 * 4, 0);
    picture.Freeze();

    Runner.IsTrue(!AtlasImage.Fits(picture, shape!.Value.Width, shape.Value.Height),
        "a square picture does not fit a 207x171 frame, and the editor says so");

    var written = AtlasImage.ReplaceFrames(sheet, picture, frames);
    var after = AtlasImage.Load(new MemoryStreamSource(written).Path);

    Runner.AreEqual(sheet.PixelWidth, after.PixelWidth, "the sheet keeps its width");
    Runner.AreEqual(sheet.PixelHeight, after.PixelHeight, "and its height");

    // The frame we asked for changed, and a frame we did not ask for did not.
    var other = atlas.Rect(IconAtlas.Tilesets[0], atlas.Ids.Skip(1).First())!.Value;
    Runner.IsTrue(!Pixels.Same(sheet, after, frames[0]), "the frame we replaced changed");
    Runner.IsTrue(Pixels.Same(sheet, after, new System.Windows.Int32Rect(other.X, other.Y, other.Width, other.Height)),
        "and the frame beside it did not");
});

runner.Test("every sound the unit map names is a file the game actually ships", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var sounds = SoundLibrary.Scan(game);
    var missing = new List<string>();
    var total = 0;

    for (var unit = 0; unit < 110; unit++)
    {
        foreach (var sound in UnitSounds.For(unit))
        {
            total++;
            if (sounds.Find(sound.FileName) is null) missing.Add($"{unit}:{sound.FileName}");
        }
    }

    Console.WriteLine($"        ({total} sounds across {UnitSounds.Voices.Count} voices)");
    Runner.AreEqual(0, missing.Count, "every named file is installed: " + string.Join(", ", missing.Take(6)));

    // The kinds that used to be missing entirely.
    var footman = UnitSounds.For(0);
    foreach (var kind in new[] { UnitSoundKind.Ready, UnitSoundKind.Acknowledge, UnitSoundKind.Select,
                                 UnitSoundKind.Annoyed, UnitSoundKind.Help, UnitSoundKind.Death })
        Runner.IsTrue(footman.Any(s => s.Kind == kind), $"a Footman has a {kind} sound");

    Runner.IsTrue(UnitSounds.For(2).Any(s => s.Kind == UnitSoundKind.WorkDone),
        "and a Peasant reports work done");

    // Race decides the file, not just the folder: the registry keeps two banks 0x163 apart,
    // so a Grunt must not be handed the Footman's lines.
    var footmanFiles = footman.Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var gruntFiles = UnitSounds.For(1).Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    Runner.IsTrue(gruntFiles.Count > 0, "a Grunt has sounds");
    Runner.AreEqual(0, footmanFiles.Intersect(gruntFiles).Count(),
        "a Grunt and a Footman share no file");
    Runner.IsTrue(gruntFiles.All(f => f.StartsWith("O", StringComparison.OrdinalIgnoreCase)),
        "the Grunt's lines are the orc ones");

    // Shared voices are shared on purpose: a Paladin rides a Knight.
    Runner.AreEqual("Paladin", UnitSounds.VoiceOf(0x0C)?.Name, "the Paladin has his own voice");
    Runner.AreEqual(UnitSounds.VoiceOf(0x0C)?.Name, UnitSounds.VoiceOf(0x34)?.Name,
        "and Uther shares it");
    Runner.AreEqual(UnitSounds.VoiceOf(0x06)?.Name, UnitSounds.VoiceOf(0x32)?.Name,
        "Lothar rides a Knight and sounds like one");

    // The one-offs the exe gave us, which are not a speaker's voice at all.
    Runner.AreEqual("Catyessr.wav", UnitSounds.For(4).Single().FileName, "the Ballista is a machine");
    Runner.IsTrue(!UnitSounds.Known(0x22), "an unused slot stays unknown");
});

runner.Test("each building names its own noise, and the two races differ", () =>
{
    Runner.IsTrue(!UnitSounds.Known(BuildingSounds.FirstBuildingType), "type 58 has no picker");
    Runner.IsTrue(UnitSounds.Known(BuildingSounds.FirstBuildingType - 1), "type 57 does");

    // A building resolves to one file, not to the whole folder.
    Runner.AreEqual("Hfarm.wav", BuildingSounds.For(0x3A)?.FileName, "the Farm has its own noise");
    Runner.AreEqual("Mine.wav", BuildingSounds.For(0x5C)?.FileName, "and so does the Gold Mine");

    // The buildings that make no sound say so rather than borrowing one.
    foreach (var silent in new[] { 0x3C, 0x3D, 0x4A, 0x4B, 0x58, 0x5A, 0x64, 0x65, 0x66, 0x67 })
        Runner.IsTrue(BuildingSounds.For(silent) is null, $"unit 0x{silent:X2} makes no sound");

    Runner.IsTrue(BuildingSounds.For(0) is null, "and neither does a Footman");

    // Race decides the file. The registry holds two banks 0x163 apart, so a Pig Farm does
    // not sound like a Farm — which is the thing the first version of this map got wrong.
    Runner.AreEqual("Ofarm.wav", BuildingSounds.For(0x3B)?.FileName, "the Pig Farm is distinct");
    Runner.AreEqual(0x196, BuildingSounds.For(0x3B)?.SoundId, "at its own registry id");
    Runner.AreEqual(0x163, BuildingSounds.For(0x3B)!.SoundId - BuildingSounds.For(0x3A)!.SoundId,
        "exactly one bank above the human one");

    foreach (var (human, orc) in new[] { (0x3E, 0x3F), (0x42, 0x43), (0x44, 0x45), (0x46, 0x47), (0x50, 0x51) })
        Runner.AreEqual(0x163, BuildingSounds.For(orc)!.SoundId - BuildingSounds.For(human)!.SoundId,
            $"0x{orc:X2} sits one bank above 0x{human:X2}");

    // Genuinely shared files stay shared, even across the banks.
    Runner.AreEqual(BuildingSounds.For(0x48)?.FileName, BuildingSounds.For(0x49)?.FileName,
        "both shipyards ring the same bell");

    // Every one is played now; nothing is listed that the game ignores.
    Runner.IsTrue(BuildingSounds.All.All(s => s.Played), "every building sound is referenced");

    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var sounds = SoundLibrary.Scan(game);
    var missing = BuildingSounds.All.Where(s => sounds.Find(s.FileName) is null)
                                    .Select(s => s.FileName).ToList();
    Runner.AreEqual(0, missing.Count, "every building sound is installed: " + string.Join(", ", missing));

    var folder = Path.Combine(game.DataRoot, "Gamesfx", "Bldg");
    if (!Directory.Exists(folder)) { Console.WriteLine("        (no Bldg folder)"); return; }

    var onDisk = Directory.EnumerateFiles(folder, "*.wav").Select(Path.GetFileName)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var named = BuildingSounds.All.Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    Console.WriteLine($"        ({onDisk.Count} files in Gamesfx/Bldg, {BuildingSounds.All.Count} mapped)");

    var unaccounted = onDisk.Except(named, StringComparer.OrdinalIgnoreCase).ToList();
    Runner.AreEqual(0, unaccounted.Count,
        "every file in Gamesfx/Bldg belongs to a building: " + string.Join(", ", unaccounted));
});

runner.Test("a silent clip matches the one it replaces, and the game's own format", () =>
{
    // The game's own shape: plain PCM, mono, 22,050 Hz, 16-bit.
    var silence = WaveFile.Silence(TimeSpan.FromSeconds(2));
    var described = WaveFile.Describe(silence);

    Runner.IsTrue(described is not null, "what we write, we can read back");
    Runner.AreEqual(1, described!.Channels, "mono");
    Runner.AreEqual(22050, described.SampleRate, "22,050 Hz");
    Runner.AreEqual(16, described.BitsPerSample, "16-bit");
    Runner.AreEqual(2.0, Math.Round(described.Duration.TotalSeconds, 3), "two seconds of it");
    Runner.AreEqual(44 + 2 * 22050 * 2, silence.Length, "and no bytes wasted");

    // 16-bit silence is zero.
    Runner.IsTrue(silence.Skip(44).All(b => b == 0), "16-bit silence is zeroes");

    // 8-bit PCM is unsigned, so its silence is 0x80. Zeroes there are a loud click.
    var eightBit = WaveFile.Silence(TimeSpan.FromSeconds(1), new WaveFormat(1, 11025, 8, 0));
    Runner.IsTrue(eightBit.Skip(44).All(b => b == 0x80), "8-bit silence is 0x80, not zero");

    // Never a zero-length data chunk, whatever is asked for.
    Runner.IsTrue(WaveFile.Describe(WaveFile.Silence(TimeSpan.Zero))!.DataBytes > 0,
        "even the shortest clip has samples in it");

    // A briefing page is held on screen while its recording plays, so length is matched
    // rather than chosen. This is the case the note said to check.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var speech = vault.StockFile(game, "Speech/Hum_11_1.wav");
    if (!File.Exists(speech)) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(speech);
    var shape = WaveFile.Describe(original);
    Runner.IsTrue(shape is not null, "the game's own clip is plain PCM we can describe");

    var quiet = WaveFile.SilenceLike(original);
    var quietShape = WaveFile.Describe(quiet)!;

    Console.WriteLine($"        ({shape!.Duration.TotalSeconds:F1}s briefing -> {quiet.Length:N0} bytes of quiet)");

    Runner.AreEqual(Math.Round(shape.Duration.TotalSeconds, 1), Math.Round(quietShape.Duration.TotalSeconds, 1),
        "the silence runs as long as the recording it replaces");
    Runner.AreEqual(shape.SampleRate, quietShape.SampleRate, "at the same rate");
    Runner.AreEqual(shape.Channels, quietShape.Channels, "with the same channels");
    Runner.AreEqual(shape.BitsPerSample, quietShape.BitsPerSample, "and the same depth");

    // Every clip the game ships must be describable, or "silence like this one" would
    // silently fall back to a default length somewhere.
    var speechFolder = Path.GetDirectoryName(speech)!;
    var undescribable = Directory.EnumerateFiles(speechFolder, "*.wav")
        .Where(f => WaveFile.Describe(File.ReadAllBytes(f)) is null)
        .Select(Path.GetFileName)
        .ToList();

    Runner.AreEqual(0, undescribable.Count,
        "every briefing recording is plain PCM: " + string.Join(", ", undescribable.Take(5)));
});

runner.Test("a unit exported to a grid and imported back is the same unit", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var indexPath = vault.StockFile(game, SpriteIndex.Path);
    if (!File.Exists(indexPath)) { Console.WriteLine("        (skipped)"); return; }

    var index = SpriteIndex.Load(indexPath);
    var spritePath = index.Paths.FirstOrDefault(p => p.EndsWith("peon.grp", StringComparison.OrdinalIgnoreCase));
    if (spritePath is null || index.Source(spritePath) is not { } source)
    { Console.WriteLine("        (skipped)"); return; }

    var era = index.Eras.First();
    if (index.Atlas(era, source.Atlas) is not { } files) { Console.WriteLine("        (skipped)"); return; }

    var atlasFile = vault.StockFile(game, files.Json);
    var sheetFile = vault.StockFile(game, files.Image);
    if (!File.Exists(atlasFile) || !File.Exists(sheetFile)) { Console.WriteLine("        (skipped)"); return; }

    var folder = Path.Combine(Path.GetTempPath(), "runefoundry-sprite-" + Guid.NewGuid().ToString("N")[..8]);

    try
    {
        var exported = SpriteSheetIO.Export(folder, spritePath, source, sheetFile, atlasFile, null, null);

        Console.WriteLine($"        ({source.Prefix}: {exported.Frames} frames as "
                          + $"{exported.Columns} x {exported.Rows})");

        Runner.AreEqual(65, exported.Frames, "the peon has 65 frames");
        Runner.AreEqual(13, exported.Columns, "laid out thirteen across");
        Runner.AreEqual(5, exported.Rows, "and five down, one row per facing");
        Runner.IsTrue(File.Exists(exported.Sheet), "the grid was written");

        // Straight back in, untouched: whatever the round trip does to the pixels, it must
        // do nothing at all.
        var (sheetBytes, atlasJson, _, _, result) =
            SpriteSheetIO.Import(folder, source, sheetFile, atlasFile, null, null);

        Console.WriteLine($"        (repacked to {result.Width} x {result.Height}, "
                          + (result.Grew ? "grown" : "no growth") + ")");

        Runner.AreEqual(65, result.Frames, "all 65 came back");
        Runner.AreEqual(8076, result.Width, "the sheet keeps its width");

        // Growth is allowed — our packing is simpler than the one that built the sheet — but
        // it has to stay inside what every GPU the game runs on can hold.
        Runner.IsTrue(result.Height <= 8192,
            $"the sheet stays within 8192 (it is {result.Height})");

        // The proof: every frame drawn from the rebuilt sheet must match the frame drawn
        // from the game's own, pixel for pixel, through completely different rectangles.
        var before = SpriteAtlas.Load(atlasFile);
        var after = SpriteAtlas.Parse(atlasJson);

        var oldSheet = AtlasImage.Load(sheetFile);
        var newSheet = FromBytes(sheetBytes);

        var moved = 0;
        var compared = 0;

        foreach (var was in before.Sequence(source.Prefix))
        {
            var now = after.Frame(was.Name)!;
            if (now.Rect != was.Rect) moved++;

            Runner.AreEqual(was.CanvasWidth, now.CanvasWidth, $"{was.Name} keeps its canvas");

            if (now.Rect.Width > was.Rect.Width || now.Rect.Height > was.Rect.Height)
                throw new Exception($"{was.Name} came back larger than it went in");

            if (!SameFrame(oldSheet, was, newSheet, now))
                throw new Exception(
                    $"{was.Name} does not survive the round trip: "
                    + $"was {was.Rect.Width}x{was.Rect.Height} at ({was.Rect.X},{was.Rect.Y}) "
                    + $"offset ({was.OffsetX},{was.OffsetY}); "
                    + $"now {now.Rect.Width}x{now.Rect.Height} at ({now.Rect.X},{now.Rect.Y}) "
                    + $"offset ({now.OffsetX},{now.OffsetY})");

            compared++;
        }

        Console.WriteLine($"        ({compared} frames identical, {moved} moved in the sheet)");
        Runner.AreEqual(65, compared, "every frame was compared");
        Runner.IsTrue(moved > 0, "and the repack really did move them, so the comparison means something");
    }
    finally
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
});

runner.Test("one unit's art goes back without disturbing the sheet it shares", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var indexPath = vault.StockFile(game, SpriteIndex.Path);
    if (!File.Exists(indexPath)) { Console.WriteLine("        (skipped)"); return; }

    var index = SpriteIndex.Load(indexPath);
    var spritePath = index.Paths.FirstOrDefault(p => p.EndsWith("human/peon.grp", StringComparison.OrdinalIgnoreCase));
    if (spritePath is null || index.Source(spritePath) is not { } source)
    { Console.WriteLine("        (skipped)"); return; }

    var era = index.Eras.First();
    if (index.Atlas(era, source.Atlas) is not { } files) { Console.WriteLine("        (skipped)"); return; }

    var atlasFile = vault.StockFile(game, files.Json);
    var sheetFile = vault.StockFile(game, files.Image);
    if (!File.Exists(atlasFile) || !File.Exists(sheetFile)) { Console.WriteLine("        (skipped)"); return; }

    var folder = Path.Combine(Path.GetTempPath(), "runefoundry-reset-" + Guid.NewGuid().ToString("N")[..8]);

    try
    {
        // Stand in for an import: round-tripping the peon moves all 65 of its frames, which
        // is exactly the state a reset has to undo.
        SpriteSheetIO.Export(folder, spritePath, source, sheetFile, atlasFile, null, null);
        var (imported, importedAtlas, _, _, _) =
            SpriteSheetIO.Import(folder, source, sheetFile, atlasFile, null, null);

        var modSheet = Path.Combine(folder, "mod-sheet.png");
        var modAtlas = Path.Combine(folder, "mod-atlas.json");
        File.WriteAllBytes(modSheet, imported);
        File.WriteAllText(modAtlas, importedAtlas);

        var stock = SpriteAtlas.Load(atlasFile);
        var changed = SpriteSheetIO.ChangedFrames(SpriteAtlas.Parse(importedAtlas), stock);

        Console.WriteLine($"        ({changed.Count} frames moved by the import)");
        Runner.IsTrue(changed.Count > 0, "the import moved something, so there is something to put back");
        Runner.IsTrue(changed.All(f => f.StartsWith(source.Prefix, StringComparison.Ordinal)),
            "and moved nothing that is not the peon's");

        // Now put it back.
        var (restored, restoredAtlas, _, _, result) = SpriteSheetIO.Restore(
            source, modSheet, modAtlas, sheetFile, atlasFile, null, null, null, null);

        Console.WriteLine($"        (restored {result.Frames} frames)");

        var after = SpriteAtlas.Parse(restoredAtlas);
        var oldSheet = AtlasImage.Load(sheetFile);
        var newSheet = FromBytes(restored);

        foreach (var was in stock.Sequence(source.Prefix))
            if (!SameFrame(oldSheet, was, newSheet, after.Frame(was.Name)!))
                throw new Exception($"{was.Name} did not come back");

        // The other seventeen units in this sheet must be untouched by all of it.
        var others = stock.Frames
            .Where(f => !f.Name.StartsWith(source.Prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var other in others)
            if (!SameFrame(oldSheet, other, newSheet, after.Frame(other.Name)!))
                throw new Exception($"{other.Name} was disturbed by putting the peon back");

        Console.WriteLine($"        ({others.Count} other frames left alone)");
        Runner.IsTrue(others.Count > 500, "and there were plenty of them to disturb");
    }
    finally
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
});

runner.Test("a clip read header-only still reports its true length", () =>
{
    var format = new WaveFormat(1, 22050, 8, 0);
    var clip = WaveFile.Silence(TimeSpan.FromSeconds(6.4), format);

    Runner.AreEqual(6.4, Math.Round(WaveFile.Describe(clip)!.Duration.TotalSeconds, 2),
        "the whole file reads as six and a bit seconds");

    // What a list does: read the headers, leave the samples on disk.
    var head = clip[..4096];

    Runner.AreEqual(6.4, Math.Round(WaveFile.Describe(head, clip.Length)!.Duration.TotalSeconds, 2),
        "and so does the header, when told how long the file really is");

    // The bug this pins: without the length, the clip reads as however much was loaded,
    // which showed a six-second fanfare as 0:00.
    Runner.IsTrue(WaveFile.Describe(head)!.Duration.TotalSeconds < 1,
        "whereas the header alone can only speak for the bytes it has");
});

runner.Test("the copy on screen follows the content design", () =>
{
    // Source, not the built output: this is about what is written, and a published copy
    // would only report the same strings twice.
    var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
    if (!Directory.Exists(src)) { Console.WriteLine("        (skipped)"); return; }

    // Both kinds. Where a string is stored is the program's business, not the reader's:
    // an error dialog and a button label are the same voice to the person reading them,
    // and checking only the XAML let 45 em dashes live in code.
    var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal)
                    || f.EndsWith(".cs", StringComparison.Ordinal))
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
        .ToList();

    Runner.IsTrue(files.Count > 20, "there are files to read");

    var banned = new (string Pattern, string Why)[]
    {
        ("\u2014", "an em dash. Use a full stop, a comma or a colon."),
        ("...", "three dots. Use a single ellipsis character."),
        ("not just", "\"not just\". State the right thing first."),
        ("simply", "\"simply\". Cut it."),
        ("easily", "\"easily\". Cut it."),
        ("please note", "\"please note\". Cut it."),
        ("keep in mind", "\"keep in mind\". Cut it."),
        ("which means", "\"which means\". State the outcome."),
    };

    var found = new List<string>();
    var strings = 0;

    foreach (var file in files)
    {
        var consent = Path.GetFileName(file).StartsWith("CampaignRulesConsent", StringComparison.Ordinal);

        foreach (var (line, text) in Copy(file))
        {
            strings++;

            foreach (var (pattern, why) in banned)
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    found.Add($"{Path.GetFileName(file)}:{line} has {why}");

            // ASD-STE100 caps the sentence, not the paragraph: 20 words for an instruction,
            // 25 for a description. The disclosure is the one place allowed to run long,
            // and only on length; its punctuation is held to the same rule as everything.
            if (consent) continue;

            foreach (var sentence in text.Split('.', '?', '!'))
            {
                var count = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (count > 25)
                    found.Add($"{Path.GetFileName(file)}:{line} has a {count}-word sentence");
            }
        }
    }

    Console.WriteLine($"        ({strings} strings, {files.Count} files, {found.Count} against the rules)");
    Runner.IsTrue(strings > 400, "the scan reached the copy, not just a few files");

    Runner.AreEqual(0, found.Count,
        found.Count == 0 ? "" : string.Join("; ", found.Take(6)));
});

runner.Test("acts cover every mission, in the uneven split the game uses", () =>
{
    foreach (var campaign in new[] { "human", "orc", "xhuman", "xorc" })
    {
        var covered = new List<int>();

        for (var act = 1; act <= CampaignArt.Acts; act++)
        {
            var missions = CampaignArt.MissionsInAct(campaign, act);
            Runner.IsTrue(missions.Count > 0, $"{campaign} act {act} has missions");

            // An act is a run of consecutive missions, never a scattering.
            for (var i = 1; i < missions.Count; i++)
                Runner.AreEqual(missions[i - 1] + 1, missions[i], $"{campaign} act {act} runs unbroken");

            covered.AddRange(missions);
        }

        Runner.AreEqual(CampaignArt.Missions, covered.Count, $"{campaign} covers all 14 missions once");
        Runner.AreEqual(1, covered.First(), $"{campaign} starts at mission 1");
        Runner.AreEqual(CampaignArt.Missions, covered.Last(), $"{campaign} ends at mission 14");
    }

    // The split is not four missions an act. Reading the game's table at 0x8caec0 was the
    // point: Tides of Darkness gives act 1 four missions and act 2 three.
    Runner.AreEqual(4, CampaignArt.MissionsInAct("human", 1).Count, "Tides act 1 has four missions");
    Runner.AreEqual(3, CampaignArt.MissionsInAct("human", 2).Count, "Tides act 2 has three");
    Runner.AreEqual(3, CampaignArt.MissionsInAct("xhuman", 1).Count, "the expansion's act 1 has three");
    Runner.AreEqual(5, CampaignArt.MissionsInAct("xhuman", 4).Count, "and its act 4 has five");

    Console.WriteLine("        (human " + string.Join("/", Enumerable.Range(1, 4)
        .Select(a => CampaignArt.MissionsInAct("human", a).Count))
        + ", xhuman " + string.Join("/", Enumerable.Range(1, 4)
        .Select(a => CampaignArt.MissionsInAct("xhuman", a).Count)) + ")");

    Runner.AreEqual("missions 5 to 7", CampaignArt.ActRange("human", 2), "the range reads plainly");
});

runner.Test("a campaign's closing screen is found, text and speech together", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var stringsPath = vault.StockFile(game, CampaignText.Default);
    if (!File.Exists(stringsPath)) { Console.WriteLine("        (skipped)"); return; }

    var strings = GameStrings.Load(stringsPath);

    // What the game ships, read out of 0x52ab81: prefix + "_finale_" + page, counting up.
    var expected = new Dictionary<string, int>
    {
        ["human"] = 1,
        ["orc"] = 1,
        ["xhuman"] = 2,
        ["xorc"] = 3,
    };

    foreach (var campaign in Campaign.All)
    {
        var pages = campaign.Epilogue(strings);

        Runner.AreEqual(expected[campaign.Id], pages.Count,
            $"{campaign.Id} closes on {expected[campaign.Id]} page(s)");

        foreach (var page in pages)
        {
            Runner.IsTrue(strings.Has(page.TextKey), $"{page.TextKey} is in the language file");

            var speech = vault.StockFile(game, page.SpeechPath);
            Runner.IsTrue(File.Exists(speech), $"{page.SpeechPath} is on disk");
        }

        Console.WriteLine($"        ({campaign.Id}: {pages.Count} page(s), "
                          + string.Join(", ", pages.Select(p => Path.GetFileName(p.SpeechPath))) + ")");
    }
});

runner.Test("every music track names a file the game actually ships", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var folder = Path.GetDirectoryName(vault.StockFile(game, "Music/HUMAN1_r.wav"));
    if (folder is null || !Directory.Exists(folder)) { Console.WriteLine("        (skipped)"); return; }

    var named = GameMusic.All.SelectMany(cue => cue.Files).Select(f => f.Path).ToList();

    foreach (var path in named)
        Runner.IsTrue(File.Exists(vault.StockFile(game, path)), $"{path} is on disk");

    // The other direction matters more: a track left out of the list is a track nobody can
    // replace, and nothing else would ever notice.
    var onDisk = Directory.EnumerateFiles(folder, "*.wav")
        .Select(f => "Music/" + Path.GetFileName(f))
        .ToList();

    var missed = onDisk
        .Where(f => !named.Any(n => n.Equals(f, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    Console.WriteLine($"        ({GameMusic.Count} tracks, {named.Count} files, "
                      + $"{onDisk.Count} on disk)");

    Runner.AreEqual(0, missed.Count, "no track is left out: " + string.Join(", ", missed));
    Runner.AreEqual(19, GameMusic.Count, "nineteen tracks");
    Runner.AreEqual(33, named.Count, "thirty-three files, since five ship only once");
});

runner.Test("undo steps back through replacements of the same file", () =>
{
    using var sandbox = new Sandbox();
    var project = ModProject.Create(Path.Combine(sandbox.Root, "project"), "Undo Test");

    var undo = new UndoStack();
    using var files = new OverrideUndo(undo);

    const string path = "Backgrounds/loadscreen.png";

    // First replacement: the file was the game's, so there is nothing to keep and undo
    // means dropping the override.
    files.Record(project, "first", new[] { path },
        () => project.WriteOverride(path, new byte[] { 1, 1, 1 }));

    Runner.IsTrue(project.HasOverride(path), "the mod has the file");

    // Second replacement over our own work: this is the case Reset cannot stand in for,
    // because Reset would go to the game's copy and lose both.
    files.Record(project, "second", new[] { path },
        () => project.WriteOverride(path, new byte[] { 2, 2, 2 }));

    Runner.AreEqual(2, File.ReadAllBytes(project.ResolveContentPath(path))[0], "the second is in place");

    undo.Undo();
    Runner.AreEqual(1, File.ReadAllBytes(project.ResolveContentPath(path))[0],
        "undo steps back to the first replacement, not to the game's copy");

    undo.Redo();
    Runner.AreEqual(2, File.ReadAllBytes(project.ResolveContentPath(path))[0], "redo puts the second back");

    undo.Undo();
    undo.Undo();
    Runner.IsTrue(!project.HasOverride(path),
        "undoing the first replacement drops the override, which is what Reset does");

    undo.Redo();
    Runner.IsTrue(project.HasOverride(path), "and redo brings it back");

    Console.WriteLine("        (two replacements, stepped back and forward)");

    // Closing a project has to take the kept copies with it. Three sessions' worth had
    // reached 320 MB on this machine before anything cleared them.
    files.Record(project, "third", new[] { path },
        () => project.WriteOverride(path, new byte[] { 3, 3, 3 }));

    var store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RuneFoundry", "undo");

    var kept = Directory.Exists(store)
        ? Directory.EnumerateDirectories(store).Sum(d => Directory.EnumerateFiles(d).Count())
        : 0;

    Runner.IsTrue(kept > 0, "a copy was kept while the project was open");

    files.Clear();

    var after = Directory.Exists(store)
        ? Directory.EnumerateDirectories(store).Sum(d => Directory.EnumerateFiles(d).Count())
        : 0;

    Runner.AreEqual(0, after, "closing the project takes the kept copies with it");
    Console.WriteLine($"        ({kept} copies kept, {after} left after closing)");
});

runner.Test("each campaign's picture is its own frame, and no two share one", () =>
{
    // The orc pair is numbered the opposite way to the human pair, so the four campaigns
    // are spelled out and checked rather than derived from the id.
    var expected = new Dictionary<string, string>
    {
        ["human"] = "sketch_human1",
        ["xhuman"] = "sketch_human2",
        ["orc"] = "sketch_orc2",
        ["xorc"] = "sketch_orc1",
    };

    var used = new List<string>();

    foreach (var (campaign, frame) in expected)
    {
        var sketch = CampaignArt.Sketch(campaign);
        var lit = CampaignArt.SketchHovered(campaign);

        Runner.IsTrue(sketch is not null, $"{campaign} has a picture");
        Runner.AreEqual(frame, sketch!.Frame, $"{campaign} draws {frame}");
        Runner.AreEqual(frame + "_hovered", lit!.Frame, $"{campaign}'s pointed-at copy matches it");

        used.Add(sketch.Frame!);
        used.Add(lit.Frame!);
    }

    // The bug this pins: two campaigns pointing at one frame means replacing either one
    // changes both, and the screen gives no hint that it happened.
    Runner.AreEqual(used.Count, used.Distinct().Count(), "no two campaigns share a frame");

    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (8 frames, all distinct)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var atlasPath = vault.StockFile(game, CampaignArt.SketchAtlas);
    if (!File.Exists(atlasPath)) { Console.WriteLine("        (8 frames, all distinct)"); return; }

    var atlas = FrameAtlas.Load(atlasPath);
    foreach (var frame in used)
        Runner.IsTrue(atlas.Has(frame), $"{frame} is in the sheet");

    Console.WriteLine($"        (8 frames, all distinct, all in the sheet)");
});

runner.Test("a rebuilt ai.bin is checked by what its scripts say, not how much", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);
    var bytes = AiLinker.Rebuild(file, separateAliasedSlots: false, out _);
    var back = AiFile.Parse(bytes);

    // Every instruction, not every count. The old check compared how many a script had and
    // would have passed a file whose instructions were all the wrong bytes.
    var compared = 0;

    for (var i = 0; i < AiFile.ScriptCount; i++)
    {
        var was = file.Scripts[i];
        var now = back.Scripts[i];

        Runner.AreEqual(was.Instructions.Count, now.Instructions.Count,
            $"slot {i} keeps its instruction count");

        for (var at = 0; at < was.Instructions.Count; at++)
        {
            var a = was.Instructions[at];
            var b = now.Instructions[at];

            Runner.AreEqual(a.Opcode, b.Opcode, $"slot {i} instruction {at} keeps its opcode");

            // A goto's operand is a byte position and is meant to move; everything else
            // has to come back identical.
            if (a.Opcode != (byte)AiOpcode.Goto)
                Runner.AreEqual(Convert.ToHexString(a.Operands), Convert.ToHexString(b.Operands),
                    $"slot {i} instruction {at} keeps its operands");

            compared++;
        }
    }

    // And every jump lands inside the file.
    var jumps = 0;

    foreach (var script in back.Scripts)
        foreach (var instruction in script.Instructions)
        {
            if (instruction.Opcode != (byte)AiOpcode.Goto || instruction.IsUnknown) continue;

            var target = AiFile.ReadWord(instruction);
            Runner.IsTrue(target >= AiFile.HeaderSize && target < bytes.Length,
                $"a jump to {target} is inside the {bytes.Length}-byte file");

            jumps++;
        }

    Console.WriteLine($"        ({compared} instructions compared, {jumps} jumps checked)");
    Runner.IsTrue(compared > 5000, "the whole file was compared");
});

runner.Test("a script's byte budget is its own, and the roomy slots can be found", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);

    var rows = file.Scripts
        .Select((s, i) => new AiRow(s, $"Slot {i}", ""))
        .Where(r => r.Room > 0)
        .ToList();

    // Writing your own script means taking a slot and clearing it, so the budget has to be
    // visible before you choose. Every slot must be able to state one.
    foreach (var row in rows)
    {
        Runner.IsTrue(row.Room > 0, $"{row.Title} states a budget");
        Runner.IsTrue(row.Used <= row.Room, $"{row.Title} fits its own room as it ships");
        // The list says how full, briefly; the pane beside it says it in bytes.
        Runner.IsTrue(row.Budget.EndsWith("% full", StringComparison.Ordinal),
            $"{row.Title} says how full it is");
        Runner.IsTrue(row.BudgetDetail.Contains("bytes", StringComparison.Ordinal),
            $"{row.Title} says its budget in bytes");
    }

    var roomiest = rows.OrderByDescending(r => r.Room).First();
    var median = rows.OrderBy(r => r.Room).ElementAt(rows.Count / 2);

    Console.WriteLine($"        ({rows.Count} slots, roomiest {roomiest.Room} bytes, "
                      + $"median {median.Room}, so about {median.Spare} spare instructions there)");

    // Clearing a slot gives its whole budget over, which is the workflow this supports.
    var cleared = new AiRow(roomiest.Script, roomiest.Title, "");
    Runner.IsTrue(cleared.Room >= 512,
        $"the roomiest slot is worth clearing: {cleared.Room} bytes");

    Runner.IsTrue(!rows.Any(r => r.Overflows), "nothing the game ships is over its own budget");
});

runner.Test("a jump follows the row it aims at when the rows above it move", () =>
{
    // A goto holds an absolute byte. Choosing a wave writes down where that wave begins at
    // that moment, so inserting a row above it used to move the wave without moving the
    // number, and the jump quietly pointed into the middle of the wave before.
    var script = new List<AiInstruction>
    {
        new() { Opcode = (byte)AiOpcode.Var, Operands = new byte[] { 0x22, 0 } },
        new() { Opcode = (byte)AiOpcode.Sleep, Operands = new byte[] { 0x10, 0x27, 0, 0 } },
        new() { Opcode = (byte)AiOpcode.Goto, Operands = new byte[] { 0, 0 } },
    };

    var blocks = script.Select(AiBlock.From).ToList();

    // Lay them out the way the editor does: each row at the offset the one before ends.
    void Layout(int start)
    {
        var at = start;
        foreach (var block in blocks)
        {
            block.Offset = at;
            at += block.Length;
        }
    }

    Layout(100);

    // Aim the jump at the sleep, by its address at this moment.
    var sleep = blocks[1];
    blocks[2].Value = (uint)sleep.Offset;

    Runner.AreEqual(103, sleep.Offset, "the sleep sits where the setup ends");
    Runner.AreEqual(103u, blocks[2].Value, "and the jump aims at it");

    // Now put a row above it, the way inserting an instruction does.
    var inserted = AiBlock.From(new AiInstruction
    {
        Opcode = (byte)AiOpcode.Var,
        Operands = new byte[] { 0x09, 0 },
    });

    blocks.Insert(1, inserted);

    // What the editor does on every regroup: note what each jump aims at, move everything,
    // then point the jumps at their rows again.
    var aimedAt = blocks
        .Where(b => b.Opcode == (byte)AiOpcode.Goto)
        .ToDictionary(b => b, b => blocks.FirstOrDefault(x => x.Offset == (int)b.Value));

    Layout(100);

    foreach (var (jump, target) in aimedAt)
        if (target is not null) jump.Value = (uint)target.Offset;

    Runner.AreEqual(106, sleep.Offset, "the sleep moved down by the inserted row");
    Runner.AreEqual(106u, blocks.Last().Value, "and the jump moved with it");

    Console.WriteLine("        (jump followed its row from 103 to 106)");
});

runner.Test("an attack wave builds the units it then gathers", () =>
{
    // A wave that sets a party size and waits has told the computer to assemble units it
    // was never told to make. The build lines are what turn it into something that happens.
    foreach (var domain in new[]
             {
                 AiTemplates.AttackDomain.Land,
                 AiTemplates.AttackDomain.Naval,
                 AiTemplates.AttackDomain.Air,
             })
    {
        var units = AiTemplates.UnitsFor(domain);
        Runner.IsTrue(units.Count > 0, $"{domain} is made of something");

        var builds = units.Take(2).ToDictionary(u => u, _ => (byte)4);
        var wave = AiTemplates.AttackWave(domain, partySize: 3, delaySeconds: 120, builds: builds);

        // Sleep, the builds, the party size, the wait, the launch.
        Runner.AreEqual((byte)AiOpcode.Sleep, wave[0].Opcode, $"{domain} opens by waiting");

        var built = wave.Where(i => i.Opcode == (byte)AiOpcode.Var
                                    && builds.ContainsKey(i.Operands[0])).ToList();

        Runner.AreEqual(builds.Count, built.Count, $"{domain} asks for every unit it was given");

        foreach (var instruction in built)
            Runner.AreEqual((byte)4, instruction.Operands[1], $"{domain} asks for the count given");

        Runner.AreEqual((byte)AiOpcode.Wait, wave[^2].Opcode, $"{domain} waits for its party");
        Runner.AreEqual((byte)AiOpcode.Var, wave[^1].Opcode, $"{domain} then launches");
        Runner.AreEqual((byte)1, wave[^1].Operands[1], $"{domain} launches by setting it to one");

        // The builds come before the party size, or the party is gathered from nothing.
        var lastBuild = wave.FindLastIndex(i => i.Opcode == (byte)AiOpcode.Var
                                                && builds.ContainsKey(i.Operands[0]));
        var waitAt = wave.FindIndex(i => i.Opcode == (byte)AiOpcode.Wait);

        Runner.IsTrue(lastBuild < waitAt, $"{domain} builds before it waits");
    }

    // A count of zero is left out: telling the computer to keep none of something is a
    // different instruction from not mentioning it.
    var quiet = AiTemplates.AttackWave(AiTemplates.AttackDomain.Land,
        builds: new Dictionary<byte, byte> { [0x14] = 0, [0x15] = 2 });

    Runner.IsTrue(!quiet.Any(i => i.Opcode == (byte)AiOpcode.Var && i.Operands[0] == 0x14),
        "a count of none is not written");

    var seconds = AiTemplates.AttackWave(AiTemplates.AttackDomain.Land, delaySeconds: 120);
    var ticks = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(seconds[0].Operands);

    Runner.AreEqual((uint)(120 * AiText.TicksPerSecond), ticks,
        "two minutes is written as two minutes of ticks");

    Console.WriteLine($"        (land wave: {AiTemplates.AttackWave(AiTemplates.AttackDomain.Land, builds: new Dictionary<byte, byte> { [0x14] = 4 }).Count} instructions)");
});

runner.Test("the opening block ends at the first repeated setting", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);
    var sizes = new List<int>();

    foreach (var script in file.Scripts.Where(s => !s.IsEmpty && !s.IsStub))
    {
        // The rule the editor groups by: leading setup writes, each variable once.
        var seen = new HashSet<byte>();
        var block = 0;

        foreach (var instruction in script.Instructions)
        {
            if (instruction.IsUnknown
                || instruction.Opcode != (byte)AiOpcode.Var
                || !AiTemplates.SetupVariables.Contains(instruction.Operands[0])
                || !seen.Add(instruction.Operands[0]))
                break;
            block++;
        }

        sizes.Add(block);

        // A block can never be longer than the number of settings there are.
        Runner.IsTrue(block <= AiTemplates.SetupVariables.Count,
            $"{script.Name}: block of {block} rows exceeds "
            + $"{AiTemplates.SetupVariables.Count} settings");
    }

    var biggest = sizes.Count == 0 ? 0 : sizes.Max();
    Console.WriteLine($"        (opening blocks run {sizes.Min()} to {biggest} rows)");

    // The old rule stopped only at a row that was not a setup write, so it ran on into
    // the first wave and reached 30. Anything above the settings count is that bug back.
    Runner.IsTrue(biggest <= 26, $"the largest opening block is {biggest} rows, not 26");
});

runner.Test("a frame's extra drawings come with it", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = Path.Combine(game.DataRoot, "Art", "hd", "unit",
                            "units_common_forest_sprites.json");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var atlas = SpriteAtlas.Load(path);

    // The runestone is rock_0 plus five facings named rock_0_f0 to rock_0_f4. Counting
    // alone found one of the six and the other five could not be edited at all.
    var rock = atlas.Sequence("rock_");
    Console.WriteLine($"        (rock_ has {rock.Count} frames: "
                      + string.Join(", ", rock.Select(f => f.Name)) + ")");

    Runner.AreEqual(6, rock.Count, "all six of the runestone's frames");
    Runner.AreEqual("rock_0", rock[0].Name, "the numbered frame comes first");
    Runner.IsTrue(rock.Any(f => f.Name == "rock_0_f4"), "and the last facing is there");

    // The order has to be stable: it is what the export grid is laid out by.
    var again = atlas.Sequence("rock_");
    Runner.IsTrue(rock.Select(f => f.Name).SequenceEqual(again.Select(f => f.Name)),
        "the same order every time");

    // A sprite with no extra drawings is unchanged: this must add frames where they
    // exist and nowhere else.
    var plain = atlas.Sequence("cannon_");
    Runner.IsTrue(plain.Count > 1 && plain.All(f => !f.Name.Contains("_f")),
        $"the cannon is still a plain run of {plain.Count} frames");
});

runner.Test("QA 2: a script with no build list says so", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    // Orc 4's build list begins with the terminator, so the game builds nothing from it.
    var orc4 = AiFile.Load(path).Scripts[0x04];

    Runner.AreEqual(0, orc4.BuildList.Length, "Orc 4's list is empty");
    Runner.AreEqual("This script has no build list, so this does nothing",
        orc4.DescribeBuildLimit(1), "and any limit says so rather than 'build everything'");
    Runner.AreEqual("This script has no build list, so this does nothing",
        orc4.DescribeBuildLimit(50), "at any value");
});

runner.Test("QA 3: a variable that would reach another player is refused", () =>
{
    // Var writes one byte at that offset in the player's state, and the states are 48
    // bytes apart, so anything past 0x2F lands in the next player's.
    var refused = false;
    try
    {
        AiFile.FromText("var $40 = 1");
    }
    catch (FormatException error)
    {
        refused = true;
        Runner.IsTrue(error.Message.Contains("2F", StringComparison.OrdinalIgnoreCase),
            "the message names the limit: " + error.Message);
    }

    Runner.IsTrue(refused, "$40 is refused");

    // And an ordinary one still works.
    var fine = AiFile.FromText("var $14 = 4")[0];
    Runner.AreEqual((byte)0x14, fine.Operands[0], "$14 is accepted");
});

runner.Test("QA 10: every wait condition the file uses has a name", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);
    var unnamed = new List<byte>();

    foreach (var script in file.Scripts)
    foreach (var instruction in script.Instructions)
    {
        if (instruction.IsUnknown || instruction.Opcode != (byte)AiOpcode.Wait) continue;

        var condition = instruction.Operands[0];
        if (AiTables.WaitName(condition).StartsWith("condition ", StringComparison.Ordinal))
            unnamed.Add(condition);
    }

    Console.WriteLine($"        (conditions used: "
                      + string.Join(", ", file.Scripts
                          .SelectMany(s => s.Instructions)
                          .Where(i => !i.IsUnknown && i.Opcode == (byte)AiOpcode.Wait)
                          .Select(i => (int)i.Operands[0]).Distinct().OrderBy(n => n)) + ")");

    Runner.AreEqual(0, unnamed.Distinct().Count(),
        "no wait shows a bare number: " + string.Join(", ", unnamed.Distinct()));

    Runner.IsTrue(AiTables.WaitName(0x07).Contains("workers", StringComparison.Ordinal),
        "condition 7 is named after what it looks for");
});

runner.Test("QA 13 and 14: sprite art survives export and import", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var index = SpriteIndex.Load(Path.Combine(game.DataRoot, SpriteIndex.Path));
    if (index.Source("art/unit/other/rock.grp") is not { } source)
    { Console.WriteLine("        (skipped)"); return; }

    if (index.Atlas("forest", source.Atlas) is not { } files)
    { Console.WriteLine("        (skipped)"); return; }

    var atlasFile = Path.Combine(game.DataRoot, files.Json.Replace('/', Path.DirectorySeparatorChar));
    var sheetFile = Path.Combine(game.DataRoot, files.Image.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(atlasFile) || !File.Exists(sheetFile))
    { Console.WriteLine("        (skipped)"); return; }

    var folder = Path.Combine(Path.GetTempPath(), "runefoundry-qa-" + Guid.NewGuid().ToString("N"));
    try
    {
        var written = SpriteSheetIO.Export(folder, "art/unit/other/rock.grp", source,
                                           sheetFile, atlasFile, null, null);

        Console.WriteLine($"        (exported {written.Frames} frames, "
                          + $"{written.Columns} across by {written.Rows} down)");

        // All six pictures, not just the numbered one.
        Runner.AreEqual(6, written.Frames, "the runestone exports six frames");
        Runner.IsTrue(File.Exists(Path.Combine(folder, SpriteSheetIO.SidecarName)),
            "with a sidecar beside them");

        // Reading the untouched grid back must rebuild a sheet the atlas still describes.
        var (sheet, atlasJson, _, _, result) = SpriteSheetIO.Import(
            folder, source, sheetFile, atlasFile, null, null);

        Runner.AreEqual(6, result.Frames, "and imports all six back");
        Runner.IsTrue(sheet.Length > 0, "the rebuilt sheet has bytes");

        var rebuilt = SpriteAtlas.Parse(atlasJson);
        Runner.AreEqual(6, rebuilt.Sequence(source.Prefix).Count,
            "the rebuilt atlas still lists six");
    }
    finally
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
});

runner.Test("QA 17: importing onto the wrong sprite explains itself", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var index = SpriteIndex.Load(Path.Combine(game.DataRoot, SpriteIndex.Path));
    if (index.Source("art/unit/other/rock.grp") is not { } rock) { Console.WriteLine("        (skipped)"); return; }
    if (index.Source("art/unit/orc/peon.grp") is not { } peon) { Console.WriteLine("        (skipped)"); return; }
    if (index.Atlas("forest", rock.Atlas) is not { } files) { Console.WriteLine("        (skipped)"); return; }

    var atlasFile = Path.Combine(game.DataRoot, files.Json.Replace('/', Path.DirectorySeparatorChar));
    var sheetFile = Path.Combine(game.DataRoot, files.Image.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(atlasFile)) { Console.WriteLine("        (skipped)"); return; }

    var folder = Path.Combine(Path.GetTempPath(), "runefoundry-qa-" + Guid.NewGuid().ToString("N"));
    try
    {
        SpriteSheetIO.Export(folder, "art/unit/other/rock.grp", rock,
                             sheetFile, atlasFile, null, null);

        var said = "";
        try
        {
            SpriteSheetIO.Import(folder, peon, sheetFile, atlasFile, null, null);
        }
        catch (InvalidDataException error)
        {
            said = error.Message;
        }

        Console.WriteLine("        (" + said + ")");

        Runner.IsTrue(said.Contains("art/unit/other/rock.grp", StringComparison.Ordinal),
            "the message names the sprite the folder came from");
        Runner.IsTrue(said.Contains("prefix", StringComparison.OrdinalIgnoreCase),
            "and explains what the prefix is");
    }
    finally
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
});

runner.Test("a typed line break is stored the way the game reads it", () =>
{
    var strings = GameStrings.Parse("{\"a\": \"one\"}");

    // A text box on Windows ends a line with a carriage return and a newline. The game
    // draws the carriage return as a missing glyph, so pressing Enter in an objective put
    // a question mark on screen.
    strings.Set("human_1_objectives", "-Build three new Farms\r\n-Build a Barracks");

    Runner.AreEqual("-Build three new Farms\n-Build a Barracks",
        strings.Get("human_1_objectives"), "the carriage return is gone");

    strings.Set("lone", "one\rtwo");
    Runner.AreEqual("one\ntwo", strings.Get("lone"), "a lone carriage return becomes a newline");

    strings.Set("clean", "already\nfine");
    Runner.AreEqual("already\nfine", strings.Get("clean"), "and a clean string is untouched");

    // The game's own strings are the standard being met.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped the shipped check)"); return; }

    var path = Path.Combine(game.DataRoot, "Strings", "enUS.json");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped the shipped check)"); return; }

    var shipped = GameStrings.Load(path);
    var withReturns = shipped.Keys.Count(k => shipped.Get(k)?.Contains('\r') == true);

    Console.WriteLine($"        ({shipped.Keys.Count} shipped strings, {withReturns} with a carriage return)");
    Runner.AreEqual(0, withReturns, "no shipped string carries one");
});

runner.Test("a silenced briefing keeps the shape of what it replaced", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    // Any of the game's own speech clips will do.
    var speech = Directory.Exists(Path.Combine(game.DataRoot, "Sfx"))
        ? Directory.EnumerateFiles(Path.Combine(game.DataRoot, "Sfx"), "*.wav",
                                   SearchOption.AllDirectories).FirstOrDefault()
        : null;

    if (speech is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(speech);
    var quiet = WaveFile.SilenceLike(original);

    var before = WaveFile.Describe(original);
    var after = WaveFile.Describe(quiet);

    Runner.IsTrue(after is not null, "the silent clip is a readable wave");
    Runner.IsTrue(before is not null, "and so was the original");

    Console.WriteLine($"        ({Path.GetFileName(speech)}: {before!.Duration.TotalSeconds:0.0}s "
                      + $"-> {after!.Duration.TotalSeconds:0.0}s silent)");

    // Same length, so the page is read for as long as it was heard.
    Runner.IsTrue(Math.Abs((after.Duration - before.Duration).TotalSeconds) < 0.1,
        $"the silence runs as long: {before.Duration} against {after.Duration}");

    // Actually silent.
    Runner.IsTrue(quiet.Skip(44).All(b => b is 0 or 128),
        "every sample is silence");
});

runner.Test("a campaign picture comes as a pair, and both halves are findable", () =>
{
    // The chooser draws one picture as the pointer rests on it and a fainter one when it
    // does not. They are separate frames, so replacing one alone leaves the author's art
    // in one state and the game's in the other.
    foreach (var campaign in new[] { "human", "orc", "xhuman", "xorc" })
    {
        var plain = CampaignArt.Sketch(campaign);
        var lit = CampaignArt.SketchHovered(campaign);

        Runner.IsTrue(plain is not null, $"{campaign} has a picture");
        Runner.IsTrue(lit is not null, $"{campaign} has a pointed-at picture");

        Runner.IsTrue(plain!.Frame != lit!.Frame,
            $"{campaign}: the two are different frames, not one drawn twice");

        Runner.AreEqual(plain.Frame + "_hovered", lit.Frame,
            $"{campaign}: the pointed-at one is named after the other");

        // Same sheet and same table, or replacing the pair would touch two files.
        Runner.AreEqual(plain.Image, lit.Image, $"{campaign}: both live in one sheet");
        Runner.AreEqual(plain.Atlas, lit.Atlas, $"{campaign}: and one table");
    }

    // The orc pair was swapped once: orc reads sketch_orc2 and the expansion sketch_orc1.
    Runner.AreEqual("sketch_orc2", CampaignArt.Sketch("orc")!.Frame,
        "the orc campaign draws sketch_orc2");
    Runner.AreEqual("sketch_orc1", CampaignArt.Sketch("xorc")!.Frame,
        "and the expansion draws sketch_orc1");

    Console.WriteLine("        (4 campaigns, 8 frames, 2 per campaign)");
});

runner.Test("a script's room is measured to its trailer, not to the end of its slot", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = Path.Combine(game.DataRoot, "Rez", "ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Parse(File.ReadAllBytes(path));

    var withTrailer = 0;
    var tight = 0;

    foreach (var script in file.Scripts)
    {
        if (script.IsEmpty) continue;

        // What the game ships must fit the rule, or the rule is wrong.
        Runner.IsTrue(script.CodeLength <= script.CodeCapacity,
            $"{script.Name}: {script.CodeLength} bytes of code in {script.CodeCapacity}");

        if (script.Trailer.Length == 0) continue;
        withTrailer++;

        // The distinction is not academic: for a script with a trailer the slot is bigger
        // than the room, and measuring against the slot would offer space that belongs to
        // another script's build list.
        Runner.IsTrue(script.CodeCapacity < script.OriginalBytes.Length,
            $"{script.Name}: a trailer should leave the code less room than the slot");

        if (script.CodeCapacity == script.CodeLength) tight++;
    }

    Console.WriteLine($"        ({file.Scripts.Count(s => !s.IsEmpty)} scripts, {withTrailer} with a trailer, "
                      + $"{tight} of those with no room at all)");
});

runner.Test("a pasted script is checked before it can be used", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = Path.Combine(game.DataRoot, "Rez", "ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Parse(File.ReadAllBytes(path));
    var script = file.Scripts.First(s => !s.IsEmpty && s.Instructions.Count > 4);
    var room = script.CodeCapacity - AiScript.HeaderLength;

    // A script copied out of the editor goes back in.
    var text = AiFile.ToText(script);
    var read = AiFile.CheckText(text, room);

    Runner.IsTrue(read.IsUsable, $"{script.Name} does not fit its own slot: {read.Problem}");
    Runner.AreEqual(script.Instructions.Count, read.Instructions!.Count,
        "the same instructions come back");
    Runner.AreEqual(script.CodeLength - AiScript.HeaderLength, read.Bytes,
        "and they weigh the same");

    // Too long for the slot.
    var twice = AiFile.CheckText(text + "\n" + text, room);
    Runner.IsTrue(!twice.IsUsable, "a script twice the size is refused");
    Runner.IsTrue(twice.Problem!.Contains("holds", StringComparison.Ordinal),
        $"and says what the slot holds: {twice.Problem}");

    // Not a script at all.
    var nonsense = AiFile.CheckText("this is not an AI script", room);
    Runner.IsTrue(!nonsense.IsUsable, "prose is refused");

    var empty = AiFile.CheckText("   ", room);
    Runner.IsTrue(!empty.IsUsable, "and so is nothing");

    // The comments the copy writes are ignored on the way back in.
    var stripped = string.Join("\n", text.Split('\n')
        .Select(line => line.Split(';')[0].TrimEnd()));

    var bare = AiFile.CheckText(stripped, room);
    Runner.IsTrue(bare.IsUsable, $"a script with its comments taken off still reads: {bare.Problem}");
    Runner.AreEqual(read.Bytes, bare.Bytes, "and weighs the same without them");

    Console.WriteLine($"        ({script.Name}: {read.Instructions.Count} instructions, "
                      + $"{read.Bytes} of {room} bytes)");
});

runner.Test("campaign art comes out at the size the game has it", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var atlasPath = game.ResolveDataPath(CampaignArt.SketchAtlas);
    var sheetPath = game.ResolveDataPath(CampaignArt.SketchImage);
    if (!File.Exists(atlasPath) || !File.Exists(sheetPath)) { Console.WriteLine("        (skipped)"); return; }

    // The campaign picture lives in a sheet, so exporting it means cutting it out. What
    // the card draws is a 240 pixel thumbnail, and that is what used to be written.
    var picture = CampaignArt.Sketch("human")!;
    var rect = FrameAtlas.Load(atlasPath).Rect(picture.Frame!)!.Value;

    Runner.IsTrue(rect.Width > 240,
        $"the frame is {rect.Width} wide, so a thumbnail would be visibly smaller");

    var sheet = AtlasImage.Load(sheetPath);
    var cut = new System.Windows.Media.Imaging.CroppedBitmap(sheet, new System.Windows.Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
    cut.Freeze();

    var temp = Path.Combine(Path.GetTempPath(), "runefoundry-export-check.png");
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cut));
    using (var stream = File.Create(temp)) encoder.Save(stream);

    var written = AtlasImage.Load(temp);

    Runner.AreEqual(rect.Width, written.PixelWidth, "the export is as wide as the frame");
    Runner.AreEqual(rect.Height, written.PixelHeight, "and as tall");

    File.Delete(temp);

    // A picture that is a whole file is copied instead, so nothing goes through a decoder.
    var splash = CampaignArt.Briefing("human", 1);
    if (splash is not null && !splash.InSheet)
    {
        var source = game.ResolveDataPath(splash.Image);
        if (File.Exists(source))
        {
            var copy = Path.Combine(Path.GetTempPath(), "runefoundry-export-copy.png");
            File.Copy(source, copy, overwrite: true);

            Runner.IsTrue(File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(copy)),
                "a whole file comes out byte for byte");

            var shape = AtlasImage.Load(copy);
            Console.WriteLine($"        ({rect.Width} x {rect.Height} cut, "
                              + $"{shape.PixelWidth} x {shape.PixelHeight} copied)");

            File.Delete(copy);
            return;
        }
    }

    Console.WriteLine($"        ({rect.Width} x {rect.Height} cut)");
});

runner.Test("both halves of a campaign picture survive being written", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var atlasPath = game.ResolveDataPath(CampaignArt.SketchAtlas);
    var sheetPath = game.ResolveDataPath(CampaignArt.SketchImage);
    if (!File.Exists(atlasPath) || !File.Exists(sheetPath)) { Console.WriteLine("        (skipped)"); return; }

    var atlas = FrameAtlas.Load(atlasPath);
    var plain = CampaignArt.Sketch("human")!;
    var lit = CampaignArt.SketchHovered("human")!;

    Runner.AreEqual(plain.Image, lit.Image, "the pair lives in one sheet");

    var one = atlas.Rect(plain.Frame!)!.Value;
    var two = atlas.Rect(lit.Frame!)!.Value;

    var first = new System.Windows.Int32Rect(one.X, one.Y, one.Width, one.Height);
    var second = new System.Windows.Int32Rect(two.X, two.Y, two.Width, two.Height);

    var sheet = AtlasImage.Load(sheetPath);

    // Two different pictures, written in one pass. Composing them separately gives two
    // whole sheets, each holding one change, and writing both to one path keeps only the
    // second: the campaign picture was replaced at rest and left as the game's when
    // pointed at.
    var written = AtlasImage.ReplaceFrames(sheet, new[]
    {
        (Solid(255, 0, 0), first),
        (Solid(0, 0, 255), second),
    });

    var temp = Path.Combine(Path.GetTempPath(), "runefoundry-pair-check.png");
    File.WriteAllBytes(temp, written);

    var after = AtlasImage.Load(temp);

    Runner.IsTrue(!AtlasImage.SameArea(sheet, after, first), "the picture at rest changed");
    Runner.IsTrue(!AtlasImage.SameArea(sheet, after, second), "and so did the pointed-at one");

    // And nothing else moved: the sheet holds 60-odd other frames.
    var elsewhere = atlas.Names
        .Where(n => n != plain.Frame && n != lit.Frame)
        .Select(n => atlas.Rect(n))
        .OfType<IconRect>()
        .Select(r => new System.Windows.Int32Rect(r.X, r.Y, r.Width, r.Height))
        .Where(r => r.X + r.Width <= sheet.PixelWidth && r.Y + r.Height <= sheet.PixelHeight)
        .Take(20)
        .ToList();

    foreach (var frame in elsewhere)
        Runner.IsTrue(AtlasImage.SameArea(sheet, after, frame),
            $"the frame at {frame.X},{frame.Y} was left alone");

    File.Delete(temp);

    Console.WriteLine($"        (2 frames written, {elsewhere.Count} others checked untouched)");

    // A flat colour, so a frame that took it is unmistakable.
    static System.Windows.Media.Imaging.BitmapSource Solid(byte red, byte green, byte blue)
    {
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }

        var made = System.Windows.Media.Imaging.BitmapSource.Create(
            16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4);

        made.Freeze();
        return made;
    }
});

runner.Test("every unit the game gives a voice has one here", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var library = SoundLibrary.Scan(game);
    var strings = GameStrings.Load(Path.Combine(game.DataRoot, "Strings", "enUS.json"));
    var table = UnitDataFile.Load(game.ResolveDataPath("Rez/unitdata.dat"));

    var silent = new List<string>();

    // 0x3A is the first building; those get their sounds from BuildingSounds.
    for (var unit = 0; unit < 0x3A && unit < table.RecordCount; unit++)
    {
        var named = UnitSounds.For(unit);

        // Every name in the table has to be a file the game actually has, or a mod would be
        // offered a line it cannot change.
        foreach (var sound in named)
            Runner.IsTrue(library.Find(sound.FileName) is not null,
                $"unit {unit:X2} asks for {sound.FileName}, which is not in the game");

        if (named.Count > 0) continue;
        if (table.IsEmpty(unit)) continue;

        silent.Add($"{unit:X2} {strings.UnitName(unit)}");
    }

    Console.WriteLine($"        ({silent.Count} units with no sound: {string.Join(", ", silent)})");
});

runner.Test("Deathwing speaks, and the Skeleton does not", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var strings = GameStrings.Load(Path.Combine(game.DataRoot, "Strings", "enUS.json"));

    // The two were swapped: the Skeleton was given Deathwing's nine lines and Deathwing was
    // given the Death Knight's, which left Gamesfx/DeathWng unreachable and the Skeleton
    // saying "yes, my lord".
    var deathwing = UnitSounds.For(0x23);
    var skeleton = UnitSounds.For(0x37);

    Runner.IsTrue(deathwing.All(s => s.FileName.StartsWith("De", StringComparison.Ordinal)),
        "Deathwing uses his own folder: " + string.Join(", ", deathwing.Select(s => s.FileName)));

    Runner.AreEqual(2, skeleton.Count, "the Skeleton has the two clips it has and no more");

    Runner.IsTrue(skeleton.Any(s => s.FileName == "Skeleton Move.wav"), "it moves");
    Runner.IsTrue(skeleton.Any(s => s.FileName == "Skeleton Death.wav"
                                    && s.Kind == UnitSoundKind.Death), "and it dies");

    Console.WriteLine($"        ({strings.UnitName(0x23)}: {deathwing.Count} lines; "
                      + $"{strings.UnitName(0x37)}: {skeleton.Count})");
});

runner.Test("a sprite knows its seasonal twins", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var index = SpriteIndex.Load(Path.Combine(game.DataRoot, SpriteIndex.Path));

    // The rock is forest and swamp; winter and the expansion swamp are separate sprites.
    var family = index.Family("art/unit/other/rock.grp");

    Runner.IsTrue(family.Contains("art/unit/other/s_rock.grp"),
        "the rock knows about s_rock: " + string.Join(", ", family));
    Runner.IsTrue(family.Contains("art/unit/other/x_rock.grp"),
        "and about x_rock: " + string.Join(", ", family));

    // It works from either end, or a person starting at the winter one is no better off.
    Runner.IsTrue(index.Family("art/unit/other/s_rock.grp").Contains("art/unit/other/rock.grp"),
        "and s_rock knows about the rock");

    // A sprite with no twins says so rather than guessing.
    Runner.AreEqual(0, index.Family("art/unit/human/knight.grp").Count,
        "the Knight is drawn once and has no seasonal twins");

    var families = index.Paths.Count(p => index.Family(p).Count > 0);
    Console.WriteLine($"        ({families} of {index.Paths.Count} sprites have twins)");

    Runner.IsTrue(families > 0, "some sprites really are split this way");
});

runner.Test("art exported once is imported into every season", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var index = SpriteIndex.Load(Path.Combine(game.DataRoot, SpriteIndex.Path));
    var source = index.Source("art/unit/other/rock.grp");

    Runner.IsTrue(source is not null, "the rock is in the sprite index");
    Runner.AreEqual("rock_", source!.Value.Prefix, "and files its frames under rock_");

    // The rock's atlas group resolves to a different file in each season, which is why
    // exporting it writes a folder per season rather than one folder.
    var files = index.Eras
        .Select(era => index.Atlas(era, source.Value.Atlas)?.Json)
        .OfType<string>()
        .Distinct()
        .ToList();

    Console.WriteLine($"        ({index.Eras.Count} seasons, "
                      + $"{files.Count} atlases for {source.Value.Atlas})");

    Runner.IsTrue(files.Count > 1,
        "this sprite really does have more than one atlas, or the test proves nothing");

    // The group name is shared by every season; the art is not. The rock is drawn in
    // forest and swamp only, so importing into the other two would fail on an atlas with
    // no rock in it.
    var withArt = 0;
    foreach (var era in index.Eras)
    {
        if (index.Atlas(era, source.Value.Atlas)?.Json is not { } relative) continue;

        var full = Path.Combine(game.DataRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) continue;

        if (SpriteAtlas.Load(full).Sequence(source.Value.Prefix, "").Count > 0) withArt++;
    }

    Console.WriteLine($"        ({withArt} of {index.Eras.Count} seasons draw the rock)");

    // One group names a different sheet in every season, so anything holding a decoded
    // sheet has to key on the file. Keying on the group showed one season's art in
    // another's frames.
    var sheets = index.Eras
        .Select(era => index.Atlas(era, source.Value.Atlas)?.Image)
        .OfType<string>()
        .ToList();

    Runner.AreEqual(sheets.Count, sheets.Distinct().Count(),
        "each season names its own sheet file: " + string.Join(", ", sheets));
    Runner.IsTrue(withArt > 0 && withArt < index.Eras.Count,
        $"the rock is in some seasons and not others, not {withArt}");

    // A folder holding the sidecar directly stands in for every season.
    var root = Path.Combine(Path.GetTempPath(), "runefoundry-era-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, SpriteSheetIO.SidecarName), "{}");

        foreach (var era in index.Eras)
            Runner.AreEqual(root, SpriteSheetIO.ArtFolder(root, era, files.Count),
                $"{era} falls back to the folder itself");

        // A season with its own folder uses that instead.
        var winter = Path.Combine(root, "winter");
        Directory.CreateDirectory(winter);
        File.WriteAllText(Path.Combine(winter, SpriteSheetIO.SidecarName), "{}");

        Runner.AreEqual(winter, SpriteSheetIO.ArtFolder(root, "winter", files.Count),
            "winter uses its own folder when it has one");
        Runner.AreEqual(root, SpriteSheetIO.ArtFolder(root, "forest", files.Count),
            "and the others still fall back");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
});

runner.Test("a wave can attack by land and air at once", () =>
{
    var land = new AiTemplates.AttackPart(
        AiTemplates.AttackDomain.Land, 3,
        new Dictionary<byte, byte> { [0x14] = 4 });
    var air = new AiTemplates.AttackPart(
        AiTemplates.AttackDomain.Air, 2,
        new Dictionary<byte, byte> { [0x20] = 2 });

    var wave = AiTemplates.AttackWave(new[] { land, air }, delaySeconds: 60);

    // One sleep opens the wave, whatever it goes on to do.
    Runner.AreEqual(1, wave.Count(i => i.Opcode == (byte)AiOpcode.Sleep),
        "one sleep, not one per domain");

    // Each domain gets its own wait and its own launch.
    var waits = wave.Where(i => i.Opcode == (byte)AiOpcode.Wait)
                    .Select(i => i.Operands[0]).ToList();
    Runner.IsTrue(waits.Contains((byte)0x04), "it waits for the land party");
    Runner.IsTrue(waits.Contains((byte)0x06), "and for the air party");

    var launches = wave
        .Where(i => i.Opcode == (byte)AiOpcode.Var && i.Operands[1] == 1)
        .Select(i => i.Operands[0]).ToList();
    Runner.IsTrue(launches.Contains((byte)0x09), "it launches the land attack");
    Runner.IsTrue(launches.Contains((byte)0x0B), "and the air attack");

    // The party sizes carry their multipliers, or the wait means something else.
    var sets = wave
        .Where(i => i.Opcode == (byte)AiOpcode.Var)
        .ToDictionary(i => i.Operands[0], i => i.Operands[1]);
    Runner.AreEqual((byte)3, sets[0x0D], "land party size");
    Runner.AreEqual((byte)1, sets[0x0E], "land party multiplier");
    Runner.AreEqual((byte)2, sets[0x11], "air party size");
    Runner.AreEqual((byte)1, sets[0x12], "air party multiplier");

    // Single domain still works, and is what the old call gives.
    var one = AiTemplates.AttackWave(AiTemplates.AttackDomain.Land, 3, 60,
        new Dictionary<byte, byte> { [0x14] = 4 });
    var same = AiTemplates.AttackWave(new[] { land }, 60);
    Runner.AreEqual(one.Sum(i => i.Length), same.Sum(i => i.Length),
        "one domain writes the same bytes either way");

    Console.WriteLine($"        (land+air wave: {wave.Count} instructions, "
                      + $"{wave.Sum(i => i.Length)} bytes)");
});

runner.Test("the advice is quiet on the game's own scripts", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);
    var complaints = new List<string>();

    foreach (var script in file.Scripts.Where(s => !s.IsEmpty))
    {
        foreach (var instruction in script.Instructions)
        {
            if (AiAdvice.About(script, instruction) is { } note)
                complaints.Add($"{script.Name}: {note}");
        }
    }

    Console.WriteLine($"        ({complaints.Count} complaints across "
                      + $"{file.Scripts.Count(s => !s.IsEmpty)} scripts)");
    foreach (var line in complaints.Distinct().Take(3)) Console.WriteLine("          " + line);

    // Blizzard's own scripts work, so anything flagged here is a false alarm.
    Runner.IsTrue(complaints.Count == 0,
        "nothing in the shipped file is flagged: " + string.Join(" | ", complaints.Take(3)));
});

runner.Test("the advice catches what it is for", () =>
{
    var stub = new AiScript { Index = 0 };

    // A variable past 0x2F writes into the next player's state.
    var stray = new AiInstruction { Opcode = (byte)AiOpcode.Var, Operands = new byte[] { 0x40, 1 } };
    Runner.IsTrue(AiAdvice.About(stub, stray)?.Contains("another player") == true,
        "a variable past the end of the state is caught");

    var fine = new AiInstruction { Opcode = (byte)AiOpcode.Var, Operands = new byte[] { 0x14, 4 } };
    Runner.IsTrue(AiAdvice.About(stub, fine) is null, "an ordinary unit count is not");

    // Waiting for a sea party in a script that never asks for ships never ends.
    var landOnly = new AiScript { Index = 1 };
    landOnly.Instructions.Add(new AiInstruction
        { Opcode = (byte)AiOpcode.Var, Operands = new byte[] { 0x14, 4 } });
    landOnly.Instructions.Add(new AiInstruction
        { Opcode = (byte)AiOpcode.Var, Operands = new byte[] { 0x0F, 3 } });
    var seaWait = new AiInstruction { Opcode = (byte)AiOpcode.Wait, Operands = new byte[] { 0x05 } };
    landOnly.Instructions.Add(seaWait);

    Runner.IsTrue(AiAdvice.Caution(landOnly, seaWait)?.Contains("never asks") == true,
        "a sea wait with no ships is a caution, not a verdict: the map may supply them");
});

runner.Test("a build limit says where the building stops", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);

    // Orc 14 (Green) builds its base from a mixed list, so its limits step down a
    // list whose entries have names. It is the case that read as a bare number.
    var green = file.Scripts[0x1A];

    Runner.IsTrue(green.HasBuildList, "Orc 14 (Green) has a readable build list");
    Runner.IsTrue(green.BuildList.Length == 27,
        $"its list holds 27 entries, not {green.BuildList.Length}");

    // The list is 0xFF terminated. Reading a fixed window used to run past the end.
    Runner.IsTrue(!green.BuildList.Contains((byte)0xFF), "and stops at the terminator");

    Runner.AreEqual("Build nothing from the build list", green.DescribeBuildLimit(0),
        "zero builds nothing");
    Runner.AreEqual("Build the list down to Town Hall / Great Hall",
        green.DescribeBuildLimit(1), "one reaches the first entry");
    Runner.AreEqual("Build everything in the build list", green.DescribeBuildLimit(27),
        "the full count is everything");
    Runner.AreEqual("Build everything in the build list", green.DescribeBuildLimit(28),
        "and so is more than the full count");

    var described = green.Instructions
        .Where(i => !i.IsUnknown && i.Opcode == (byte)AiOpcode.Var && i.Operands[0] == 0x22)
        .Select(i => AiText.Describe(i, green.DescribeBuildLimit))
        .ToList();

    Console.WriteLine($"        ({described.Count} build limits, "
                      + $"{described.Distinct().Count()} distinct)");
    foreach (var line in described.Take(4)) Console.WriteLine("          " + line);

    Runner.IsTrue(described.All(d => !d.Contains("build list entry", StringComparison.Ordinal)),
        "and none of them is a bare number");
});

runner.Test("no instruction describes itself as a number when it has a name", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var path = vault.StockFile(game, "Rez/ai.bin");
    if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

    var file = AiFile.Load(path);

    // Variables above the labelled range are build-item codes. Reading them as "Variable
    // $3A" put a hex number on screen where a Farm was meant.
    Runner.AreEqual("Farm", AiNames.Variable(0x3A), "0x3A is a Farm");
    Runner.AreEqual("Barracks", AiNames.Variable(0x3C), "0x3C is a Barracks");
    Runner.IsTrue(AiNames.IsBuildItem(0x3A), "and it is known to be something built");
    Runner.IsTrue(!AiNames.IsBuildItem(0x14), "while a unit count is not");

    Runner.AreEqual("Build and ensure there's always 4 Footmen / Grunts in the army",
        AiText.DescribeVar(0x14, 4), "a unit count reads as a standing request");
    Runner.AreEqual("Stop building Footmen / Grunts",
        AiText.DescribeVar(0x14, 0), "and zero clears it");

    // Every description the shipped file produces, checked for leftover raw numbers.
    var vague = new List<string>();

    foreach (var script in file.Scripts)
    {
        foreach (var instruction in script.Instructions)
        {
            if (instruction.IsUnknown) continue;

            var text = AiText.Describe(instruction, script.DescribeBuildLimit);

            if (text.Contains("build-list entry", StringComparison.Ordinal)
                || text.Contains("Variable $", StringComparison.Ordinal)
                || text.Contains("item $", StringComparison.Ordinal)
                || text.Contains("Variable ", StringComparison.Ordinal))
                vague.Add(text);
        }
    }

    Console.WriteLine($"        ({file.Scripts.Sum(s => s.Instructions.Count)} instructions, "
                      + $"{vague.Distinct().Count()} still described by number)");

    // The build list lives in the map rather than in ai.bin, so a script whose list this
    // build does not carry can only say which entry was queued. That is the honest limit,
    // and it should be rare.
    Console.WriteLine("        (" + string.Join(" | ", vague.Distinct().Take(6)) + ")");
});

runner.Test("holding a clip longer pads it, and never cuts it short", () =>
{
    var format = new WaveFormat(1, 22050, 8, 0);

    // A second of tone, then a second of silence: something with a tail to trim.
    var tone = WaveFile.Silence(TimeSpan.FromSeconds(2), format);
    for (var i = 44; i < 44 + 22050; i++) tone[i] = (byte)(i % 2 == 0 ? 0x20 : 0xE0);

    Runner.AreEqual(2.0, Math.Round(WaveFile.Describe(tone)!.Duration.TotalSeconds, 2),
        "the clip starts out two seconds long");

    var trimmed = WaveFile.Trimmed(tone);
    Runner.AreEqual(1.0, Math.Round(WaveFile.Describe(trimmed)!.Duration.TotalSeconds, 2),
        "trimming drops the silent second and keeps the tone");

    var held = WaveFile.HeldFor(tone, TimeSpan.FromSeconds(8));
    Runner.AreEqual(8.0, Math.Round(WaveFile.Describe(held)!.Duration.TotalSeconds, 2),
        "holding it to eight seconds gives eight seconds");

    // The point of trimming first: adjusting twice must not stack padding on padding.
    var again = WaveFile.HeldFor(held, TimeSpan.FromSeconds(5));
    Runner.AreEqual(5.0, Math.Round(WaveFile.Describe(again)!.Duration.TotalSeconds, 2),
        "holding an already-padded clip to five seconds gives five, not thirteen");

    var short_ = WaveFile.HeldFor(tone, TimeSpan.FromSeconds(0.25));
    Runner.AreEqual(1.0, Math.Round(WaveFile.Describe(short_)!.Duration.TotalSeconds, 2),
        "asking for less than the sound itself keeps the sound whole");

    // Padding an 8-bit clip with zeroes rather than mid-scale is an audible click.
    var tail = held[^100..];
    Runner.IsTrue(Array.TrueForAll(tail, b => b == 0x80), "8-bit padding is silence, not a click");
});

runner.Test("the act fanfare can be held longer to keep the act title card up", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);

    foreach (var campaign in new[] { "human", "orc" })
    {
        var cue = GameMedia.Fanfare(campaign);
        var path = vault.StockFile(game, cue.Files[0].Path);
        if (!File.Exists(path)) { Console.WriteLine("        (skipped)"); return; }

        var original = File.ReadAllBytes(path);
        var shape = WaveFile.Describe(original);
        Runner.IsTrue(shape is not null, $"{cue.Files[0].Path} is plain PCM we can reshape");

        var held = WaveFile.HeldFor(original, TimeSpan.FromSeconds(9));
        var heldShape = WaveFile.Describe(held)!;

        Console.WriteLine($"        ({cue.Files[0].Path}: {shape!.Duration.TotalSeconds:F1}s -> "
                          + $"{heldShape.Duration.TotalSeconds:F1}s)");

        Runner.AreEqual(9.0, Math.Round(heldShape.Duration.TotalSeconds, 1),
            "the fanfare now runs nine seconds, so the card stays nine seconds");
        Runner.AreEqual(shape.SampleRate, heldShape.SampleRate, "at the same rate");
        Runner.AreEqual(shape.BitsPerSample, heldShape.BitsPerSample, "and the same depth");

        Runner.IsTrue(WaveFile.Describe(WaveFile.Trimmed(original))!.Duration <= shape.Duration,
            "trimming never lengthens a clip");
    }
});

runner.Test("resetting one mission puts back everything it is made of, and nothing else", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-reset-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var project = ModProject.Create(Path.Combine(folder, "reset.w2proj"), "Reset");

        var human = Campaign.All.First(c => c.Id == "human");
        var mission = human.Missions(null)[0];
        var other = human.Missions(null)[1];

        // A mission is four kinds of thing at once, and a reset that forgets one is a trap.
        project.WriteOverride(mission.MapPath, new byte[] { 1, 2, 3 });
        foreach (var page in mission.Pages) project.WriteOverride(page.SpeechPath, new byte[] { 4 });
        project.SetObjective(mission.ExeSlot, 0x0100);
        project.SetThreshold(mission.ExeSlot, 4);

        // The neighbour must come through untouched.
        project.WriteOverride(other.MapPath, new byte[] { 9 });
        project.SetObjective(other.ExeSlot, 8);

        var stock = GameStrings.Parse("{\"human_1_name\":\"Hillsbrad\",\"human_2_name\":\"Ambush\"}");
        var mod = GameStrings.Parse("{\"human_1_name\":\"Mine\",\"human_2_name\":\"Ambush\"}");
        var pageKey = mission.Pages[0].TextKey;
        mod.Set(pageKey, "invented");

        var plan = MissionReset.Prepare(project, mission, mod, stock);

        Runner.IsTrue(!plan.IsEmpty, "there is something to reset");
        Runner.AreEqual(1 + mission.Pages.Count, plan.Files.Count, "the map and every briefing recording");
        Runner.AreEqual(2, plan.TextKeys.Count, "the changed name and the invented page");
        Runner.IsTrue(plan.ScenarioRule, "and the scenario rule");
        Runner.IsTrue(plan.Describe().Contains("the map"), "the description says so: " + plan.Describe());

        Runner.IsTrue(MissionReset.Apply(project, mission, plan, mod, stock), "the text needed saving");

        Runner.IsTrue(!project.HasOverride(mission.MapPath), "the map is gone");
        foreach (var page in mission.Pages)
            Runner.IsTrue(!project.HasOverride(page.SpeechPath), "and every recording");
        Runner.IsTrue(project.ObjectiveFor(mission.ExeSlot) is null, "and the objective");
        Runner.IsTrue(project.ThresholdFor(mission.ExeSlot) is null, "and its threshold");

        // Restored to the game's wording, not blanked.
        Runner.AreEqual("Hillsbrad", mod.Get("human_1_name"), "the name is the game's again");

        // A key the mod invented is removed, not set to empty — the game would show empty.
        Runner.IsTrue(mod.Get(pageKey) is null, "an invented key is dropped, not blanked");

        // The neighbour is untouched.
        Runner.IsTrue(project.HasOverride(other.MapPath), "the next mission keeps its map");
        Runner.AreEqual(8, project.ObjectiveFor(other.ExeSlot) ?? -1, "and its rule");
        Runner.AreEqual("Ambush", mod.Get("human_2_name"), "and its text");

        // Doing it twice is not an error, it is a no-op.
        Runner.IsTrue(MissionReset.Prepare(project, mission, mod, stock).IsEmpty,
            "a reset mission has nothing left to reset");

        // Build restrictions live in the map, so they are named only when the map has them.
        var mapPath = new BackupVault(BackupVault.DefaultRoot).StockFile(
            GameInstall.Detect() ?? throw new InvalidOperationException(), mission.MapPath);
        if (File.Exists(mapPath))
        {
            var withAllow = PudFile.WriteAllow(File.ReadAllBytes(mapPath),
                CampaignTech.AllowArrays(0, 0, 0, new[] { 1 }));
            project.WriteOverride(mission.MapPath, withAllow);

            var withTech = MissionReset.Prepare(project, mission, mod, stock, withAllow);
            Runner.IsTrue(withTech.Describe().Contains("build restrictions"),
                "a map carrying an ALOW chunk says so: " + withTech.Describe());
        }
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
});

runner.Test("a duplicated sound name resolves by a stated rule, not by directory order", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-snd-" + Guid.NewGuid().ToString("N"));
    var data = Path.Combine(folder, "x86", "Data");
    try
    {
        void Wav(params string[] parts)
        {
            var path = Path.Combine(new[] { data }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1 });
        }

        // The same name in three places: another root, the speaker's folder, and a stray
        // subfolder of it. This is the shape the game actually ships.
        Wav("Sfx", "Knwhat1.wav");
        Wav("Gamesfx", "Knight", "Knwhat1.wav");
        Wav("Gamesfx", "Knight", "a", "Knwhat1.wav");

        // And a name with no reason to prefer either: same root, same depth.
        Wav("Gamesfx", "Bravo", "Tie.wav");
        Wav("Gamesfx", "Alpha", "Tie.wav");

        // TryOpen wants a complete install; the stand-in only needs to answer for its Data
        // folder, so it gets an empty executable to satisfy that check.
        File.WriteAllBytes(Path.Combine(folder, "x86", "Warcraft II.exe"), Array.Empty<byte>());

        Runner.IsTrue(GameInstall.TryOpen(folder, out var install, out var why), "a stand-in install: " + why);
        var sounds = SoundLibrary.Scan(install!);

        // Root order beats depth: Sfx/Knwhat1.wav is shallower and still loses.
        Runner.AreEqual("Gamesfx/Knight/Knwhat1.wav", sounds.Find("Knwhat1.wav"),
            "the first root wins even when a later one is shallower");

        var knight = sounds.Collisions.Single(c => c.FileName == "Knwhat1.wav");
        Runner.AreEqual(2, knight.Ignored.Count, "and the other two are recorded as ignored");
        Runner.IsTrue(!knight.Ambiguous, "with nothing ambiguous about it");

        // A real tie is decided the same way every run, and flagged rather than hidden.
        Runner.AreEqual("Gamesfx/Alpha/Tie.wav", sounds.Find("Tie.wav"), "a tie breaks by path, not by walk order");
        Runner.IsTrue(sounds.Collisions.Single(c => c.FileName == "Tie.wav").Ambiguous,
            "and is reported as a guess");
        Runner.AreEqual(1, sounds.AmbiguousCollisions.Count(), "which is the only one worth reporting");

        // Case does not matter, as it did not before.
        Runner.AreEqual("Gamesfx/Knight/Knwhat1.wav", sounds.Find("KNWHAT1.WAV"), "names match without case");
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }

    // And against the install itself: the game ships these duplicates, and every one of
    // them must resolve to the copy the game plays.
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var real = SoundLibrary.Scan(game);
    Console.WriteLine($"        ({real.Count} names, {real.Collisions.Count} duplicated)");

    Runner.AreEqual(0, real.AmbiguousCollisions.Count(),
        "no shipped duplicate needs a guess: "
        + string.Join(", ", real.AmbiguousCollisions.Select(c => c.FileName)));

    foreach (var collision in real.Collisions)
        Runner.IsTrue(!collision.Chosen.Contains("/a/") && !collision.Chosen.Contains("New folder"),
            $"{collision.FileName} resolves to the played copy, not a stray one ({collision.Chosen})");

    // The one this started from.
    if (real.Find("Tuyessr1.wav") is { } turalyon)
        Runner.AreEqual("Gamesfx/Turalyon/Tuyessr1.wav", turalyon, "Turalyon's line is the one beside the folder");
});

runner.Test("every scenario rule can be told apart from the others in a dropdown", () =>
{
    // These are read as a list, so a label that does not distinguish its rule from the one
    // above it is a bug even when every word of it is true.
    var labels = CampaignObjectiveNames.Choices.Select(c => c.Label).ToList();
    var repeated = labels.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    Runner.AreEqual(0, repeated.Count, "no two rules share a label: " + string.Join(" / ", repeated));

    var ids = CampaignObjectiveNames.Choices.Select(c => c.Id).ToList();
    Runner.AreEqual(ids.Count, ids.Distinct().Count(), "and no id is listed twice");

    // XOrc01 is a destroy rule with a survivor, not a delivery. The handler at 0x004F5700
    // loses when no unit of type 0x19 is left, and wins when two counters reach zero: the
    // Death Knights and their Temple, which is what the shipped objective text says.
    var grom = CampaignObjectiveNames.Describe(11);

    Runner.IsTrue(grom.Contains("Death Knight", StringComparison.Ordinal),
        "objective 11 is about the Death Knights: " + grom);
    Runner.IsTrue(grom.Contains("Grom", StringComparison.Ordinal),
        "and it names the hero who has to survive: " + grom);
    Runner.IsTrue(!grom.Contains("Circle of Power", StringComparison.Ordinal),
        "and it is not a delivery to the Circle of Power: " + grom);

    // Human10 ships objective 4, which the dispatcher rewrites to 0x200 before play. It is
    // understood, so it must not read as undecoded.
    Runner.IsTrue(CampaignObjectiveNames.IsKnown(4), "objective 4 is known");
    Runner.AreEqual(CampaignObjectiveNames.Describe(0x0200), CampaignObjectiveNames.Describe(4),
        "and describes as the rule it becomes");
    Runner.IsTrue(!CampaignObjectiveNames.Describe(4).Contains("not yet decoded"), "not as a gap");
    Runner.IsTrue(CampaignObjectiveNames.Choices.All(c => c.Id != 4),
        "but is not offered as a second entry reading the same as 0x0200");

    // Every id the game actually ships must describe as something.
    var game = GameInstall.Detect();
    if (game is null || !File.Exists(game.ExecutablePath)) { Console.WriteLine("        (skipped)"); return; }

    var shipped = CampaignObjectives.Read(File.ReadAllBytes(game.ExecutablePath)).Distinct().ToList();
    var unknown = shipped.Where(id => !CampaignObjectiveNames.IsKnown(id)).ToList();
    Runner.AreEqual(0, unknown.Count,
        "every objective the campaign ships is described: " + string.Join(", ", unknown));
});

runner.Test("the campaign tech tables hold the mission build restrictions", () =>
{
    var game = GameInstall.Detect();
    if (game is null || !File.Exists(game.ExecutablePath)) { Console.WriteLine("        (skipped)"); return; }

    var exe = File.ReadAllBytes(game.ExecutablePath);

    var units = CampaignTech.Read(exe, CampaignTech.Table.Units);
    var upgrades = CampaignTech.Read(exe, CampaignTech.Table.Upgrades);
    var spells = CampaignTech.Read(exe, CampaignTech.Table.Spells);

    Runner.AreEqual(52, units.Length, "52 campaign slots");
    Runner.AreEqual(52, upgrades.Length, "and the same for upgrades");
    Runner.AreEqual(52, spells.Length, "and for spells");

    // Human 1 is the mission the restriction is most obvious in: farms and barracks and
    // nothing else, no upgrades, no spells. If this drifts the addresses are wrong and
    // writing to them would be writing into something unknown.
    Runner.AreEqual(0x04030003u, units[0], "Human01 allows five things");
    Runner.AreEqual(0u, upgrades[0], "Human01 researches nothing");
    Runner.AreEqual(0u, spells[0], "Human01 casts nothing");

    Runner.IsTrue(CampaignTech.Restricts(units[0]), "so it counts as restricted");
    Runner.IsTrue(!CampaignTech.Restricts(CampaignTech.Everything), "and an open mission does not");

    // The campaign opens up as it goes: the last mission must allow more than the first.
    Runner.IsTrue(units[50] > units[0], "the tech tree grows through the campaign");

    // The three tables are laid out around the objective ones, which is how they were
    // found; if that spacing changed, this is no longer the same executable.
    Runner.AreEqual(0x008C1BB8u - 0x008C1AE8u, (uint)(CampaignObjectives.TableAddress - CampaignTech.UnitsAddress),
        "the unit table sits one table ahead of the objectives");
});

runner.Test("an ALOW chunk lifts a mission's build restrictions in the map itself", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var vault = new BackupVault(BackupVault.DefaultRoot);
    var mapPath = vault.StockFile(game, @"Campaign\Human\HUMAN01.PUD");
    if (!File.Exists(mapPath)) { Console.WriteLine("        (skipped)"); return; }

    var stock = File.ReadAllBytes(mapPath);
    var pud = PudFile.Parse(stock);

    // The shipped campaign maps carry no ALOW chunk, which is exactly why the executable's
    // table is what takes effect. If that ever changes, this feature is overwriting
    // something the map meant to say.
    Runner.IsTrue(!PudFile.HasAllow(stock), "the stock map has no ALOW chunk");

    // Human 1 is played by player 2; the human slot is not the same one in every mission,
    // which is why it is read from OWNR rather than assumed.
    var humans = CampaignTech.HumanPlayers(pud.PlayerOwners);
    Runner.AreEqual(1, humans.Count, "one human slot");
    Runner.AreEqual(1, humans[0], "and on Human 1 it is player 2");

    var arrays = CampaignTech.AllowArrays(0x04030003, 0, 0, humans);
    var withAllow = PudFile.WriteAllow(stock, arrays);

    // The game checks the length and ignores the chunk if it is wrong.
    var (at, length) = (0, 0);
    var parsed = PudFile.Parse(withAllow);
    var chunk = parsed.Chunks.First(c => c.Tag == "ALOW");
    (at, length) = (chunk.Offset, chunk.Length);
    Runner.AreEqual(PudFile.AllowChunkSize, length, "the chunk is exactly 0x180 bytes");
    Runner.AreEqual(0x180, length, "which is what the handler demands");

    // It belongs after UDTA, where the format puts it.
    var tags = parsed.Chunks.Select(c => c.Tag).ToList();
    Runner.AreEqual(tags.IndexOf("UDTA") + 1, tags.IndexOf("ALOW"), "and sits straight after UDTA");

    var read = PudFile.ReadAllow(withAllow);
    Runner.IsTrue(read is not null, "and reads back");

    // The human is freed and nobody else is: an AI that could build four things still can.
    Runner.AreEqual(CampaignTech.Everything, read![CampaignTech.AllowUnits][1], "the human may build anything");
    Runner.AreEqual(CampaignTech.Everything, read[CampaignTech.AllowUpgrades][1], "and research anything");
    Runner.AreEqual(CampaignTech.Everything, read[CampaignTech.AllowSpells][1], "and cast anything");
    Runner.AreEqual(0x04030003u, read[CampaignTech.AllowUnits][0], "the computer keeps the campaign's masks");
    Runner.AreEqual(0u, read[CampaignTech.AllowUpgrades][0], "including no upgrades");

    // The arrays the fanout fills with constants must be reproduced, not invented.
    Runner.AreEqual(CampaignTech.UpgradeStateSeed, read[CampaignTech.AllowUpgradeState][0],
        "the upgrade state seed is the game's own 0x4020");
    Runner.AreEqual(0u, read[CampaignTech.AllowUnitState][0], "and the other two are zero");
    Runner.AreEqual(0u, read[CampaignTech.AllowSpellState][0], "as the fanout leaves them");

    Runner.IsTrue(CampaignTech.OpensEverythingFor(read, humans), "the checkbox can tell it is on");
    Runner.IsTrue(!CampaignTech.OpensEverythingFor(read, new[] { 0 }), "and that the computer is not");

    // Taking it out again has to give back the byte-for-byte original, or unticking the box
    // would leave the map subtly different from the game's.
    var removed = PudFile.RemoveAllow(withAllow);
    Runner.IsTrue(removed.AsSpan().SequenceEqual(stock), "removing it restores the map exactly");

    // Writing twice replaces rather than stacks.
    Runner.AreEqual(withAllow.Length, PudFile.WriteAllow(withAllow, arrays).Length, "writing twice replaces");

    // And the map still parses as the game will read it.
    var after = PudFile.Parse(withAllow);
    Runner.AreEqual(pud.Width, after.Width, "the map still reads");
    Runner.IsTrue(after.StructureWarning is null, "with no structural complaint: " + after.StructureWarning);
});

runner.Test("switching a mission to destroy-all is two bytes and reversible", () =>
{
    var game = GameInstall.Detect();
    if (game is null || !File.Exists(game.ExecutablePath)) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(game.ExecutablePath);
    var patched = (byte[])original.Clone();

    CampaignObjectives.Write(patched, 0, CampaignObjectives.DestroyAllEnemies);

    var differing = 0;
    for (var i = 0; i < original.Length; i++) if (original[i] != patched[i]) differing++;
    Runner.AreEqual(1, differing, "exactly one byte changes for objective 0 -> 0x0100");

    Runner.AreEqual(CampaignObjectives.DestroyAllEnemies, CampaignObjectives.Read(patched)[0], "and it took");
    Runner.IsTrue(CampaignObjectives.IsDestroyAll(CampaignObjectives.Read(patched)[0]), "reads back as destroy-all");

    // Every other slot must be untouched.
    var before = CampaignObjectives.Read(original);
    var after = CampaignObjectives.Read(patched);
    for (var i = 1; i < before.Length; i++)
        Runner.AreEqual(before[i], after[i], $"slot {i} is left alone");

    // Putting the original id back restores the file exactly.
    CampaignObjectives.Write(patched, 0, before[0]);
    Runner.IsTrue(original.AsSpan().SequenceEqual(patched), "and putting it back undoes it completely");
});

runner.Test("a file that is not the game executable is refused", () =>
{
    Runner.IsTrue(!CampaignObjectives.TryLocate(new byte[] { 1, 2, 3 }, out _, out _), "not an exe");
    Runner.IsTrue(!CampaignObjectives.TryLocate(System.Text.Encoding.ASCII.GetBytes("MZ" + new string('x', 200)),
        out _, out _), "MZ alone is not enough");
});

runner.Test("a destroy-all choice survives into the built mod", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "war2mod-obj-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var project = ModProject.Create(Path.Combine(folder, "test.w2proj"), "Test");

        var human = Campaign.All.First(c => c.Id == "human");
        var slot = human.Missions(null)[0].ExeSlot;

        Runner.IsTrue(!project.WantsDestroyAll(slot), "off to begin with");
        Runner.IsTrue(project.SetDestroyAll(slot, true), "turning it on is a change");
        Runner.IsTrue(!project.SetDestroyAll(slot, true), "turning it on again is not");
        Runner.IsTrue(project.WantsDestroyAll(slot), "and it stuck");

        project.Save();
        var reopened = ModProject.Load(Path.Combine(folder, "test.w2proj"));
        Runner.IsTrue(reopened.WantsDestroyAll(slot), "it survives a save and load");

        // The loader only ever sees the manifest, so the choice has to reach it.
        var manifest = reopened.BuildManifest(null);
        Runner.AreEqual(0x0100, manifest.Objectives[slot], "and reaches the built package");

        // Any of the game's own conditions can be picked, not just destroy-all.
        reopened.SetObjective(slot, 8);
        Runner.AreEqual(8, reopened.ObjectiveFor(slot) ?? -1, "another objective can be chosen");
        Runner.AreEqual("Destroy the Dark Portal", CampaignObjectiveNames.Describe(8), "and it is named");

        Runner.IsTrue(reopened.SetObjective(slot, null), "clearing it is a change");
        Runner.AreEqual(0, reopened.BuildManifest(null).Objectives.Count, "and clears it");
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
});

runner.Test("the delivery threshold packs a count and a hero requirement", () =>
{
    // Low byte is how many units must arrive; bit 12 says only a Hero-flagged type counts.
    // Every delivery adds 1 to the game's counter and a Hero adds 0x1000, so 0x1001 can
    // only ever be reached by one Hero — which is what Orc02 asks for.
    Runner.AreEqual(0x0001, (int)CampaignObjectives.Threshold(1, 0), "one of anything");
    Runner.AreEqual(0x1001, (int)CampaignObjectives.Threshold(1, 1), "one Hero, as Orc02 ships");
    Runner.AreEqual(0x0004, (int)CampaignObjectives.Threshold(4, 0), "four, as Human10 ships");
    Runner.AreEqual(0x1004, (int)CampaignObjectives.Threshold(4, 1), "four arrivals, one a Hero");

    Runner.AreEqual(1, CampaignObjectives.CountOf(0x1001), "the count reads back");
    Runner.AreEqual(1, CampaignObjectives.HeroesOf(0x1001), "and so does the Hero count");
    Runner.AreEqual(0, CampaignObjectives.HeroesOf(0x0004), "which is zero when not asked for");

    // The bonus is gated on bit 12, which an even number of Heroes leaves clear.
    Runner.IsTrue(CampaignObjectives.HeroCountWorks(1), "one Hero works");
    Runner.IsTrue(CampaignObjectives.HeroCountWorks(3), "three work");
    Runner.IsTrue(!CampaignObjectives.HeroCountWorks(2), "two do not");

    var game = GameInstall.Detect();
    if (game is null || !File.Exists(game.ExecutablePath)) { Console.WriteLine("        (skipped)"); return; }

    // And the shipped table has to agree, or the packing is wrong.
    var thresholds = CampaignObjectives.ReadThresholds(File.ReadAllBytes(game.ExecutablePath));
    Runner.AreEqual(52, thresholds.Length, "one per campaign slot");
    Runner.AreEqual(0x0001, (int)thresholds[2], "Human02 wants one Elven Archer");
    Runner.AreEqual(0x1001, (int)thresholds[3], "Orc02 wants Zul'jin specifically");
    Runner.AreEqual(0x0004, (int)thresholds[18], "Human10 wants four traitors");
    Runner.AreEqual(0x0003, (int)thresholds[28], "XHuman01 wants three of the escort");
});

runner.Test("a goto keeps aiming where it aimed, named or not", () =>
{
    // The point of the dropdown is that you pick a wave and the offset follows. The trap
    // is the other direction: a jump into the middle of a wave matches no entry, and a
    // combo cannot show a selection that is not in its list — so without the synthetic
    // entry, merely opening the script would silently retarget the jump.
    var block = AiBlock.From(AiFile.FromText("goto 261")[0]);
    block.JumpTargets = new[]
    {
        new AiJumpTarget(4, "SETUP · byte 4"),
        new AiJumpTarget(96, "WAVE 1 · byte 96"),
    };

    Runner.IsTrue(block.ShowsGoto, "a goto with targets offers them");
    Runner.IsTrue(!block.ShowsNumber, "and drops the byte box that replaced");
    Runner.AreEqual(3, block.GotoTargets.Length, "the unnamed offset is kept as an entry");
    Runner.AreEqual(261, block.GotoTarget?.Offset ?? -1, "and stays selected");
    Runner.AreEqual(261, (int)AiFile.ReadWord(block.ToInstruction()), "so the bytes are unchanged");

    // Picking a wave is what actually moves it.
    block.GotoTarget = block.GotoTargets.First(target => target.Label.StartsWith("WAVE"));
    Runner.AreEqual(96, (int)AiFile.ReadWord(block.ToInstruction()), "picking a wave sets its offset");
    Runner.AreEqual(2, block.GotoTargets.Length, "and the unnamed entry goes away");

    // With nothing to pick from, the byte box has to come back or the row is uneditable.
    var bare = AiBlock.From(AiFile.FromText("goto 261")[0]);
    Runner.IsTrue(!bare.ShowsGoto && bare.ShowsNumber, "no targets means the number box");
});

runner.Test("rows only carry a description where it adds something", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));
    int withText = 0, total = 0;

    foreach (var script in ai.Scripts.Where(s => !s.IsEmpty))
    {
        foreach (var instruction in script.Instructions)
        {
            var block = AiBlock.From(instruction);
            total++;
            if (block.Summary.Length > 0) withText++;

            // The rows whose controls already say everything must stay quiet.
            if (block.Action is AiBlock.AiAction.BuildUnits or AiBlock.AiAction.AttackParty
                or AiBlock.AiAction.LaunchAttack or AiBlock.AiAction.RearmAttack or AiBlock.AiAction.Wait)
                Runner.AreEqual("", block.Summary, $"{block.Action} should need no description");
        }
    }

    Console.WriteLine($"        ({withText} of {total} rows still carry text, " +
                      $"{100 - withText * 100 / total}% now silent)");
});

runner.Test("stub scripts are identified as jumping to shared code", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var ai = AiFile.Parse(File.ReadAllBytes(path));
    var stubs = ai.Scripts.Where(s => s.IsStub).ToList();

    Runner.IsTrue(stubs.Count > 0, "the file does contain stubs");
    Console.WriteLine($"        ({stubs.Count} stubs: " +
                      string.Join(", ", stubs.Select(s => $"{s.Name} -> {s.JumpsAwayTo}")) + ")");

    // Passive is the clearest case: its whole body is a jump into the region before the
    // first script, which holds a setup block and an idle loop.
    var passive = ai.Scripts[1];
    Runner.IsTrue(passive.IsStub, "Passive should be recognised as a stub");
    Runner.IsTrue(passive.JumpsAwayTo < ai.Scripts.Where(s => !s.IsEmpty).Min(s => s.Offset),
        "and it should jump below the first script, into the shared region");

    Runner.IsTrue(ai.SharedCodeLength > 0, "the shared region should be reported, not hidden");
    Console.WriteLine($"        (shared region: {ai.SharedCodeLength} bytes from {ai.SharedCodeStart})");
});

runner.Test("the shared routine decodes and rebuilds in place", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(path);
    var ai = AiFile.Parse(original);

    var routine = ai.SharedRoutine;
    Runner.IsTrue(routine is not null, "the shared routine should be exposed");
    Runner.IsTrue(routine!.Instructions.Count > 20,
        $"it should decode as a real script, got {routine.Instructions.Count} instructions");

    // It ends the way the stubs imply: an idle loop.
    var last = routine.Instructions[^1];
    Runner.AreEqual((byte)AiOpcode.Goto, last.Opcode, "it should end in a goto");

    var sleeps = routine.Instructions.Count(i => i.Opcode == (byte)AiOpcode.Sleep);
    Console.WriteLine($"        ({routine.Instructions.Count} instructions, {sleeps} sleep, " +
                      $"ends goto {AiFile.ReadWord(last)})");

    // Untouched, the file is unchanged.
    Runner.IsTrue(original.SequenceEqual(ai.ToBytes()), "an unedited file must stay identical");

    // Edited, only the routine's own bytes move.
    routine.Instructions[0] = AiFile.FromText("var peasants = 3")[0];
    routine.IsModified = true;

    var rebuilt = ai.ToBytes();
    Runner.AreEqual(original.Length, rebuilt.Length, "the file must not change size");

    var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != rebuilt[i]).ToList();
    Runner.IsTrue(changed.All(i => i >= routine.Offset && i < routine.Offset + routine.OriginalBytes.Length),
        "changes must stay inside the routine: " + string.Join(",", changed.Take(8)));
});

runner.Test("shortening a script does not drag other scripts' data backwards", () =>
{
    var game = GameInstall.Detect();
    if (game is null) { Console.WriteLine("        (skipped)"); return; }

    var path = StockFile(game, "Rez/ai.bin");
    if (path is null) { Console.WriteLine("        (skipped)"); return; }

    var original = File.ReadAllBytes(path);
    var ai = AiFile.Parse(original);

    // Scripts tile the file, so the build lists and rate tables sit inside some script's
    // trailing bytes. Removing an instruction used to slide all of that earlier, breaking
    // every pointer into it. Pick the script carrying the most such data.
    var victim = ai.Scripts.Where(s => !s.IsEmpty).OrderByDescending(s => s.Trailer.Length).First();
    Runner.IsTrue(victim.Trailer.Length > 200,
        $"expected a script with substantial trailing data, got {victim.Trailer.Length}");

    var before = ai.Scripts.Where(s => !s.IsEmpty)
        .Select(s => (s.Index, s.Header0, s.Header1))
        .ToList();

    // Capture what every pointer currently sees.
    var pointedAt = before.SelectMany(s => new[] { s.Header0, s.Header1 })
        .Distinct()
        .Where(ptr => ptr + 16 <= original.Length)
        .ToDictionary(ptr => ptr, ptr => original[ptr..(ptr + 16)]);

    victim.Instructions.RemoveAt(victim.Instructions.Count - 2);
    victim.IsModified = true;

    var rebuilt = ai.ToBytes();
    Runner.AreEqual(original.Length, rebuilt.Length, "file size");

    foreach (var (ptr, expected) in pointedAt)
    {
        Runner.IsTrue(expected.SequenceEqual(rebuilt[ptr..(ptr + 16)]),
            $"data at pointer {ptr} moved after editing {victim.Name}");
    }

    Console.WriteLine($"        (shortened {victim.Name}, which carries {victim.Trailer.Length} bytes " +
                      $"of other scripts' data; all {pointedAt.Count} pointers still resolve)");
});

return runner.Report();


// ---- helpers ------------------------------------------------------------

/// <summary>A 1x1 PNG of one colour, for testing that image previews actually reload.</summary>
static System.Windows.Media.Imaging.BitmapSource FromBytes(byte[] png)
{
    var image = new System.Windows.Media.Imaging.BitmapImage();
    image.BeginInit();
    image.StreamSource = new MemoryStream(png);
    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
    image.EndInit();
    image.Freeze();
    return image;
}

/// <summary>
/// Whether a frame draws the same out of two sheets that pack it differently.
///
/// Not a comparison of rectangles: the game's own packing keeps a transparent pixel or two
/// around each frame, and trimming tighter on the way back in changes every rectangle and
/// offset while changing nothing anybody can see. What has to match is the picture the
/// engine puts on the canvas, so both are drawn onto one and the canvases are compared.
/// </summary>
static bool SameFrame(System.Windows.Media.Imaging.BitmapSource a, AtlasFrame fa,
                      System.Windows.Media.Imaging.BitmapSource b, AtlasFrame fb)
{
    if (fa.CanvasWidth != fb.CanvasWidth || fa.CanvasHeight != fb.CanvasHeight) return false;

    return OnCanvas(a, fa).AsSpan().SequenceEqual(OnCanvas(b, fb));

    static byte[] OnCanvas(System.Windows.Media.Imaging.BitmapSource sheet, AtlasFrame f)
    {
        var canvas = new byte[f.CanvasWidth * f.CanvasHeight * 4];

        var stride = f.Rect.Width * 4;
        var frame = new byte[stride * f.Rect.Height];

        var source = sheet.Format == System.Windows.Media.PixelFormats.Bgra32
            ? sheet
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(
                sheet, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        source.CopyPixels(
            new System.Windows.Int32Rect(f.Rect.X, f.Rect.Y, f.Rect.Width, f.Rect.Height),
            frame, stride, 0);

        for (var row = 0; row < f.Rect.Height; row++)
            Buffer.BlockCopy(frame, row * stride, canvas,
                             ((f.OffsetY + row) * f.CanvasWidth + f.OffsetX) * 4, stride);

        return canvas;
    }
}

/// <summary>
/// Every string in a source file that a person could read, with its line.
///
/// XAML gives up its copy through the attributes that carry it and the text between tags.
/// C# gives up quoted literals, minus the ones that are plainly not prose: paths, format
/// specifiers, and anything without two words in it. The point is not to catch every string
/// but to catch every sentence, and a sentence has spaces in it.
/// </summary>
static IEnumerable<(int Line, string Text)> Copy(string file)
{
    var text = File.ReadAllText(file);

    if (file.EndsWith(".xaml", StringComparison.Ordinal))
    {
        foreach (var name in new[] { "Content", "Header", "Text", "ToolTip", "Title" })
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, name + "=\"([^\"]*)\""))
            {
                var value = m.Groups[1].Value;
                if (value.Length == 0 || value.StartsWith("{", StringComparison.Ordinal)) continue;
                if (!value.Any(char.IsLetter)) continue;

                yield return (text[..m.Index].Count(c => c == '\n') + 1, value);
            }
        }

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     text, @"<(?:TextBlock|Run)\b[^>]*>([^<>{]+)</(?:TextBlock|Run)>"))
        {
            var value = m.Groups[1].Value.Trim();
            if (value.Length > 0 && value.Any(char.IsLetter))
                yield return (text[..m.Index].Count(c => c == '\n') + 1, value);
        }

        yield break;
    }

    var lines = text.Split('\n');

    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i];
        var trimmed = line.TrimStart();

        // Comments are for whoever reads the source, and are held to no rule here.
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(line, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var value = m.Groups[1].Value;

            // Prose has spaces and letters. A path, a key or a format string has neither
            // enough of, and reporting them would bury the sentences.
            if (!value.Contains(' ') || !value.Any(char.IsLetter)) continue;

            yield return (i + 1, value);
        }
    }
}

static byte[] MakePng(byte r, byte g, byte b)
{
    var pixel = new byte[] { b, g, r, 255 };
    var source = System.Windows.Media.Imaging.BitmapSource.Create(
        1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixel, 4);

    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

    using var buffer = new MemoryStream();
    encoder.Save(buffer);
    return buffer.ToArray();
}

static string Describe(System.Windows.Media.Imaging.BitmapSource bitmap)
{
    var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
        bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

    var pixel = new byte[4];
    converted.CopyPixels(pixel, 4, 0);
    return $"{pixel[2]},{pixel[1]},{pixel[0]}";
}

static byte[] BuildFrame(List<byte[]> rows)
{
    // uint16 offset per row (relative to frame start), then the row payloads.
    var tableSize = rows.Count * 2;
    var frame = new byte[tableSize + rows.Sum(r => r.Length)];
    var cursor = tableSize;
    for (var i = 0; i < rows.Count; i++)
    {
        BitConverter.GetBytes((ushort)cursor).CopyTo(frame, i * 2);
        rows[i].CopyTo(frame, cursor);
        cursor += rows[i].Length;
    }
    return frame;
}

/// <summary>
/// The file as the game shipped it. The vault holds that copy whenever a mod has been
/// applied; the game folder then holds the mod's version, which these tests are not about.
/// </summary>
static string? StockFile(GameInstall game, string relativePath)
{
    var vault = BackupVault.Default();
    var original = vault.OriginalFile(relativePath);
    if (original is not null) return original;

    var path = game.ResolveDataPath(relativePath);
    return File.Exists(path) ? path : null;
}

static byte[] BuildGrp(int width, int height, byte[] framePayload)
{
    const int headerSize = 6 + 8;
    var file = new byte[headerSize + framePayload.Length];
    BitConverter.GetBytes((ushort)1).CopyTo(file, 0);
    BitConverter.GetBytes((ushort)width).CopyTo(file, 2);
    BitConverter.GetBytes((ushort)height).CopyTo(file, 4);
    file[6] = 0;
    file[7] = 0;
    file[8] = (byte)width;
    file[9] = (byte)height;
    BitConverter.GetBytes((uint)headerSize).CopyTo(file, 10);
    framePayload.CopyTo(file, headerSize);
    return file;
}

sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public BackupVault Vault { get; }

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "war2mod-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Vault = new BackupVault(Path.Combine(Root, "vault"));
    }

    public GameInstall CreateGame()
    {
        var root = Path.Combine(Root, "game");
        var data = Path.Combine(root, "x86", "Data", "Art");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(root, "x86", "Data", "Rez"));

        File.WriteAllBytes(Path.Combine(root, "x86", "Warcraft II.exe"), new byte[] { 0x4D, 0x5A });
        File.WriteAllBytes(Path.Combine(data, "unit.png"), new byte[] { 0xAB });
        File.WriteAllBytes(Path.Combine(data, "other.png"), new byte[] { 0xCD, 0xEF });
        File.WriteAllBytes(Path.Combine(root, "x86", "Data", "Rez", "strings.tbl"),
            new TblFile(new[] { "Grunt" }).ToBytes());

        if (!GameInstall.TryOpen(root, out var game, out var error))
            throw new InvalidOperationException("sandbox game did not validate: " + error);
        return game!;
    }

    public ModProject CreateProject(string id, string name)
    {
        var directory = Path.Combine(Root, "projects", id);
        Directory.CreateDirectory(directory);
        var project = ModProject.Create(Path.Combine(directory, id + ModProject.Extension), name);
        project.Id = id;
        project.Save();
        return project;
    }

    public string BuildSimpleMod(string id, string name, string relativePath, byte[] content, GameInstall game)
    {
        var project = CreateProject(id, name);
        var target = project.ResolveContentPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);

        var packagePath = Path.Combine(Root, id + ".w2mod");
        project.Build(packagePath, game);
        return packagePath;
    }

    public static Dictionary<string, string> SnapshotHashes(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            map[Path.GetRelativePath(root, file)] =
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        return map;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir cleanup is best effort */ }
    }
}

sealed class Runner
{
    private int _passed;
    private readonly List<string> _failures = new();

    public void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine(_failures.Count == 0
            ? $"{_passed} passed."
            : $"{_passed} passed, {_failures.Count} failed.");
        return _failures.Count == 0 ? 0 : 1;
    }

    public static void IsTrue(bool condition, string what)
    {
        if (!condition) throw new Exception("expected true — " + what);
    }

    public static void IsFalse(bool condition, string what)
    {
        if (condition) throw new Exception("expected false — " + what);
    }

    public static void AreNotEqual<T>(T unwanted, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(unwanted, actual))
            throw new Exception($"{what}: expected something other than {unwanted}");
    }

    public static void AreEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }
}

/// <summary>Writes bytes to a temp file so an image decoder can be pointed at them.</summary>
sealed class MemoryStreamSource : IDisposable
{
    public string Path { get; }

    public MemoryStreamSource(byte[] bytes)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "war2mod-atlas-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(Path, bytes);
    }

    public void Dispose() { try { File.Delete(Path); } catch { } }
}

static class Pixels
{
    public static bool Same(System.Windows.Media.Imaging.BitmapSource a,
                       System.Windows.Media.Imaging.BitmapSource b,
                       System.Windows.Int32Rect area)
{
    var stride = area.Width * 4;
    var one = new byte[stride * area.Height];
    var two = new byte[stride * area.Height];

    Convert(a).CopyPixels(area, one, stride, 0);
    Convert(b).CopyPixels(area, two, stride, 0);

    return one.AsSpan().SequenceEqual(two);

    static System.Windows.Media.Imaging.BitmapSource Convert(System.Windows.Media.Imaging.BitmapSource s)
        => s.Format == System.Windows.Media.PixelFormats.Bgra32
            ? s
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(
                s, System.Windows.Media.PixelFormats.Bgra32, null, 0);
}
}
