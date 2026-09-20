using PRJ2_Extractor.Core;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using System.Threading;

// PrjDiag: validation harness for PRJ2-Extractor's geometry/texture work. Compares our conversion
// of alexhub2.tr4 against the hand-authored ground-truth alexhub2_orig.prj2.
//
// Cleaned up to keep only the reusable regression checks (see DOCUMENTATION.md for the history of
// findings this harness produced along the way). One-off investigation blocks that led to a specific,
// now-documented fix were removed -- restore an old version from git history (`git log -- PrjDiag/Program.cs`)
// if a similar deep-dive needs to be repeated. Always verify brace balance and the trailing `return 0;`
// after editing this file; it has previously suffered real corruption from accumulating ad-hoc blocks.
//
// IMPORTANT: room matching below checks X, Z, AND Y (Position.Y vs -YBottom) -- NOT just X/Z. The
// level has 28 groups of rooms sharing the same X/Z footprint but different Y (vertically stacked
// rooms), and an X/Z-only match silently picks whichever one happens to be first in the reference's
// room list. This was a real, session-spanning bug in this harness (not in TrLevel.cs) that made the
// wall-tier match read artificially low (92.38% -> 94.75% once fixed) by comparing dozens of our
// rooms against the wrong stacked neighbor. Never remove the Y term from these matches.

using var level = new TrLevel();
byte result = level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var prj = level.ConvertToPrj(@"C:\Users\Checkm8ra1n\Documents\alexhub2_test.prj2", saveTga: false);

var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var refLevel = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings);
var refRooms = refLevel.Rooms.Where(r => r != null).ToList();

bool RoomMatches(TombLib.LevelData.Room rr, PRJ2_Extractor.Models.LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 &&
    Math.Abs(rr.Position.Y + r1.YBottom) < 300;

// === 1. Ownership-agnostic wall-tier comparison (PRIMARY METRIC) ===
// Compares presence of a texture at each SEAM (not each sector): does our classification put
// SOMETHING at this seam's QA/Middle/WS tier (on either side), matching whether the reference has
// ANYTHING there (on either side, Negative or Positive)? Isolates tier classification from
// ownership-side choice. This is the headline number quoted throughout DOCUMENTATION.md.
var tiers = new[] { ("QA", SectorFace.Wall_NegativeX_QA, SectorFace.Wall_PositiveX_QA, SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_PositiveZ_QA, 2, 5),
                     ("WS", SectorFace.Wall_NegativeX_WS, SectorFace.Wall_PositiveX_WS, SectorFace.Wall_NegativeZ_WS, SectorFace.Wall_PositiveZ_WS, 3, 6),
                     ("Mid", SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_PositiveZ_Middle, 4, 7) };

int agree = 0, fp = 0, fn = 0, total = 0;
var byTier = new Dictionary<string, (int a, int fp, int fn)>();
foreach (var t in tiers) byTier[t.Item1 + "_X"] = (0, 0, 0);
foreach (var t in tiers) byTier[t.Item1 + "_Z"] = (0, 0, 0);

for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var pr = prj.Rooms[i];
    var refRoom = refRooms.FirstOrDefault(rr => RoomMatches(rr, r1));
    if (refRoom == null) continue;

    // X-seams (between x and x-1)
    for (int x = 1; x < pr.XSize && x < refRoom.NumXSectors; x++)
    for (int z = 0; z < pr.ZSize && z < refRoom.NumZSectors; z++)
    {
        int bOwn = x * pr.ZSize + z;
        if (bOwn >= pr.Blocks.Length) continue;
        foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
        {
            total++;
            bool ours = pr.Blocks[bOwn].Textures[slotX].Tipo == 0x0007;
            bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x - 1, z].GetFaceTextures().ContainsKey(posX);
            var key = name + "_X"; var cur = byTier[key];
            if (ours == theirs) { agree++; byTier[key] = (cur.a + 1, cur.fp, cur.fn); }
            else if (ours) { fp++; byTier[key] = (cur.a, cur.fp + 1, cur.fn); }
            else { fn++; byTier[key] = (cur.a, cur.fp, cur.fn + 1); }
        }
    }
    // Z-seams (between z and z-1)
    for (int x = 0; x < pr.XSize && x < refRoom.NumXSectors; x++)
    for (int z = 1; z < pr.ZSize && z < refRoom.NumZSectors; z++)
    {
        int bOwn = x * pr.ZSize + z;
        if (bOwn >= pr.Blocks.Length) continue;
        foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
        {
            total++;
            bool ours = pr.Blocks[bOwn].Textures[slotZ].Tipo == 0x0007;
            bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z - 1].GetFaceTextures().ContainsKey(posZ);
            var key = name + "_Z"; var cur = byTier[key];
            if (ours == theirs) { agree++; byTier[key] = (cur.a + 1, cur.fp, cur.fn); }
            else if (ours) { fp++; byTier[key] = (cur.a, cur.fp + 1, cur.fn); }
            else { fn++; byTier[key] = (cur.a, cur.fp, cur.fn + 1); }
        }
    }
}

