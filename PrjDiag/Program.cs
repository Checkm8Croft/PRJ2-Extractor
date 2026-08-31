using PRJ2_Extractor.Core;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using System.Threading;

using var level = new TrLevel();
byte result = level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var prj = level.ConvertToPrj(@"C:\Users\Checkm8ra1n\Documents\alexhub2_test.prj2", saveTga: false);

var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var refLevel = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings);
var refRooms = refLevel.Rooms.Where(r => r != null).ToList();

// Compare presence of a texture at each SEAM (not each sector): does our classification put SOMETHING
// at this seam's QA/Middle/WS tier (on either side), matching whether the reference has ANYTHING there
// (on either side, Negative or Positive)? This isolates tier classification from ownership-side choice.
var tiers = new[] { ("QA", SectorFace.Wall_NegativeX_QA, SectorFace.Wall_PositiveX_QA, SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_PositiveZ_QA, 2, 5),
                     ("WS", SectorFace.Wall_NegativeX_WS, SectorFace.Wall_PositiveX_WS, SectorFace.Wall_NegativeZ_WS, SectorFace.Wall_PositiveZ_WS, 3, 6),
                     ("Mid", SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_PositiveZ_Middle, 4, 7) };

int agree = 0, fp = 0, fn = 0, total = 0;
var byTier = new Dictionary<string,(int a,int fp,int fn)>();
foreach (var t in tiers) byTier[t.Item1 + "_X"] = (0,0,0);
foreach (var t in tiers) byTier[t.Item1 + "_Z"] = (0,0,0);

for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var pr = prj.Rooms[i];
    var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
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
            bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x-1, z].GetFaceTextures().ContainsKey(posX);
            var key = name + "_X"; var cur = byTier[key];
            if (ours == theirs) { agree++; byTier[key] = (cur.a+1, cur.fp, cur.fn); }
            else if (ours) { fp++; byTier[key] = (cur.a, cur.fp+1, cur.fn); }
            else { fn++; byTier[key] = (cur.a, cur.fp, cur.fn+1); }
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
            bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z-1].GetFaceTextures().ContainsKey(posZ);
            var key = name + "_Z"; var cur = byTier[key];
            if (ours == theirs) { agree++; byTier[key] = (cur.a+1, cur.fp, cur.fn); }
            else if (ours) { fp++; byTier[key] = (cur.a, cur.fp+1, cur.fn); }
            else { fn++; byTier[key] = (cur.a, cur.fp, cur.fn+1); }
        }
    }
}

Console.WriteLine($"Total: {total}, Agree: {agree} ({100.0*agree/total:F2}%), FP: {fp}, FN: {fn}");
Console.WriteLine("--- By tier (ownership-agnostic) ---");
foreach (var kv in byTier)
{
    int t = kv.Value.a + kv.Value.fp + kv.Value.fn;
    Console.WriteLine($"{kv.Key,-8} agree={kv.Value.a,5} fp={kv.Value.fp,4} fn={kv.Value.fn,4} ({100.0*kv.Value.a/t:F1}%)");
}

// --- Extra diagnostics: per-room error concentration + correlation with sloped/diagonal sectors ---
var perRoomErr = new List<(int room, int err, int tot, int slopedErr, int flatErr)>();
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var pr = prj.Rooms[i];
    var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
    if (refRoom == null) continue;

    int roomErr = 0, roomTot = 0, slopedErr = 0, flatErr = 0;

    bool IsSloped(PRJ2_Extractor.Models.Block b) =>
        b.FloorCorner.Any(c => c != 0) || b.CeilCorner.Any(c => c != 0);

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
            bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x-1, z].GetFaceTextures().ContainsKey(posX);
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
            bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z-1].GetFaceTextures().ContainsKey(posZ);
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
    Console.WriteLine($"Room {r.room,4}: err={r.err,4}/{r.tot,4} ({100.0*r.err/r.tot:F1}%)  slopedErr={r.slopedErr,4} flatErr={r.flatErr,4}");

// --- Deep dive: flat-seam errors -- FP vs FN split, and height-delta histogram for FN (micro-step check) ---
{
    int flatFp = 0, flatFn = 0;
    var deltaHisto = new SortedDictionary<int, int>(); // delta in clicks -> count (FN only)
    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r1 = level.Rooms[i];
        var pr = prj.Rooms[i];
        var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
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
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x-1, z].GetFaceTextures().ContainsKey(posX);
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
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z-1].GetFaceTextures().ContainsKey(posZ);
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
    Console.WriteLine("FN by |floor delta| in clicks (own vs neighbor, click=256 units) -- tests micro-step hypothesis:");
    foreach (var kv in deltaHisto.Take(20))
        Console.WriteLine($"  delta={kv.Key,3} clicks: {kv.Value,5}");
}

// --- Probe: for delta=0 flat FN, is own or neighbor a fully-solid wall block (Floor >= Ceiling)? ---
// Also break down by which tier failed and check ceiling delta (floor delta=0 doesn't rule out a
// genuine WS/ceiling-driven face being misfiled here).
{
    var perTier = new Dictionary<string, int>();
    var ceilDeltaHisto = new SortedDictionary<int, int>();
    int delta0Total = 0, delta0SolidOwn = 0, delta0SolidNeighbor = 0, delta0SolidEither = 0, delta0Neither = 0;
    int bothFloorAndCeilEqual = 0;
    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r1 = level.Rooms[i];
        var pr = prj.Rooms[i];
        var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
        if (refRoom == null) continue;
        bool IsSloped(PRJ2_Extractor.Models.Block b) => b.FloorCorner.Any(c => c != 0) || b.CeilCorner.Any(c => c != 0);
        bool IsSolidWall(PRJ2_Extractor.Models.Block b) => b.Floor >= b.Ceiling;

        void Probe(string tierName, PRJ2_Extractor.Models.Block ownBlock, PRJ2_Extractor.Models.Block neighBlock, bool ours, bool theirs)
        {
            if (IsSloped(ownBlock) || IsSloped(neighBlock)) return;
            if (ours == theirs) return;
            if (ours) return; // only interested in FN here
            if (ownBlock.Floor != neighBlock.Floor) return; // delta != 0
            delta0Total++;
            perTier.TryGetValue(tierName, out int tc); perTier[tierName] = tc + 1;
            int cDelta = Math.Abs(ownBlock.Ceiling - neighBlock.Ceiling);
            ceilDeltaHisto.TryGetValue(cDelta, out int cc); ceilDeltaHisto[cDelta] = cc + 1;
            if (cDelta == 0) bothFloorAndCeilEqual++;
            bool so = IsSolidWall(ownBlock), sn = IsSolidWall(neighBlock);
            if (so) delta0SolidOwn++;
            if (sn) delta0SolidNeighbor++;
            if (so || sn) delta0SolidEither++; else delta0Neither++;
        }

        for (int x = 1; x < pr.XSize && x < refRoom.NumXSectors; x++)
        for (int z = 0; z < pr.ZSize && z < refRoom.NumZSectors; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = (x - 1) * pr.ZSize + z;
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotX].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x-1, z].GetFaceTextures().ContainsKey(posX);
                Probe(name, pr.Blocks[bOwn], pr.Blocks[bNeigh], ours, theirs);
            }
        }
        for (int x = 0; x < pr.XSize && x < refRoom.NumXSectors; x++)
        for (int z = 1; z < pr.ZSize && z < refRoom.NumZSectors; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = x * pr.ZSize + (z - 1);
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotZ].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z-1].GetFaceTextures().ContainsKey(posZ);
                Probe(name, pr.Blocks[bOwn], pr.Blocks[bNeigh], ours, theirs);
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine("--- Delta=0 (floor) flat FN breakdown: is own or neighbor a solid wall block (Floor >= Ceiling)? ---");
    Console.WriteLine($"Total delta=0 FN:      {delta0Total}");
    Console.WriteLine($"  own is solid wall:   {delta0SolidOwn}");
    Console.WriteLine($"  neighbor solid wall: {delta0SolidNeighbor}");
    Console.WriteLine($"  either solid:        {delta0SolidEither}");
    Console.WriteLine($"  neither solid:       {delta0Neither}");
    Console.WriteLine($"  BOTH floor AND ceiling equal (own==neighbor on both): {bothFloorAndCeilEqual}");
    Console.WriteLine();
    Console.WriteLine("By tier (floor delta=0):");
    foreach (var kv in perTier.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Key,-6}: {kv.Value}");
    Console.WriteLine();
    Console.WriteLine("Ceiling delta histogram for these same floor-delta=0 FN cases:");
    foreach (var kv in ceilDeltaHisto.Take(20))
        Console.WriteLine($"  cDelta={kv.Key,3} clicks: {kv.Value,5}");
}

// --- Probe: for the 779 same-floor-same-ceiling FN cases, does own or neighbor sector have a
// portal (RoomAbove/RoomBelow != 255 in raw TR4 sector data)? Tests the "vestigial texture under a
// portal, never compiled into geometry" hypothesis. ---
{
    int total2 = 0, ownPortal = 0, neighPortal = 0, eitherPortal = 0, neitherPortal = 0;
    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r1 = level.Rooms[i];
        var pr = prj.Rooms[i];
        var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
        if (refRoom == null) continue;
        bool IsSloped(PRJ2_Extractor.Models.Block b) => b.FloorCorner.Any(c => c != 0) || b.CeilCorner.Any(c => c != 0);
        bool HasPortal(PRJ2_Extractor.Models.LevelSector s) => s.RoomAbove != 255 || s.RoomBelow != 255;

        void Probe(PRJ2_Extractor.Models.Block ownBlock, PRJ2_Extractor.Models.Block neighBlock,
                   PRJ2_Extractor.Models.LevelSector ownSector, PRJ2_Extractor.Models.LevelSector neighSector,
                   bool ours, bool theirs)
        {
            if (IsSloped(ownBlock) || IsSloped(neighBlock)) return;
            if (ours == theirs || ours) return;
            if (ownBlock.Floor != neighBlock.Floor || ownBlock.Ceiling != neighBlock.Ceiling) return;
            total2++;
            bool op = HasPortal(ownSector), np = HasPortal(neighSector);
            if (op) ownPortal++;
            if (np) neighPortal++;
            if (op || np) eitherPortal++; else neitherPortal++;
        }

        for (int x = 1; x < pr.XSize && x < refRoom.NumXSectors && x < r1.NumX; x++)
        for (int z = 0; z < pr.ZSize && z < refRoom.NumZSectors && z < r1.NumZ; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = (x - 1) * pr.ZSize + z;
            int sOwn = x * r1.NumZ + z;
            int sNeigh = (x - 1) * r1.NumZ + z;
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            if (sOwn >= r1.Sectors.Length || sNeigh >= r1.Sectors.Length) continue;
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotX].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x-1, z].GetFaceTextures().ContainsKey(posX);
                Probe(pr.Blocks[bOwn], pr.Blocks[bNeigh], r1.Sectors[sOwn], r1.Sectors[sNeigh], ours, theirs);
            }
        }
        for (int x = 0; x < pr.XSize && x < refRoom.NumXSectors && x < r1.NumX; x++)
        for (int z = 1; z < pr.ZSize && z < refRoom.NumZSectors && z < r1.NumZ; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = x * pr.ZSize + (z - 1);
            int sOwn = x * r1.NumZ + z;
            int sNeigh = x * r1.NumZ + (z - 1);
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            if (sOwn >= r1.Sectors.Length || sNeigh >= r1.Sectors.Length) continue;
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotZ].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z-1].GetFaceTextures().ContainsKey(posZ);
                Probe(pr.Blocks[bOwn], pr.Blocks[bNeigh], r1.Sectors[sOwn], r1.Sectors[sNeigh], ours, theirs);
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine("--- Same-floor-same-ceiling FN: portal correlation (RoomAbove/RoomBelow != 255) ---");
    Console.WriteLine($"Total:          {total2}");
    Console.WriteLine($"  own portal:   {ownPortal}");
    Console.WriteLine($"  neigh portal: {neighPortal}");
    Console.WriteLine($"  either:       {eitherPortal}");
    Console.WriteLine($"  neither:      {neitherPortal}");
}