Console.WriteLine($"Total: {total}, Agree: {agree} ({100.0 * agree / total:F2}%), FP: {fp}, FN: {fn}");
Console.WriteLine("--- By tier (ownership-agnostic) ---");
foreach (var kv in byTier)
{
    int t = kv.Value.a + kv.Value.fp + kv.Value.fn;
    Console.WriteLine($"{kv.Key,-8} agree={kv.Value.a,5} fp={kv.Value.fp,4} fn={kv.Value.fn,4} ({100.0 * kv.Value.a / t:F1}%)");
}

// === 2. Per-room error concentration + sloped/flat correlation ===
// Flags which rooms concentrate the most wall-tier errors, and whether errors correlate with
// sloped/diagonal sectors (FloorCorner/CeilCorner != 0) vs flat ones -- a useful starting point for
// any future targeted wall-tier investigation.
{
    var perRoomErr = new List<(int room, int err, int tot, int slopedErr, int flatErr)>();
    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r1 = level.Rooms[i];
        var pr = prj.Rooms[i];
        var refRoom = refRooms.FirstOrDefault(rr => RoomMatches(rr, r1));
        if (refRoom == null) continue;

        int roomErr = 0, roomTot = 0, slopedErr = 0, flatErr = 0;
        bool IsSloped(PRJ2_Extractor.Models.Block b) => b.FloorCorner.Any(c => c != 0) || b.CeilCorner.Any(c => c != 0);

        for (int x = 1; x < pr.XSize && x < refRoom.NumXSectors; x++)
        for (int z = 0; z < pr.ZSize && z < refRoom.NumZSectors; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = (x - 1) * pr.ZSize + z;
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            bool sloped = IsSloped(pr.Blocks[bOwn]) || IsSloped(pr.Blocks[bNeigh]);
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                roomTot++;
                bool ours = pr.Blocks[bOwn].Textures[slotX].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x - 1, z].GetFaceTextures().ContainsKey(posX);
                if (ours != theirs) { roomErr++; if (sloped) slopedErr++; else flatErr++; }
            }
        }
        for (int x = 0; x < pr.XSize && x < refRoom.NumXSectors; x++)
        for (int z = 1; z < pr.ZSize && z < refRoom.NumZSectors; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = x * pr.ZSize + (z - 1);
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            bool sloped = IsSloped(pr.Blocks[bOwn]) || IsSloped(pr.Blocks[bNeigh]);
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                roomTot++;
                bool ours = pr.Blocks[bOwn].Textures[slotZ].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z - 1].GetFaceTextures().ContainsKey(posZ);
                if (ours != theirs) { roomErr++; if (sloped) slopedErr++; else flatErr++; }
            }
        }

        if (roomTot > 0) perRoomErr.Add((i, roomErr, roomTot, slopedErr, flatErr));
    }

    int totalSlopedErr = perRoomErr.Sum(r => r.slopedErr);
    int totalFlatErr = perRoomErr.Sum(r => r.flatErr);
    Console.WriteLine();
    Console.WriteLine("--- Error correlation: sloped/diagonal (FloorCorner or CeilCorner != 0) vs flat sectors ---");
    Console.WriteLine($"Errors on sloped-involved seams: {totalSlopedErr}");
    Console.WriteLine($"Errors on flat seams:            {totalFlatErr}");

    Console.WriteLine();
    Console.WriteLine("--- Top 15 rooms by error count ---");
    foreach (var r in perRoomErr.OrderByDescending(r => r.err).Take(15))
        Console.WriteLine($"Room {r.room,4}: err={r.err,4}/{r.tot,4} ({100.0 * r.err / r.tot:F1}%)  slopedErr={r.slopedErr,4} flatErr={r.flatErr,4}");
}

// === 3. Flat-seam FP/FN split + floor-delta histogram ===
// Restricts to seams with no sloped sectors on either side, to separate "flat geometry logic is
// still wrong somewhere" from "sloped-sector handling is the remaining gap".
{
    int flatFp = 0, flatFn = 0;
    var deltaHisto = new SortedDictionary<int, int>(); // delta in clicks -> count (FN only)
    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r1 = level.Rooms[i];
        var pr = prj.Rooms[i];
        var refRoom = refRooms.FirstOrDefault(rr => RoomMatches(rr, r1));
        if (refRoom == null) continue;
        bool IsSloped(PRJ2_Extractor.Models.Block b) => b.FloorCorner.Any(c => c != 0) || b.CeilCorner.Any(c => c != 0);

        for (int x = 1; x < pr.XSize && x < refRoom.NumXSectors; x++)
        for (int z = 0; z < pr.ZSize && z < refRoom.NumZSectors; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = (x - 1) * pr.ZSize + z;
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            if (IsSloped(pr.Blocks[bOwn]) || IsSloped(pr.Blocks[bNeigh])) continue; // flat only
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotX].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x - 1, z].GetFaceTextures().ContainsKey(posX);
                if (ours == theirs) continue;
                if (ours) { flatFp++; continue; }
                flatFn++;
                int delta = Math.Abs(pr.Blocks[bOwn].Floor - pr.Blocks[bNeigh].Floor);
                deltaHisto.TryGetValue(delta, out int c); deltaHisto[delta] = c + 1;
            }
        }
        for (int x = 0; x < pr.XSize && x < refRoom.NumXSectors; x++)
        for (int z = 1; z < pr.ZSize && z < refRoom.NumZSectors; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = x * pr.ZSize + (z - 1);
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            if (IsSloped(pr.Blocks[bOwn]) || IsSloped(pr.Blocks[bNeigh])) continue; // flat only
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotZ].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z - 1].GetFaceTextures().ContainsKey(posZ);
                if (ours == theirs) continue;
                if (ours) { flatFp++; continue; }
                flatFn++;
                int delta = Math.Abs(pr.Blocks[bOwn].Floor - pr.Blocks[bNeigh].Floor);
                deltaHisto.TryGetValue(delta, out int c); deltaHisto[delta] = c + 1;
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine($"--- Flat-seam errors: FP={flatFp} FN={flatFn} ---");
    Console.WriteLine("FN by |floor delta| in clicks (own vs neighbor, click=256 units):");
    foreach (var kv in deltaHisto.Take(20))
        Console.WriteLine($"  delta={kv.Key,3} clicks: {kv.Value,5}");
}