// --- Deep dive on the 312 residual (no portal, no solid, floor+ceiling equal) FN cases:
// tier breakdown, room-boundary correlation, and FloorData presence (trigger/climb/etc). ---
{
    var perTier2 = new Dictionary<string, int>();
    int atRoomBoundary = 0, notAtBoundary = 0;
    int ownHasFd = 0, neighHasFd = 0, eitherHasFd = 0, neitherHasFd = 0;
    var floorTypesSeen = new Dictionary<string, int>();
    var sampleRooms = new List<(int room, int x, int z, string tier, bool boundary)>();

    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r1 = level.Rooms[i];
        var pr = prj.Rooms[i];
        var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
        if (refRoom == null) continue;
        bool IsSloped(PRJ2_Extractor.Models.Block b) => b.FloorCorner.Any(c => c != 0) || b.CeilCorner.Any(c => c != 0);
        bool HasPortal(PRJ2_Extractor.Models.LevelSector s) => s.RoomAbove != 255 || s.RoomBelow != 255;

        void Probe(int x, int z, bool isXDir, string tierName,
                   PRJ2_Extractor.Models.Block ownBlock, PRJ2_Extractor.Models.Block neighBlock,
                   PRJ2_Extractor.Models.LevelSector ownSector, PRJ2_Extractor.Models.LevelSector neighSector,
                   bool ours, bool theirs)
        {
            if (IsSloped(ownBlock) || IsSloped(neighBlock)) return;
            if (ours == theirs || ours) return;
            if (ownBlock.Floor != neighBlock.Floor || ownBlock.Ceiling != neighBlock.Ceiling) return;
            if (HasPortal(ownSector) || HasPortal(neighSector)) return;

            perTier2.TryGetValue(tierName, out int tc); perTier2[tierName] = tc + 1;

            bool boundary = x == 0 || z == 0 || x == pr.XSize - 1 || z == pr.ZSize - 1;
            if (boundary) atRoomBoundary++; else notAtBoundary++;

            bool ohfd = ownSector.HasFd, nhfd = neighSector.HasFd;
            if (ohfd) ownHasFd++;
            if (nhfd) neighHasFd++;
            if (ohfd || nhfd) eitherHasFd++; else neitherHasFd++;

            foreach (var fdList in new[] { ownSector.FloorInfo, neighSector.FloorInfo })
            {
                if (fdList == null) continue;
                foreach (var fd in fdList)
                {
                    var k = fd.Tipo.ToString();
                    floorTypesSeen.TryGetValue(k, out int fc); floorTypesSeen[k] = fc + 1;
                }
            }

            if (sampleRooms.Count < 25) sampleRooms.Add((i, x, z, tierName, boundary));
        }

        for (int x = 1; x < pr.XSize && x < refRoom.NumXSectors && x < r1.NumX; x++)
        for (int z = 0; z < pr.ZSize && z < refRoom.NumZSectors && z < r1.NumZ; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = (x - 1) * pr.ZSize + z;
            int sOwn = x * r1.NumZ + z;
            int sNeigh = (x - 1) * r1.NumZ + z;
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            if (sOwn >= r1.Sectors.Length || sNeigh >= r1.Sectors.Length) continue;
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotX].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negX) || refRoom.Sectors[x-1, z].GetFaceTextures().ContainsKey(posX);
                Probe(x, z, true, name, pr.Blocks[bOwn], pr.Blocks[bNeigh], r1.Sectors[sOwn], r1.Sectors[sNeigh], ours, theirs);
            }
        }
        for (int x = 0; x < pr.XSize && x < refRoom.NumXSectors && x < r1.NumX; x++)
        for (int z = 1; z < pr.ZSize && z < refRoom.NumZSectors && z < r1.NumZ; z++)
        {
            int bOwn = x * pr.ZSize + z;
            int bNeigh = x * pr.ZSize + (z - 1);
            int sOwn = x * r1.NumZ + z;
            int sNeigh = x * r1.NumZ + (z - 1);
            if (bOwn >= pr.Blocks.Length || bNeigh >= pr.Blocks.Length) continue;
            if (sOwn >= r1.Sectors.Length || sNeigh >= r1.Sectors.Length) continue;
            foreach (var (name, negX, posX, negZ, posZ, slotX, slotZ) in tiers)
            {
                bool ours = pr.Blocks[bOwn].Textures[slotZ].Tipo == 0x0007;
                bool theirs = refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(negZ) || refRoom.Sectors[x, z-1].GetFaceTextures().ContainsKey(posZ);
                Probe(x, z, false, name, pr.Blocks[bOwn], pr.Blocks[bNeigh], r1.Sectors[sOwn], r1.Sectors[sNeigh], ours, theirs);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("--- Residual 312 (no portal/solid, floor+ceiling equal) FN: deep dive ---");
    Console.WriteLine("By tier:");
    foreach (var kv in perTier2.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Key,-6}: {kv.Value}");
    Console.WriteLine($"Room boundary:     {atRoomBoundary}");
    Console.WriteLine($"Not room boundary: {notAtBoundary}");
    Console.WriteLine($"own HasFd:    {ownHasFd}");
    Console.WriteLine($"neigh HasFd:  {neighHasFd}");
    Console.WriteLine($"either HasFd: {eitherHasFd}");
    Console.WriteLine($"neither HasFd:{neitherHasFd}");
    Console.WriteLine("FloorData types seen on own/neighbor sectors (own or neighbor may have multiple functions):");
    foreach (var kv in floorTypesSeen.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Key,-10}: {kv.Value}");
    Console.WriteLine("Sample cases (room, x, z, tier, boundary):");
    foreach (var s in sampleRooms)
        Console.WriteLine($"  room={s.room,4} x={s.x,3} z={s.z,3} tier={s.tier,-6} boundary={s.boundary}");
}

// --- Targeted probe: does room 1, seam x=1/z=3 (X-direction wall) have ANY compiled RoomFace
// (rectangle or triangle) near that seam at all? Dumps raw vertex data if found. ---
{
    var r1 = level.Rooms[1];
    // World-space seam: sector column boundary between block x=0 and x=1 is at local X = 1*1024.
    int seamWorldX = 1 * 1024;
    // Sector z=3 spans local Z in [3*1024, 4*1024].
    int zLo = 3 * 1024, zHi = 4 * 1024;

    Console.WriteLine();
    Console.WriteLine("--- Targeted probe: room 1, seam x=1 (block col 0|1), z=3 ---");
    Console.WriteLine($"Room 1 NumX={r1.NumX} NumZ={r1.NumZ} YBottom={r1.YBottom} YTop={r1.YTop}");
    int idxOwn = 1 * r1.NumZ + 3;
    int idxNeigh = 0 * r1.NumZ + 3;
    if (idxOwn < r1.Sectors.Length && idxNeigh < r1.Sectors.Length)
    {
        var so = r1.Sectors[idxOwn]; var sn = r1.Sectors[idxNeigh];
        Console.WriteLine($"own (1,3):      Floor={so.Floor} Ceiling={so.Ceiling} RoomAbove={so.RoomAbove} RoomBelow={so.RoomBelow} HasFd={so.HasFd}");
        Console.WriteLine($"neighbor (0,3): Floor={sn.Floor} Ceiling={sn.Ceiling} RoomAbove={sn.RoomAbove} RoomBelow={sn.RoomBelow} HasFd={sn.HasFd}");
    }

    int foundCount = 0;
    string FmtVerts(PRJ2_Extractor.Models.RoomVertex[] vs) => string.Join("; ", vs.Select(v => $"({v.X},{v.Y},{v.Z})"));
    void Scan(PRJ2_Extractor.Models.RoomFace[] faces, string kind)
    {
        foreach (var f in faces)
        {
            var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
            int avgX = (int)verts.Average(v => (int)v.X);
            int avgZ = (int)verts.Average(v => (int)v.Z);
            int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
            bool nearSeam = Math.Abs(avgX - seamWorldX) < 64 && Math.Abs(maxX - minX) < 64; // vertical-ish face at the seam
            if (!nearSeam) continue;
            if (avgZ < zLo - 64 || avgZ > zHi + 64) continue;
            foundCount++;
            Console.WriteLine($"  [{kind}] tex={f.Texture} avgX={avgX} avgZ={avgZ} verts=[{FmtVerts(verts)}]");
        }
    }
    Scan(r1.Rectangles, "rect");
    Scan(r1.Triangles, "tri");
    Console.WriteLine($"Total compiled faces found near this seam: {foundCount}");
}

// --- Real export + real-file validation: run the actual Prj2Exporter.Export path (as a user
// would), then reload the produced .prj2 with TombLib and dump actual SetFaceTexture results for
// a handful of sectors, to confirm textures are genuinely present in the file (not just in our
// in-memory model). ---
{
    Console.WriteLine();
    Console.WriteLine("--- Real Prj2Exporter.Export run ---");
    string outPath = @"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2";
    var warnings = Prj2Exporter.Export(level, outPath);
    Console.WriteLine($"Warnings: {warnings.Count}");
    foreach (var w in warnings.Take(20)) Console.WriteLine($"  - {w}");

    var settings2 = new Prj2Loader.Settings();
    var exportedLevel = Prj2Loader.LoadFromPrj2(outPath, null, CancellationToken.None, settings2);
    int fTotal = 0, fDefined = 0;
    var faceTypeCounts = new Dictionary<string, int>();
    for (int i = 0; i < exportedLevel.Rooms.Length && i < 5; i++)
    {
        var room = exportedLevel.Rooms[i];
        if (room == null) continue;
        for (int x = 0; x < room.NumXSectors; x++)
        for (int z = 0; z < room.NumZSectors; z++)
        {
            foreach (var kv in room.Sectors[x, z].GetFaceTextures())
            {
                fTotal++;
                if (kv.Value.TextureIsUnavailable) continue;
                fDefined++;
                var k = kv.Key.ToString();
                faceTypeCounts.TryGetValue(k, out int c); faceTypeCounts[k] = c + 1;
            }
        }
    }
    Console.WriteLine($"Rooms 0-4: total face-texture entries={fTotal}, with valid (non-unavailable) texture={fDefined}");

    // Full-level check: every room, every face texture, TexCoord within the compacted atlas bounds.
    int allTotal = 0, allDefined = 0, allOutOfAtlas = 0;
    float atlasW = 256f, atlasH = 2048f; // known from the .tga dims just verified
    for (int i = 0; i < exportedLevel.Rooms.Length; i++)
    {
        var room = exportedLevel.Rooms[i];
        if (room == null) continue;
        for (int x = 0; x < room.NumXSectors; x++)
        for (int z = 0; z < room.NumZSectors; z++)
        {
            foreach (var kv in room.Sectors[x, z].GetFaceTextures())
            {
                allTotal++;
                if (kv.Value.TextureIsUnavailable) continue;
                allDefined++;
                var c = kv.Value.TexCoord0;
                if (c.X < 0 || c.X > atlasW || c.Y < 0 || c.Y > atlasH) allOutOfAtlas++;
            }
        }
    }
    Console.WriteLine($"FULL LEVEL: total face-texture entries={allTotal}, valid={allDefined}, TexCoord0 outside {atlasW}x{atlasH} atlas={allOutOfAtlas}");
    foreach (var kv in faceTypeCounts.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Key,-24}: {kv.Value}");

    // Dump the specific room-1 seam we investigated earlier, to see actual texture assignment.
    var room1 = exportedLevel.Rooms.Length > 1 ? exportedLevel.Rooms[1] : null;
    if (room1 != null)
    {
        Console.WriteLine();
        Console.WriteLine("Room 1, sector (1,3) face textures (post real export+reload):");
        foreach (var kv in room1.Sectors[1, 3].GetFaceTextures())
            Console.WriteLine($"  {kv.Key}: unavailable={kv.Value.TextureIsUnavailable} texCoord0={kv.Value.TexCoord0}");
    }
}


// --- Diagnose "TEXTURE OUT OF BOUNDS": how many ObjectTextures reference a tile >= NumRoomTextiles
// (i.e. an object-texture tile our .tga atlas never captured), and what is the resulting max Y vs
// actual atlas height? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Texture atlas bounds diagnostic ---");
    Console.WriteLine($"NumRoomTextiles={level.NumRoomTextiles} NumObjTextiles={level.NumObjTextiles} NumBumpTextiles={level.NumBumpTextiles}");
    int atlasHeightPx = level.NumRoomTextiles * 256;
    if (level.NumBumpTextiles > 0) atlasHeightPx += (level.NumBumpTextiles / 2) * 256;
    Console.WriteLine($"Computed atlas height (px): {atlasHeightPx}");
    if (level.TextureBitmap != null)
        Console.WriteLine($"Actual TextureBitmap size: {level.TextureBitmap.PixelWidth}x{level.TextureBitmap.PixelHeight}");

    int overTile = 0, within = 0;
    int maxTileSeen = -1, maxYSeen = -1;
    foreach (var tex in level.ObjectTextures)
    {
        int tile = tex.TileAndFlag & 0x7FFF;
        if (tile > maxTileSeen) maxTileSeen = tile;
        if (tile >= level.NumRoomTextiles) overTile++; else within++;
    }
    Console.WriteLine($"ObjectTextures total={level.ObjectTextures.Length}  tile<NumRoomTextiles={within}  tile>=NumRoomTextiles={overTile}");
    Console.WriteLine($"Max tile index referenced by any ObjectTexture: {maxTileSeen}  (NumRoomTextiles={level.NumRoomTextiles})");

    // Now check specifically which tiles are referenced by actual ROOM FACES (not all ObjectTextures,
    // some of which may be for moveables/statics only).
    var usedByRoomFaces = new HashSet<int>();
    foreach (var room in level.Rooms)
    {
        foreach (var f in room.Rectangles.Concat(room.Triangles))
        {
            int ti = f.Texture & 0x7FFF;
            if (ti >= 0 && ti < level.ObjectTextures.Length)
                usedByRoomFaces.Add(level.ObjectTextures[ti].TileAndFlag & 0x7FFF);
        }
    }
    int roomFacesOverTile = usedByRoomFaces.Count(t => t >= level.NumRoomTextiles);
    Console.WriteLine($"Distinct tiles referenced by ROOM FACES: {usedByRoomFaces.Count}  of which >= NumRoomTextiles: {roomFacesOverTile}");
    if (usedByRoomFaces.Count > 0)
        Console.WriteLine($"Room-face tile range: min={usedByRoomFaces.Min()} max={usedByRoomFaces.Max()}");

    int bmpHeight = level.TextureBitmap?.PixelHeight ?? 0;
    int outOfBoundsCount = 0;
    foreach (var texIdx in level.ObjectTextures.Select((t, idx) => idx))
    {
        var tex = level.ObjectTextures[texIdx];
        int tile = tex.TileAndFlag & 0x7FFF;
        int minY = tex.Vertices.Min(v => v.Y >> 8);
        int computedY = tile * 256 + Math.Clamp(minY, 0, 255);
        if (computedY >= bmpHeight) outOfBoundsCount++;
    }
    Console.WriteLine($"ObjectTextures whose computed atlas Y still exceeds bitmap height ({bmpHeight}px): {outOfBoundsCount} / {level.ObjectTextures.Length}");
}

// --- Investigate the messy geometry cluster near Room0: flip-room overlap? diagonal splits? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Room0 and neighbor investigation ---");
    var r0 = level.Rooms[0];
    Console.WriteLine($"Room0: NumX={r0.NumX} NumZ={r0.NumZ} X={r0.X} Z={r0.Z} YBottom={r0.YBottom} YTop={r0.YTop}");
    Console.WriteLine($"Room0: AltRoom={r0.AltRoom} IsFlipRoom={r0.IsFlipRoom} OriginalRoom={r0.OriginalRoom} AltGroup={r0.AltGroup}");
    Console.WriteLine($"Room0: NumPortals={r0.NumPortals} NumRectangles={r0.Rectangles.Length} NumTriangles={r0.Triangles.Length}");

    var neighborRoomIds = new SortedSet<int>();
    foreach (var po in r0.Portals) neighborRoomIds.Add(po.ToRoom);
    Console.WriteLine($"Room0 portal neighbors: {string.Join(", ", neighborRoomIds)}");

    foreach (var nid in neighborRoomIds)
    {
        if (nid >= level.Rooms.Length) continue;
        var rn = level.Rooms[nid];
        Console.WriteLine($"  Room{nid}: NumX={rn.NumX} NumZ={rn.NumZ} AltRoom={rn.AltRoom} IsFlipRoom={rn.IsFlipRoom} OriginalRoom={rn.OriginalRoom} X={rn.X} Z={rn.Z}");
    }

    // Diagonal split / non-planar sector count for Room0 and neighbors.
    int CountSplitSectors(PRJ2_Extractor.Models.LevelRoom room)
    {
        int c = 0;
        foreach (var s in room.Sectors)
        {
            if (s.FloorInfo == null) continue;
            foreach (var fd in s.FloorInfo)
                if (fd.Tipo is >= PRJ2_Extractor.Models.FloorType.Split1 and <= PRJ2_Extractor.Models.FloorType.Nocol8) { c++; break; }
        }
        return c;
    }
    Console.WriteLine($"Room0 split sectors: {CountSplitSectors(r0)} / {r0.Sectors.Length}");
    foreach (var nid in neighborRoomIds)
    {
        if (nid >= level.Rooms.Length) continue;
        Console.WriteLine($"Room{nid} split sectors: {CountSplitSectors(level.Rooms[nid])} / {level.Rooms[nid].Sectors.Length}");
    }

    // If Room0 has a flip/alt room, is the alt room ALSO being processed/exported (i.e. is our
    // exporter drawing BOTH Room0 and its alt room's geometry in the same physical space)?
    if (r0.AltRoom != -1 && r0.AltRoom < level.Rooms.Length)
    {
        var alt = level.Rooms[r0.AltRoom];
        Console.WriteLine($"Room0's alt room ({r0.AltRoom}): X={alt.X} Z={alt.Z} (same position as Room0? {alt.X == r0.X && alt.Z == r0.Z})");
    }
}

// Room0 and Room3 share X,Z -- check Y overlap and portal relationship in detail.
{
    var r0 = level.Rooms[0];
    var r3 = level.Rooms[3];
    Console.WriteLine();
    Console.WriteLine("--- Room0 vs Room3 (same X,Z) ---");
    Console.WriteLine($"Room0: YBottom={r0.YBottom} YTop={r0.YTop}  X={r0.X} Z={r0.Z}");
    Console.WriteLine($"Room3: YBottom={r3.YBottom} YTop={r3.YTop}  X={r3.X} Z={r3.Z}");
    Console.WriteLine($"Y ranges overlap: {!(r0.YTop >= r3.YBottom || r3.YTop >= r0.YBottom)}");

    Console.WriteLine("Room0 portals:");
    foreach (var po in r0.Portals)
        Console.WriteLine($"  -> Room{po.ToRoom}  Normal=({po.Normal.X},{po.Normal.Y},{po.Normal.Z})");
    Console.WriteLine("Room3 portals:");
    foreach (var po in r3.Portals)
        Console.WriteLine($"  -> Room{po.ToRoom}  Normal=({po.Normal.X},{po.Normal.Y},{po.Normal.Z})");

    // Check ALL rooms for duplicate X,Z pairs -- is this a one-off or a pattern?
    var byPos = level.Rooms.Select((r, idx) => (idx, r.X, r.Z)).GroupBy(t => (t.X, t.Z)).Where(g => g.Count() > 1).ToList();
    Console.WriteLine();
    Console.WriteLine($"Rooms sharing X,Z with another room: {byPos.Sum(g => g.Count())} rooms in {byPos.Count} groups");
    foreach (var g in byPos.Take(15))
        Console.WriteLine($"  X={g.Key.X} Z={g.Key.Z}: rooms [{string.Join(",", g.Select(t => t.idx))}]");
}

// Level-wide scan: flip rooms and diagonal-split usage.
{
    Console.WriteLine();
    Console.WriteLine("--- Level-wide flip room / split scan ---");
    int flipCount = 0, splitRoomCount = 0, totalSplitSectors = 0;
    var splitRoomIds = new List<int>();
    for (int i = 0; i < level.Rooms.Length; i++)
    {
        var r = level.Rooms[i];
        if (r.IsFlipRoom) flipCount++;
        int splits = 0;
        foreach (var s in r.Sectors)
        {
            if (s.FloorInfo == null) continue;
            foreach (var fd in s.FloorInfo)
                if (fd.Tipo is >= PRJ2_Extractor.Models.FloorType.Split1 and <= PRJ2_Extractor.Models.FloorType.Nocol8) { splits++; break; }
        }
        if (splits > 0) { splitRoomCount++; totalSplitSectors += splits; splitRoomIds.Add(i); }
    }
    Console.WriteLine($"Flip rooms: {flipCount} / {level.Rooms.Length}");
    Console.WriteLine($"Rooms with >=1 diagonal-split sector: {splitRoomCount} (total split sectors: {totalSplitSectors})");
    Console.WriteLine($"Split room IDs: {string.Join(",", splitRoomIds.Take(40))}");
}

// 2-hop neighbor BFS from Room0, check split/flip status for each.
{
    Console.WriteLine();
    Console.WriteLine("--- Room0 2-hop neighborhood scan ---");
    var visited = new HashSet<int> { 0 };
    var frontier = new List<int> { 0 };
    for (int hop = 0; hop < 2; hop++)
    {
        var next = new List<int>();
        foreach (var rid in frontier)
        {
            if (rid >= level.Rooms.Length) continue;
            foreach (var po in level.Rooms[rid].Portals)
                if (visited.Add(po.ToRoom)) next.Add(po.ToRoom);
        }
        frontier = next;
    }
    Console.WriteLine($"Rooms within 2 hops of Room0: {visited.Count} -> [{string.Join(",", visited.OrderBy(x => x))}]");

    foreach (var rid in visited.OrderBy(x => x))
    {
        if (rid >= level.Rooms.Length) continue;
        var r = level.Rooms[rid];
        int splits = 0;
        foreach (var s in r.Sectors)
        {
            if (s.FloorInfo == null) continue;
            foreach (var fd in s.FloorInfo)
                if (fd.Tipo is >= PRJ2_Extractor.Models.FloorType.Split1 and <= PRJ2_Extractor.Models.FloorType.Nocol8) { splits++; break; }
        }
        if (splits > 0 || r.IsFlipRoom)
            Console.WriteLine($"  Room{rid}: splits={splits} isFlip={r.IsFlipRoom} NumX={r.NumX} NumZ={r.NumZ} X={r.X} Z={r.Z} Y=[{r.YTop},{r.YBottom}]");
    }
}

// Investigate the specific rooms identified in the messy purple cluster.
{
    Console.WriteLine();
    Console.WriteLine("--- Specific room investigation: 16,17,21,58,61 ---");
    foreach (var rid in new[] { 16, 17, 21, 58, 61 })
    {
        if (rid >= level.Rooms.Length) { Console.WriteLine($"Room{rid}: out of range"); continue; }
        var r = level.Rooms[rid];
        int splits = 0;
        var splitTypes = new List<string>();
        foreach (var s in r.Sectors)
        {
            if (s.FloorInfo == null) continue;
            foreach (var fd in s.FloorInfo)
                if (fd.Tipo is >= PRJ2_Extractor.Models.FloorType.Split1 and <= PRJ2_Extractor.Models.FloorType.Nocol8)
                { splits++; splitTypes.Add(fd.Tipo.ToString()); break; }
        }
        Console.WriteLine($"Room{rid}: NumX={r.NumX} NumZ={r.NumZ} X={r.X} Z={r.Z} Y=[{r.YTop},{r.YBottom}] IsFlip={r.IsFlipRoom} AltRoom={r.AltRoom} splits={splits} [{string.Join(",", splitTypes.Distinct())}] NumPortals={r.NumPortals} NumRect={r.Rectangles.Length} NumTri={r.Triangles.Length}");
        var neighbors = r.Portals.Select(po => po.ToRoom).Distinct();
        Console.WriteLine($"  portal neighbors: {string.Join(",", neighbors)}");
    }
}

// Check NewFlags bump-mapping bits (9-10) for ObjectTextures referencing tile >= NumRoomTextiles+NumObjTextiles.
{
    Console.WriteLine();
    Console.WriteLine("--- Bump-flag check on high-tile ObjectTextures ---");
    int maxDiffuse = level.NumRoomTextiles + level.NumObjTextiles;
    Console.WriteLine($"maxDiffuse (NumRoomTextiles+NumObjTextiles) = {maxDiffuse}");
    var highTileTexIndices = new List<int>();
    for (int i = 0; i < level.ObjectTextures.Length; i++)
    {
        int tile = level.ObjectTextures[i].TileAndFlag & 0x7FFF;
        if (tile >= maxDiffuse) highTileTexIndices.Add(i);
    }
    Console.WriteLine($"ObjectTextures with tile >= maxDiffuse: {highTileTexIndices.Count}");
    foreach (var idx in highTileTexIndices.Take(15))
    {
        var t = level.ObjectTextures[idx];
        int tile = t.TileAndFlag & 0x7FFF;
        int bumpLevel = (t.NewFlags >> 9) & 0x3;
        Console.WriteLine($"  ObjTex[{idx}]: tile={tile} NewFlags=0x{t.NewFlags:X4} bumpLevel={bumpLevel} Attribute={t.Attribute}");
    }
}

// Dump the raw "bump region" of TextureBitmap as PNG for visual inspection.
{
    Console.WriteLine();
    Console.WriteLine("--- Dumping bump-region pixels as PNG ---");
    int maxDiffuse = level.NumRoomTextiles + level.NumObjTextiles;
    int bumpRows = level.TextureBitmap.PixelHeight - maxDiffuse * 256;
    Console.WriteLine($"Bump region: rows [{maxDiffuse * 256}, {level.TextureBitmap.PixelHeight}) = {bumpRows} rows tall, {bumpRows / 256.0:F2} tiles");

    if (bumpRows > 0)
    {
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(level.TextureBitmap,
            new System.Windows.Int32Rect(0, maxDiffuse * 256, 256, bumpRows));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cropped));
        using var fs = new System.IO.FileStream(@"C:\Users\Checkm8ra1n\Documents\bump_region_dump.png", System.IO.FileMode.Create);
        encoder.Save(fs);
        Console.WriteLine("Saved: C:\\Users\\Checkm8ra1n\\Documents\\bump_region_dump.png");
    }

    // Also dump the full atlas (room+obj+bump) for reference / comparison.
    var encoder2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(level.TextureBitmap));
    using var fs2 = new System.IO.FileStream(@"C:\Users\Checkm8ra1n\Documents\full_atlas_dump.png", System.IO.FileMode.Create);
    encoder2.Save(fs2);
    Console.WriteLine("Saved: C:\\Users\\Checkm8ra1n\\Documents\\full_atlas_dump.png");
}