// === 4. Real export + reload validation ===
// Runs the actual Prj2Exporter.Export path (as a user would), reloads the produced .prj2 with
// TombLib, and checks every exported face-texture entry is valid and inside the compacted atlas --
// catches export-layer bugs the in-memory-model comparisons above can't see.
{
    Console.WriteLine();
    Console.WriteLine("--- Real Prj2Exporter.Export run ---");
    string outPath = @"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2";
    var warnings = Prj2Exporter.Export(level, outPath);
    Console.WriteLine($"Warnings: {warnings.Count}");
    foreach (var w in warnings.Take(20)) Console.WriteLine($"  - {w}");

    var settings2 = new Prj2Loader.Settings();
    var exportedLevel = Prj2Loader.LoadFromPrj2(outPath, null, CancellationToken.None, settings2);

    int allTotal = 0, allDefined = 0, allOutOfAtlas = 0;
    float atlasW = 256f, atlasH = 2048f; // known from the .tga dims at time of writing; adjust if the atlas grows
    for (int i = 0; i < exportedLevel.Rooms.Length; i++)
    {
        var room = exportedLevel.Rooms[i];
        if (room == null) continue;
        for (int x = 0; x < room.NumXSectors; x++)
        for (int z = 0; z < room.NumZSectors; z++)
        foreach (var kv in room.Sectors[x, z].GetFaceTextures())
        {
            allTotal++;
            if (kv.Value.TextureIsUnavailable) continue;
            allDefined++;
            var c = kv.Value.TexCoord0;
            if (c.X < 0 || c.X > atlasW || c.Y < 0 || c.Y > atlasH) allOutOfAtlas++;
        }
    }
    Console.WriteLine($"FULL LEVEL: total face-texture entries={allTotal}, valid={allDefined}, TexCoord0 outside {atlasW}x{atlasH} atlas={allOutOfAtlas}");
}

// === 5. Coverage by SectorFace type: our export vs reference ===
// Full breakdown of how many of each SectorFace type (Floor, Ceiling, all 4x3 wall tiers,
// Floor2/Ceiling2, Triangle2 variants...) we produce vs how many the reference has. The single most
// useful ongoing progress metric for texture-coverage work, since it isolates which specific
// category to look at next.
{
    Console.WriteLine();
    Console.WriteLine("--- Coverage by SectorFace type (ours / reference) ---");
    var settings3 = new Prj2Loader.Settings();
    var outPath2 = @"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2";
    var ourLevel = Prj2Loader.LoadFromPrj2(outPath2, null, CancellationToken.None, settings3);

    var ourByType = new Dictionary<string, int>();
    for (int i = 0; i < ourLevel.Rooms.Length; i++)
    {
        var room = ourLevel.Rooms[i];
        if (room == null) continue;
        for (int x = 0; x < room.NumXSectors; x++)
        for (int z = 0; z < room.NumZSectors; z++)
        foreach (var kv in room.Sectors[x, z].GetFaceTextures())
        {
            if (kv.Value.TextureIsUnavailable) continue;
            var k = kv.Key.ToString();
            ourByType.TryGetValue(k, out int c); ourByType[k] = c + 1;
        }
    }
    var refByType = new Dictionary<string, int>();
    foreach (var room in refRooms)
    for (int x = 0; x < room.NumXSectors; x++)
    for (int z = 0; z < room.NumZSectors; z++)
    foreach (var kv in room.Sectors[x, z].GetFaceTextures())
    {
        var k = kv.Key.ToString();
        refByType.TryGetValue(k, out int c); refByType[k] = c + 1;
    }
    foreach (var k in refByType.Keys.OrderByDescending(k => refByType[k]))
    {
        ourByType.TryGetValue(k, out int ours);
        int refc = refByType[k];
        Console.WriteLine($"  {k,-24}: {ours,5} / {refc,5}  ({100.0 * ours / Math.Max(1, refc):F1}%)");
    }
}

return 0;