// Check actual Right/Bottom (width/height) values for room-face textures -- should be ~63-64 for
// a full 4-click (1024 unit) face at standard 16px/click density; if they cluster much smaller
// (e.g. ~12-13), that confirms a UV width/height computation bug.
{
    Console.WriteLine();
    Console.WriteLine("--- TexInfo Right/Bottom sanity check for room-face textures ---");
    var usedTexIndices = new SortedSet<int>();
    foreach (var room in level.Rooms)
    {
        foreach (var f in room.Rectangles.Concat(room.Triangles))
        {
            int ti = f.Texture & 0x7FFF;
            if (ti >= 0 && ti < level.ObjectTextures.Length) usedTexIndices.Add(ti);
        }
    }
    var prj2 = level.ConvertToPrj(@"C:\Users\Checkm8ra1n\Documents\alexhub2_dummy.prj2", saveTga: false);
    var rightHisto = new SortedDictionary<int,int>();
    var bottomHisto = new SortedDictionary<int,int>();
    int outOfTableCount = 0;
    foreach (var ti in usedTexIndices)
    {
        if (ti >= prj2.Textures.Length) { outOfTableCount++; continue; }
        var t = prj2.Textures[ti];
        rightHisto.TryGetValue(t.Right, out int rc); rightHisto[t.Right] = rc + 1;
        bottomHisto.TryGetValue(t.Bottom, out int bc); bottomHisto[t.Bottom] = bc + 1;
    }
    Console.WriteLine($"NumTextures in prj2.Textures table: {prj2.Textures.Length} (ObjectTextures.Length={level.ObjectTextures.Length})");
    Console.WriteLine($"Room-face texture indices BEYOND the 1024-entry table: {outOfTableCount} / {usedTexIndices.Count}");
    Console.WriteLine($"Distinct texture indices used by room faces: {usedTexIndices.Count}");
    Console.WriteLine("Right (width) histogram:");
    foreach (var kv in rightHisto.Take(20)) Console.WriteLine($"  Right={kv.Key,3}: {kv.Value}");
    Console.WriteLine("Bottom (height) histogram:");
    foreach (var kv in bottomHisto.Take(20)) Console.WriteLine($"  Bottom={kv.Key,3}: {kv.Value}");

    // Sample raw ObjectTexture vertex data for a couple of room-face textures.
    Console.WriteLine();
    Console.WriteLine("Sample raw ObjectTexture vertices for first 5 room-face texture indices:");
    foreach (var ti in usedTexIndices.Take(5))
    {
        var ot = level.ObjectTextures[ti];
        Console.WriteLine($"  ObjTex[{ti}]: tile={ot.TileAndFlag & 0x7FFF}");
        foreach (var v in ot.Vertices)
            Console.WriteLine($"    raw X=0x{v.X:X4} ({v.X}) -> hiByte={v.X>>8} loByte={v.X&0xFF} | raw Y=0x{v.Y:X4} ({v.Y}) -> hiByte={v.Y>>8} loByte={v.Y&0xFF}");
    }
}

// Quantify empty (untextured) sector faces across the whole exported level vs the reference.
{
    Console.WriteLine();
    Console.WriteLine("--- Empty-face quantification: our export vs reference ---");
    var settings3 = new Prj2Loader.Settings();
    var outPath2 = @"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2";
    var ourLevel = Prj2Loader.LoadFromPrj2(outPath2, null, CancellationToken.None, settings3);

    int ourTotalFaces = 0, ourTexturedFaces = 0;
    for (int i = 0; i < ourLevel.Rooms.Length; i++)
    {
        var room = ourLevel.Rooms[i];
        if (room == null) continue;
        for (int x = 0; x < room.NumXSectors; x++)
        for (int z = 0; z < room.NumZSectors; z++)
        {
            foreach (SectorFace face in room.Sectors[x, z].GetFaceTextures().Keys.ToList())
            {
                ourTotalFaces++;
                if (!room.Sectors[x, z].GetFaceTextures()[face].TextureIsUnavailable) ourTexturedFaces++;
            }
        }
    }

    int refTotalFaces = 0;
    for (int i = 0; i < refRooms.Count; i++)
    {
        var room = refRooms[i];
        for (int x = 0; x < room.NumXSectors; x++)
        for (int z = 0; z < room.NumZSectors; z++)
            refTotalFaces += room.Sectors[x, z].GetFaceTextures().Count;
    }

    Console.WriteLine($"Our export: {ourTexturedFaces} textured / {ourTotalFaces} sector-face entries with SOME texture-having geometry");
    Console.WriteLine($"Reference (orig): {refTotalFaces} sector-face texture entries total");
    Console.WriteLine($"Coverage ratio: {100.0 * ourTexturedFaces / Math.Max(1, refTotalFaces):F1}% of reference's face count is textured in ours");

    // Breakdown by SectorFace type: ours (textured count) vs reference (total count).
    var ourByType = new Dictionary<string,int>();
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
    var refByType = new Dictionary<string,int>();
    foreach (var room in refRooms)
    for (int x = 0; x < room.NumXSectors; x++)
    for (int z = 0; z < room.NumZSectors; z++)
    foreach (var kv in room.Sectors[x, z].GetFaceTextures())
    {
        var k = kv.Key.ToString();
        refByType.TryGetValue(k, out int c); refByType[k] = c + 1;
    }
    Console.WriteLine();
    Console.WriteLine("--- Coverage by SectorFace type (ours / reference) ---");
    foreach (var k in refByType.Keys.OrderByDescending(k => refByType[k]))
    {
        ourByType.TryGetValue(k, out int ours);
        int refc = refByType[k];
        Console.WriteLine($"  {k,-24}: {ours,5} / {refc,5}  ({100.0*ours/Math.Max(1,refc):F1}%)");
    }
}

// Investigate Floor2/Ceiling2: find real occurrences in the reference and inspect the corresponding
// raw TR4 sector/FloorData to see if there's a derivable signal we're not parsing.
{
    Console.WriteLine();
    Console.WriteLine("--- Floor2/Ceiling2 investigation ---");
    var floor2Faces = new HashSet<SectorFace> {
        SectorFace.Wall_NegativeX_Floor2, SectorFace.Wall_PositiveX_Floor2,
        SectorFace.Wall_NegativeZ_Floor2, SectorFace.Wall_PositiveZ_Floor2,
        SectorFace.Wall_NegativeX_Ceiling2, SectorFace.Wall_PositiveX_Ceiling2,
        SectorFace.Wall_NegativeZ_Ceiling2, SectorFace.Wall_PositiveZ_Ceiling2,
    };

    int found = 0;
    for (int ri = 0; ri < refRooms.Count && found < 8; ri++)
    {
        var refRoom = refRooms[ri];
        var r1 = level.Rooms.FirstOrDefault(rr => Math.Abs(rr.X - refRoom.Position.X * 1024) < 1100 && Math.Abs(rr.Z - refRoom.Position.Z * 1024) < 1100);
        if (r1 == null) continue;
        int roomIdx = Array.IndexOf(level.Rooms, r1);

        for (int x = 0; x < refRoom.NumXSectors && found < 8; x++)
        for (int z = 0; z < refRoom.NumZSectors && found < 8; z++)
        {
            var faces = refRoom.Sectors[x, z].GetFaceTextures();
            foreach (var face in faces.Keys)
            {
                if (!floor2Faces.Contains(face)) continue;
                found++;
                Console.WriteLine();
                Console.WriteLine($"Found {face} at refRoom[{ri}] (our room idx {roomIdx}) sector ({x},{z})");

                int idx = x * r1.NumZ + z;
                if (idx < 0 || idx >= r1.Sectors.Length) { Console.WriteLine("  (sector index out of range in our raw data)"); continue; }
                var sec = r1.Sectors[idx];
                Console.WriteLine($"  Raw sector: Floor={sec.Floor} Ceiling={sec.Ceiling} RoomAbove={sec.RoomAbove} RoomBelow={sec.RoomBelow} HasFd={sec.HasFd}");
                if (sec.FloorInfo != null)
                {
                    foreach (var fd in sec.FloorInfo)
                    {
                        Console.WriteLine($"    FloorData: Tipo={fd.Tipo} TriHLo={fd.TriHLo} TriHHi={fd.TriHHi} Corners=[{string.Join(",", fd.Corners)}] AddX={fd.AddX} AddZ={fd.AddZ}");
                    }
                }
                else
                {
                    Console.WriteLine("    (no FloorData at all)");
                }

                // Also check the sector on the relevant side (neighbor) since Floor2/Ceiling2 is a
                // wall-tier face, same as QA/Middle/WS -- print immediate neighbors' raw data too.
                foreach (var (dx, dz, label) in new[] { (-1,0,"x-1"), (1,0,"x+1"), (0,-1,"z-1"), (0,1,"z+1") })
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= r1.NumX || nz >= r1.NumZ) continue;
                    int nidx = nx * r1.NumZ + nz;
                    var nsec = r1.Sectors[nidx];
                    Console.WriteLine($"  Neighbor {label} ({nx},{nz}): Floor={nsec.Floor} Ceiling={nsec.Ceiling} HasFd={nsec.HasFd}");
                    if (nsec.FloorInfo != null)
                        foreach (var fd in nsec.FloorInfo)
                            Console.WriteLine($"      FloorData: Tipo={fd.Tipo} TriHLo={fd.TriHLo} TriHHi={fd.TriHHi} Corners=[{string.Join(",", fd.Corners)}]");
                }
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine($"Total Floor2/Ceiling2 instances inspected: {found}");
}

// Test hypothesis: Floor2/Ceiling2 correspond to EXTRA real compiled quads at a seam (beyond what
// our 3-tier QA/Middle/WS classification captures), not hidden FloorData.
{
    Console.WriteLine();
    Console.WriteLine("--- Floor2/Ceiling2 vs real compiled quad count at the seam ---");
    var floor2Faces = new HashSet<SectorFace> {
        SectorFace.Wall_NegativeX_Floor2, SectorFace.Wall_PositiveX_Floor2,
        SectorFace.Wall_NegativeZ_Floor2, SectorFace.Wall_PositiveZ_Floor2,
        SectorFace.Wall_NegativeX_Ceiling2, SectorFace.Wall_PositiveX_Ceiling2,
        SectorFace.Wall_NegativeZ_Ceiling2, SectorFace.Wall_PositiveZ_Ceiling2,
    };

    int found = 0;
    var quadCounts = new List<int>();
    for (int ri = 0; ri < refRooms.Count; ri++)
    {
        var refRoom = refRooms[ri];
        var r1 = level.Rooms.FirstOrDefault(rr => Math.Abs(rr.X - refRoom.Position.X * 1024) < 1100 && Math.Abs(rr.Z - refRoom.Position.Z * 1024) < 1100);
        if (r1 == null) continue;

        for (int x = 0; x < refRoom.NumXSectors; x++)
        for (int z = 0; z < refRoom.NumZSectors; z++)
        {
            var faces = refRoom.Sectors[x, z].GetFaceTextures();
            bool hasF2C2 = faces.Keys.Any(floor2Faces.Contains);
            if (!hasF2C2) continue;
            found++;

            // Count real compiled quads (rectangles+triangles) near this seam, X-direction (seam
            // between block x-1 and x, at world X = x*1024 relative to room origin).
            int seamWorldX = x * 1024;
            int zLo = z * 1024, zHi = (z + 1) * 1024;
            int count = 0;
            foreach (var f in r1.Rectangles.Concat(r1.Triangles))
            {
                var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
                int avgX = (int)verts.Average(v => (int)v.X);
                int avgZ = (int)verts.Average(v => (int)v.Z);
                int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
                bool nearSeam = Math.Abs(avgX - seamWorldX) < 64 && Math.Abs(maxX - minX) < 64;
                if (nearSeam && avgZ >= zLo - 64 && avgZ <= zHi + 64) count++;
            }
            quadCounts.Add(count);
            if (found <= 15)
                Console.WriteLine($"  refRoom[{ri}] sector({x},{z}): real compiled quads near X-seam = {count}");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"Total Floor2/Ceiling2-bearing sectors checked: {found}");
    if (quadCounts.Count > 0)
    {
        var histo = quadCounts.GroupBy(c => c).OrderBy(g => g.Key);
        Console.WriteLine("Quad-count histogram:");
        foreach (var g in histo) Console.WriteLine($"  {g.Key} quads: {g.Count()} sectors");
    }
}

// Investigate: for border/solid-neighbor walls (the "unreliable neighbor" case), how many REAL
// compiled quads typically exist at the seam? If mostly 2-3, a shape-aware split (using real quad
// boundaries) could beat the fixed "sixth" heuristic without needing Floor2/Ceiling2 at all.
{
    Console.WriteLine();
    Console.WriteLine("--- Real compiled quad count at ALL border/solid-neighbor wall seams ---");
    bool IsBorderOrSolidLocal(PRJ2_Extractor.Models.LevelRoom room, int x, int z)
    {
        if (x < 0 || z < 0 || x >= room.NumX || z >= room.NumZ) return true;
        if (x == 0 || x == room.NumX - 1 || z == 0 || z == room.NumZ - 1) return true;
        int idx = x * room.NumZ + z;
        if (idx < 0 || idx >= room.Sectors.Length) return true;
        return room.Sectors[idx].Floor == -127;
    }

    var quadCounts = new List<int>();
    int checkedSeams = 0;
    for (int ri = 0; ri < level.Rooms.Length; ri++)
    {
        var r1 = level.Rooms[ri];
        for (int x = 1; x < r1.NumX; x++)
        for (int z = 0; z < r1.NumZ; z++)
        {
            if (!IsBorderOrSolidLocal(r1, x - 1, z)) continue; // only the "unreliable neighbor" case
            checkedSeams++;

            int seamWorldX = x * 1024;
            int zLo = z * 1024, zHi = (z + 1) * 1024;
            int count = 0;
            foreach (var f in r1.Rectangles.Concat(r1.Triangles))
            {
                var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
                int avgX = (int)verts.Average(v => (int)v.X);
                int avgZ = (int)verts.Average(v => (int)v.Z);
                int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
                bool nearSeam = Math.Abs(avgX - seamWorldX) < 64 && Math.Abs(maxX - minX) < 64;
                if (nearSeam && avgZ >= zLo - 64 && avgZ <= zHi + 64) count++;
            }
            if (count > 0) quadCounts.Add(count);
        }
    }
    Console.WriteLine($"Border/solid-neighbor X-direction seams checked: {checkedSeams}");
    Console.WriteLine($"...of which have 1+ real compiled quads: {quadCounts.Count}");
    var histo = quadCounts.GroupBy(c => c).OrderBy(g => g.Key);
    Console.WriteLine("Quad-count histogram (seams with real geometry only):");
    foreach (var g in histo) Console.WriteLine($"  {g.Key} quads: {g.Count()} seams");
    Console.WriteLine($"Seams with <=3 quads (fit cleanly in QA/Middle/WS): {quadCounts.Count(c => c <= 3)} / {quadCounts.Count} ({100.0*quadCounts.Count(c=>c<=3)/Math.Max(1,quadCounts.Count):F1}%)");
}

// --- Floor coverage investigation: for reference sectors with a real Floor texture, do we have
// a real compiled floor quad at that sector, and if so why didn't we assign it? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Floor coverage investigation ---");
    int totalMissing = 0, hasRealQuadButMissing = 0, noRealQuadAtAll = 0, ourSlotWasSet = 0;
    var sampleMissingWithQuad = new List<(int room, int x, int z)>();

    for (int ri = 0; ri < refRooms.Count; ri++)
    {
        var refRoom = refRooms[ri];
        var r1 = level.Rooms.FirstOrDefault(rr => Math.Abs(rr.X - refRoom.Position.X * 1024) < 1100 && Math.Abs(rr.Z - refRoom.Position.Z * 1024) < 1100);
        if (r1 == null) continue;
        int roomIdx = Array.IndexOf(level.Rooms, r1);
        var pr = prj.Rooms[roomIdx];

        for (int x = 0; x < refRoom.NumXSectors && x < pr.XSize; x++)
        for (int z = 0; z < refRoom.NumZSectors && z < pr.ZSize; z++)
        {
            if (!refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(SectorFace.Floor)) continue;

            int target = x * pr.ZSize + z;
            if (target < 0 || target >= pr.Blocks.Length) continue;
            bool ourSlotSet = pr.Blocks[target].Textures[0].Tipo == 0x0007;
            if (ourSlotSet) { ourSlotWasSet++; continue; }

            totalMissing++;

            // Does a real compiled floor quad exist near this sector's world position?
            int worldXlo = x * 1024, worldXhi = (x + 1) * 1024;
            int worldZlo = z * 1024, worldZhi = (z + 1) * 1024;
            bool foundQuad = false;
            foreach (var f in r1.Rectangles.Concat(r1.Triangles))
            {
                var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
                int minY = verts.Min(v => (int)v.Y), maxY = verts.Max(v => (int)v.Y);
                if (Math.Abs(maxY - minY) > 8) continue; // not flat -> not floor/ceiling
                int avgX = (int)verts.Average(v => (int)v.X);
                int avgZ = (int)verts.Average(v => (int)v.Z);
                if (avgX >= worldXlo - 64 && avgX <= worldXhi + 64 && avgZ >= worldZlo - 64 && avgZ <= worldZhi + 64)
                { foundQuad = true; break; }
            }
            if (foundQuad) { hasRealQuadButMissing++; if (sampleMissingWithQuad.Count < 10) sampleMissingWithQuad.Add((roomIdx, x, z)); }
            else noRealQuadAtAll++;
        }
    }

    Console.WriteLine($"Reference sectors with a Floor texture: {ourSlotWasSet + totalMissing}");
    Console.WriteLine($"  our slot already set: {ourSlotWasSet}");
    Console.WriteLine($"  missing entirely: {totalMissing}");
    Console.WriteLine($"    of which HAVE a real compiled flat quad nearby (should be fixable): {hasRealQuadButMissing}");
    Console.WriteLine($"    of which have NO real compiled quad at all (likely unrecoverable): {noRealQuadAtAll}");
    Console.WriteLine("Sample fixable cases (room, x, z):");
    foreach (var s in sampleMissingWithQuad) Console.WriteLine($"  room={s.room} x={s.x} z={s.z}");
}

// --- Deep dive: why does room=2 x=1 z=7 (a "fixable" Floor case) not get classified? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Deep dive: room2 sector(1,7) floor classification ---");
    var r1 = level.Rooms[2];
    var pr = prj.Rooms[2];
    int target = 1 * pr.ZSize + 7;
    var block = pr.Blocks[target];
    Console.WriteLine($"Block.Floor={block.Floor} Block.Ceiling={block.Ceiling}");
    Console.WriteLine($"Computed floorY={-block.Floor*256} ceilingY={-block.Ceiling*256}");
    Console.WriteLine($"Floor slot[0] Tipo={block.Textures[0].Tipo:X4}  Ceiling slot[1] Tipo={block.Textures[1].Tipo:X4}");

    int worldXlo = 1 * 1024, worldXhi = 2 * 1024;
    int worldZlo = 7 * 1024, worldZhi = 8 * 1024;
    foreach (var f in r1.Rectangles.Concat(r1.Triangles))
    {
        var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
        int minY = verts.Min(v => (int)v.Y), maxY = verts.Max(v => (int)v.Y);
        if (Math.Abs(maxY - minY) > 8) continue;
        int avgX = (int)verts.Average(v => (int)v.X);
        int avgZ = (int)verts.Average(v => (int)v.Z);
        int avgY = (int)verts.Average(v => (int)v.Y);
        int minX = verts.Min(v=>(int)v.X), maxX = verts.Max(v=>(int)v.X);
        int minZ = verts.Min(v=>(int)v.Z), maxZ = verts.Max(v=>(int)v.Z);
        if (avgX >= worldXlo - 64 && avgX <= worldXhi + 64 && avgZ >= worldZlo - 64 && avgZ <= worldZhi + 64)
        {
            Console.WriteLine($"  Real quad: avgX={avgX} avgZ={avgZ} avgY={avgY}  X=[{minX},{maxX}] Z=[{minZ},{maxZ}]  tex={f.Texture} isTriangle={f.IsTriangle}");
        }
    }
}

// --- Check if the flat floor triangle at room2 (1,7) has a sibling triangle completing the quad ---
{
    Console.WriteLine();
    Console.WriteLine("--- room2 sector(1,7): all flat triangles/rects covering this exact sector ---");
    var r1 = level.Rooms[2];
    int worldXlo = 1 * 1024, worldXhi = 2 * 1024;
    int worldZlo = 7 * 1024, worldZhi = 8 * 1024;
    foreach (var f in r1.Rectangles.Concat(r1.Triangles))
    {
        var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
        int minY = verts.Min(v => (int)v.Y), maxY = verts.Max(v => (int)v.Y);
        if (Math.Abs(maxY - minY) > 8) continue;
        int minX = verts.Min(v=>(int)v.X), maxX = verts.Max(v=>(int)v.X);
        int minZ = verts.Min(v=>(int)v.Z), maxZ = verts.Max(v=>(int)v.Z);
        // exact overlap with this sector's bounding box
        if (maxX < worldXlo || minX > worldXhi || maxZ < worldZlo || minZ > worldZhi) continue;
        int avgY = (int)verts.Average(v => (int)v.Y);
        Console.WriteLine($"  {(f.IsTriangle ? "TRI" : "RECT")} tex={f.Texture} avgY={avgY} X=[{minX},{maxX}] Z=[{minZ},{maxZ}] verts=[{string.Join(";", verts.Select(v=>$"({v.X},{v.Z})"))}]");
    }
}

// --- Debug why Floor_Triangle2 dropped to 0: find a reference sector with Floor_Triangle2 and
// inspect the real compiled triangles there. ---
{
    Console.WriteLine();
    Console.WriteLine("--- Floor_Triangle2 zero-coverage debug ---");
    int found = 0;
    for (int ri = 0; ri < refRooms.Count && found < 3; ri++)
    {
        var refRoom = refRooms[ri];
        var r1 = level.Rooms.FirstOrDefault(rr => Math.Abs(rr.X - refRoom.Position.X * 1024) < 1100 && Math.Abs(rr.Z - refRoom.Position.Z * 1024) < 1100);
        if (r1 == null) continue;
        int roomIdx = Array.IndexOf(level.Rooms, r1);

        for (int x = 0; x < refRoom.NumXSectors && found < 3; x++)
        for (int z = 0; z < refRoom.NumZSectors && found < 3; z++)
        {
            if (!refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(SectorFace.Floor_Triangle2)) continue;
            found++;
            Console.WriteLine();
            Console.WriteLine($"refRoom[{ri}] (our room {roomIdx}) sector({x},{z}) has Floor_Triangle2 in reference");

            int worldXlo = x * 1024, worldXhi = (x + 1) * 1024;
            int worldZlo = z * 1024, worldZhi = (z + 1) * 1024;
            foreach (var f in r1.Rectangles.Concat(r1.Triangles))
            {
                var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
                int minY = verts.Min(v => (int)v.Y), maxY = verts.Max(v => (int)v.Y);
                int minX = verts.Min(v=>(int)v.X), maxX = verts.Max(v=>(int)v.X);
                int minZ = verts.Min(v=>(int)v.Z), maxZ = verts.Max(v=>(int)v.Z);
                if (maxX < worldXlo || minX > worldXhi || maxZ < worldZlo || minZ > worldZhi) continue;
                Console.WriteLine($"  {(f.IsTriangle ? "TRI" : "RECT")} tex={f.Texture} Yrange=[{minY},{maxY}] X=[{minX},{maxX}] Z=[{minZ},{maxZ}]");
            }
        }
    }
}

// --- Investigate "texture larger than 64x64": how many floor/ceiling faces span multiple sectors
// via BlockRange, and what does their TexInfo UV size look like? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Oversized texture investigation: floor/ceiling faces spanning multiple sectors ---");
    int singleSector = 0, multiSector = 0;
    var multiSectorSamples = new List<(int room, int minX, int maxX, int minZ, int maxZ, int texRight, int texBottom)>();

    for (int ri = 0; ri < level.Rooms.Length; ri++)
    {
        var r1 = level.Rooms[ri];
        foreach (var f in r1.Rectangles.Concat(r1.Triangles))
        {
            var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
            int minY = verts.Min(v => (int)v.Y), maxY = verts.Max(v => (int)v.Y);
            int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
            int minZ = verts.Min(v => (int)v.Z), maxZ = verts.Max(v => (int)v.Z);
            bool looksLikeXWall = Math.Abs(maxX - minX) <= 8;
            bool looksLikeZWall = Math.Abs(maxZ - minZ) <= 8;
            if (looksLikeXWall || looksLikeZWall) continue; // only floor/ceiling faces

            int bx0 = Math.Max(0, minX / 1024), bx1 = Math.Max(0, Math.Max(minX, maxX - 1) / 1024);
            int bz0 = Math.Max(0, minZ / 1024), bz1 = Math.Max(0, Math.Max(minZ, maxZ - 1) / 1024);
            int spanX = bx1 - bx0 + 1, spanZ = bz1 - bz0 + 1;

            if (spanX == 1 && spanZ == 1) singleSector++;
            else
            {
                multiSector++;
                int ti = f.Texture & 0x7FFF;
                if (ti >= 0 && ti < level.ObjectTextures.Length && multiSectorSamples.Count < 15)
                {
                    var ot = level.ObjectTextures[ti];
                    int tminX = ot.Vertices.Min(v => v.X >> 8), tmaxX = ot.Vertices.Max(v => v.X >> 8);
                    int tminY = ot.Vertices.Min(v => v.Y >> 8), tmaxY = ot.Vertices.Max(v => v.Y >> 8);
                    multiSectorSamples.Add((ri, minX, maxX, minZ, maxZ, tmaxX - tminX, tmaxY - tminY));
                }
            }
        }
    }
    Console.WriteLine($"Floor/ceiling faces spanning exactly 1 sector: {singleSector}");
    Console.WriteLine($"Floor/ceiling faces spanning MULTIPLE sectors: {multiSector}");
    Console.WriteLine("Samples of multi-sector faces (room, world X/Z span, texture UV width/height):");
    foreach (var s in multiSectorSamples)
        Console.WriteLine($"  room={s.room} X=[{s.minX},{s.maxX}] (span={s.maxX-s.minX}) Z=[{s.minZ},{s.maxZ}] (span={s.maxZ-s.minZ})  texUV width={s.texRight} height={s.texBottom}");
}

// --- Investigate Room2 floor textures: real UV sizes for all floor/ceiling faces there ---
{
    Console.WriteLine();
    Console.WriteLine("--- Room2 floor/ceiling texture UV size audit ---");
    var r1 = level.Rooms[2];
    foreach (var f in r1.Rectangles.Concat(r1.Triangles))
    {
        var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
        int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
        int minZ = verts.Min(v => (int)v.Z), maxZ = verts.Max(v => (int)v.Z);
        bool looksLikeXWall = Math.Abs(maxX - minX) <= 8;
        bool looksLikeZWall = Math.Abs(maxZ - minZ) <= 8;
        if (looksLikeXWall || looksLikeZWall) continue; // only floor/ceiling

        int ti = f.Texture & 0x7FFF;
        if (ti < 0 || ti >= level.ObjectTextures.Length) continue;
        var ot = level.ObjectTextures[ti];
        int tminX = ot.Vertices.Min(v => v.X >> 8), tmaxX = ot.Vertices.Max(v => v.X >> 8);
        int tminY = ot.Vertices.Min(v => v.Y >> 8), tmaxY = ot.Vertices.Max(v => v.Y >> 8);
        int uvW = tmaxX - tminX, uvH = tmaxY - tminY;
        int tile = ot.TileAndFlag & 0x7FFF;
        // Flag anything noticeably bigger than a standard ~63x63 tile.
        if (uvW > 70 || uvH > 70)
            Console.WriteLine($"  LARGE UV: tex={ti} tile={tile} uvSize={uvW}x{uvH} faceX=[{minX},{maxX}] faceZ=[{minZ},{maxZ}] isTri={f.IsTriangle}");
    }
    Console.WriteLine("(no further output above this line means no oversized UVs found in Room2)");
}

// --- Compare our Room2 large-UV floor assignment against the reference at the same sector ---
{
    Console.WriteLine();
    Console.WriteLine("--- Room2 large-UV sectors vs reference ---");
    var r1 = level.Rooms[2];
    var refRoom = refRooms.FirstOrDefault(rr => Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100);
    if (refRoom == null) { Console.WriteLine("No matching reference room found for room2!"); }
    else
    {
        // Sectors x=10, z=4..13 (world X=[10240,11264], Z spans multiple).
        for (int z = 4; z <= 13; z++)
        {
            int x = 10;
            if (x >= refRoom.NumXSectors || z >= refRoom.NumZSectors) continue;
            var faces = refRoom.Sectors[x, z].GetFaceTextures();
            if (!faces.TryGetValue(SectorFace.Floor, out var tex))
            {
                Console.WriteLine($"  sector({x},{z}): reference has NO Floor texture here");
                continue;
            }
            float w = Math.Abs(tex.TexCoord1.X - tex.TexCoord0.X);
            float h = Math.Abs(tex.TexCoord2.Y - tex.TexCoord0.Y);
            Console.WriteLine($"  sector({x},{z}): reference Floor UV size ~ {w:F0}x{h:F0}  TexCoord0=({tex.TexCoord0.X:F0},{tex.TexCoord0.Y:F0})");
        }
    }
}

// --- Raw byte-level inspection of the outlier "large UV" ObjectTexture entries ---
{
    Console.WriteLine();
    Console.WriteLine("--- Raw vertex byte inspection: is Xcoordinate/Ycoordinate always 0 or 255? ---");
    foreach (var idx in new[] { 2109, 2106, 2102, 2107, 2108, 2104, 2105 })
    {
        if (idx >= level.ObjectTextures.Length) continue;
        var ot = level.ObjectTextures[idx];
        Console.WriteLine($"ObjTex[{idx}]: TileAndFlag=0x{ot.TileAndFlag:X4} (tile={ot.TileAndFlag & 0x7FFF})");
        foreach (var v in ot.Vertices)
        {
            int xCoordByte = v.X & 0xFF, xPixelByte = (v.X >> 8) & 0xFF;
            int yCoordByte = v.Y & 0xFF, yPixelByte = (v.Y >> 8) & 0xFF;
            bool xOk = xCoordByte == 0 || xCoordByte == 255;
            bool yOk = yCoordByte == 0 || yCoordByte == 255;
            Console.WriteLine($"    X: raw=0x{v.X:X4} coordByte={xCoordByte}{(xOk?"":"  <-- NOT 0/255!")} pixelByte={xPixelByte}   Y: raw=0x{v.Y:X4} coordByte={yCoordByte}{(yOk?"":"  <-- NOT 0/255!")} pixelByte={yPixelByte}");
        }
    }
}

// --- Precise check: what texture index did OUR code actually write into Room2 Floor slots
// (10, z=4..13), and what's THAT exact ObjectTexture's UV bounding box? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Precise Room2 (x=10, z=4..13) Floor slot audit ---");
    var pr = prj.Rooms[2];
    for (int z = 4; z <= 13; z++)
    {
        int x = 10;
        int target = x * pr.ZSize + z;
        if (target < 0 || target >= pr.Blocks.Length) { Console.WriteLine($"  ({x},{z}): out of range"); continue; }
        var blockTex = pr.Blocks[target].Textures[0]; // Floor slot
        if (blockTex.Tipo != 0x0007) { Console.WriteLine($"  ({x},{z}): Floor slot NOT SET (Tipo=0x{blockTex.Tipo:X4})"); continue; }

        int textureIndex = blockTex.Index; // full int now, no more 10-bit packing
        if (textureIndex < 0 || textureIndex >= level.ObjectTextures.Length)
        { Console.WriteLine($"  ({x},{z}): textureIndex={textureIndex} OUT OF RANGE"); continue; }

        var ot = level.ObjectTextures[textureIndex];
        int tile = ot.TileAndFlag & 0x7FFF;
        var realTexInfo = prj.Textures[textureIndex];
        Console.WriteLine($"  ({x},{z}): textureIndex={textureIndex} tile={tile} REAL TexInfo uvSize={realTexInfo.Right}x{realTexInfo.Bottom} X={realTexInfo.X} rotation={blockTex.Rotation} triangle={blockTex.Triangle} isTriangleTexFlag={(ot.NewFlags & 0x8000) != 0}");
    }
}

// --- Updated Floor coverage investigation using the REAL classification criteria (not-a-wall,
// matching current ApplyRoomFaceTexture), not the old flat-only filter. ---
{
    Console.WriteLine();
    Console.WriteLine("--- Floor coverage investigation v2 (matches current not-a-wall criteria) ---");
    int totalMissing = 0, hasRealQuadButMissing = 0, noRealQuadAtAll = 0, ourSlotWasSet = 0;
    var sampleMissingWithQuad = new List<(int room, int x, int z)>();
    var sampleMissingNoQuad = new List<(int room, int x, int z)>();

    for (int ri = 0; ri < refRooms.Count; ri++)
    {
        var refRoom = refRooms[ri];
        var r1 = level.Rooms.FirstOrDefault(rr => Math.Abs(rr.X - refRoom.Position.X * 1024) < 1100 && Math.Abs(rr.Z - refRoom.Position.Z * 1024) < 1100);
        if (r1 == null) continue;
        int roomIdx = Array.IndexOf(level.Rooms, r1);
        var pr = prj.Rooms[roomIdx];

        for (int x = 0; x < refRoom.NumXSectors && x < pr.XSize; x++)
        for (int z = 0; z < refRoom.NumZSectors && z < pr.ZSize; z++)
        {
            if (!refRoom.Sectors[x, z].GetFaceTextures().ContainsKey(SectorFace.Floor)) continue;

            int target = x * pr.ZSize + z;
            if (target < 0 || target >= pr.Blocks.Length) continue;
            bool ourSlotSet = pr.Blocks[target].Textures[0].Tipo == 0x0007;
            if (ourSlotSet) { ourSlotWasSet++; continue; }

            totalMissing++;

            int worldXlo = x * 1024, worldXhi = (x + 1) * 1024;
            int worldZlo = z * 1024, worldZhi = (z + 1) * 1024;
            bool foundQuad = false;
            foreach (var f in r1.Rectangles.Concat(r1.Triangles))
            {
                var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
                int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
                int minZ = verts.Min(v => (int)v.Z), maxZ = verts.Max(v => (int)v.Z);
                bool looksLikeXWall = Math.Abs(maxX - minX) <= 8;
                bool looksLikeZWall = Math.Abs(maxZ - minZ) <= 8;
                if (looksLikeXWall || looksLikeZWall) continue; // not floor/ceiling shaped
                int avgX = (int)verts.Average(v => (int)v.X);
                int avgZ = (int)verts.Average(v => (int)v.Z);
                if (avgX >= worldXlo - 64 && avgX <= worldXhi + 64 && avgZ >= worldZlo - 64 && avgZ <= worldZhi + 64)
                { foundQuad = true; break; }
            }
            if (foundQuad) { hasRealQuadButMissing++; if (sampleMissingWithQuad.Count < 15) sampleMissingWithQuad.Add((roomIdx, x, z)); }
            else { noRealQuadAtAll++; if (sampleMissingNoQuad.Count < 5) sampleMissingNoQuad.Add((roomIdx, x, z)); }
        }
    }

    Console.WriteLine($"Reference sectors with a Floor texture: {ourSlotWasSet + totalMissing}");
    Console.WriteLine($"  our slot already set: {ourSlotWasSet}");
    Console.WriteLine($"  missing entirely: {totalMissing}");
    Console.WriteLine($"    of which HAVE a real compiled quad nearby (should be fixable): {hasRealQuadButMissing}");
    Console.WriteLine($"    of which have NO real compiled quad at all (likely unrecoverable): {noRealQuadAtAll}");
    Console.WriteLine("Sample fixable cases (room, x, z):");
    foreach (var s in sampleMissingWithQuad) Console.WriteLine($"  room={s.room} x={s.x} z={s.z}");
    Console.WriteLine("Sample unrecoverable cases (room, x, z):");
    foreach (var s in sampleMissingNoQuad) Console.WriteLine($"  room={s.room} x={s.x} z={s.z}");
}

// --- Deep dive: room2 sector(1,3) -- why is it missing despite having real geometry? ---
{
    Console.WriteLine();
    Console.WriteLine("--- Deep dive: room2 sector(1,3) ---");
    var r1 = level.Rooms[2];
    var pr = prj.Rooms[2];
    int target = 1 * pr.ZSize + 3;
    var block = pr.Blocks[target];
    Console.WriteLine($"Block.Floor={block.Floor} Block.Ceiling={block.Ceiling} FloorCorner=[{string.Join(",", block.FloorCorner)}]");
    Console.WriteLine($"Floor slot[0] Tipo=0x{block.Textures[0].Tipo:X4}  Floor_Tri2 slot[8] Tipo=0x{block.Textures[8].Tipo:X4}");

    int worldXlo = 1 * 1024, worldXhi = 2 * 1024;
    int worldZlo = 3 * 1024, worldZhi = 4 * 1024;
    foreach (var f in r1.Rectangles.Concat(r1.Triangles))
    {
        var verts = f.Vertices.Select(vi => r1.Vertices[vi]).ToArray();
        int minX = verts.Min(v => (int)v.X), maxX = verts.Max(v => (int)v.X);
        int minZ = verts.Min(v => (int)v.Z), maxZ = verts.Max(v => (int)v.Z);
        bool looksLikeXWall = Math.Abs(maxX - minX) <= 8;
        bool looksLikeZWall = Math.Abs(maxZ - minZ) <= 8;
        if (looksLikeXWall || looksLikeZWall) continue;
        int avgX = (int)verts.Average(v => (int)v.X);
        int avgZ = (int)verts.Average(v => (int)v.Z);
        if (avgX >= worldXlo - 64 && avgX <= worldXhi + 64 && avgZ >= worldZlo - 64 && avgZ <= worldZhi + 64)
        {
            int minY = verts.Min(v=>(int)v.Y), maxY = verts.Max(v=>(int)v.Y);
            int avgY = (int)verts.Average(v => (int)v.Y);
            Console.WriteLine($"  Real quad: tex={f.Texture} isTri={f.IsTriangle} avgY={avgY} Yrange=[{minY},{maxY}] X=[{minX},{maxX}] Z=[{minZ},{maxZ}]");
        }
    }
}

// --- Check room2 dimensions and border status for sector (1,3) ---
{
    Console.WriteLine();
    Console.WriteLine("--- Room2 border check ---");
    var r1 = level.Rooms[2];
    Console.WriteLine($"Room2: NumX={r1.NumX} NumZ={r1.NumZ} YBottom={r1.YBottom} YTop={r1.YTop}");
    Console.WriteLine($"Sector (1,3) is border: x==0={1==0} x==NumX-1={1==r1.NumX-1} z==0={3==0} z==NumZ-1={3==r1.NumZ-1}");
    int idx = 1 * r1.NumZ + 3;
    var sec = r1.Sectors[idx];
    Console.WriteLine($"Raw sector (1,3): Floor={sec.Floor} Ceiling={sec.Ceiling}");
}
return 0;
