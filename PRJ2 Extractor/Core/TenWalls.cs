using System.Numerics;
using TombLib.LevelData;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;

namespace PRJ2_Extractor.Core;

/// <summary>
/// The compiled file does not keep the heights of wall sectors, but TombLib builds the wall faces (QA / middle / WS ...) from them: the
/// polygons on a wall edge are stacked bands whose boundaries, at each end of the edge, are the open neighbour's floor / ceiling and the
/// wall's own floor / ceiling at that corner. The wall's corner heights are read back from those boundaries so that TombLib's face
/// geometry lines up with the polygons again (sloped walls included, since every corner is handled on its own).
/// </summary>
public static class TenWalls
{
    private enum Corner { XnZn, XpZn, XnZp, XpZp }

    /// <summary>Returns the number of wall sectors with at least one inferred corner.</summary>
    public static int Infer(TenRoom t, Room room)
    {
        int roomY = room.Position.Y;
        int Height(float engineY) => (int)Math.Round(-engineY - roomY);

        // Vertex heights of vertical polygons, by the plane they lie on ('x' or 'z'), the sector they start in and the end of the edge.
        var ends = new Dictionary<(char Axis, int Plane, int Start, int End), HashSet<int>>();
        foreach (var bucket in t.Buckets)
        foreach (var poly in bucket.Polygons)
        {
            var v = poly.Indices.Select(i => t.Positions[i]).ToArray();
            var xs = v.Select(p => (int)Math.Round(p.X)).Distinct().ToList();
            var zs = v.Select(p => (int)Math.Round(p.Z)).Distinct().ToList();
            char axis; int plane; List<int> along; bool alongX;
            if (zs.Count == 1 && xs.Count == 2) { axis = 'z'; plane = zs[0]; along = xs; alongX = true; }
            else if (xs.Count == 1 && zs.Count == 2) { axis = 'x'; plane = xs[0]; along = zs; alongX = false; }
            else continue;
            int start = along.Min();
            if (along.Max() - start != 1024) continue;           // only polygons spanning exactly one sector edge
            foreach (var p in v)
            {
                int a = (int)Math.Round(alongX ? p.X : p.Z);
                var key = (axis, plane, start, a == start ? 0 : 1);
                if (!ends.TryGetValue(key, out var set)) ends[key] = set = [];
                set.Add(Height(p.Y));
            }
        }

        var floorVotes = new Dictionary<(int, int, Corner), Dictionary<int, int>>();
        var ceilingVotes = new Dictionary<(int, int, Corner), Dictionary<int, int>>();
        static void Vote(Dictionary<(int, int, Corner), Dictionary<int, int>> votes, int x, int z, Corner corner, int height)
        {
            var key = (x, z, corner);
            if (!votes.TryGetValue(key, out var d)) votes[key] = d = [];
            d[height] = d.GetValueOrDefault(height) + 1;
        }

        for (int x = 0; x < t.XSize; x++)
        for (int z = 0; z < t.ZSize; z++)
        {
            if (room.Sectors[x, z].Type is not (SectorType.Wall or SectorType.BorderWall)) continue;

            // (neighbour, plane axis, plane coordinate, start along the edge, wall corners at end 0 / 1, neighbour corners at end 0 / 1)
            var edges = new (int nx, int nz, char axis, int plane, int start, Corner w0, Corner w1, Corner n0, Corner n1)[]
            {
                (x, z - 1, 'z', z * 1024, x * 1024, Corner.XnZn, Corner.XpZn, Corner.XnZp, Corner.XpZp),
                (x, z + 1, 'z', (z + 1) * 1024, x * 1024, Corner.XnZp, Corner.XpZp, Corner.XnZn, Corner.XpZn),
                (x - 1, z, 'x', x * 1024, z * 1024, Corner.XnZn, Corner.XnZp, Corner.XpZn, Corner.XpZp),
                (x + 1, z, 'x', (x + 1) * 1024, z * 1024, Corner.XpZn, Corner.XpZp, Corner.XnZn, Corner.XnZp),
            };
            foreach (var e in edges)
            {
                if (e.nx < 0 || e.nz < 0 || e.nx >= t.XSize || e.nz >= t.ZSize) continue;
                var n = room.Sectors[e.nx, e.nz];
                if (n.Type != SectorType.Floor) continue;
                for (int end = 0; end < 2; end++)
                {
                    if (!ends.TryGetValue((e.axis, e.plane, e.start, end), out var heights)) continue;
                    var nCorner = end == 0 ? e.n0 : e.n1; var wCorner = end == 0 ? e.w0 : e.w1;
                    int nf = Get(n.Floor, nCorner), nc = Get(n.Ceiling, nCorner);
                    var interior = heights.Where(h => Math.Abs(h - nf) > 1 && Math.Abs(h - nc) > 1).OrderBy(h => h).ToList();
                    int wf, wc;
                    if (interior.Count >= 2) { wf = interior.First(); wc = interior.Last(); }
                    else if (interior.Count == 1) { wf = interior[0]; wc = nc; }
                    else { wf = nf; wc = nc; }
                    Vote(floorVotes, x, z, wCorner, wf);
                    Vote(ceilingVotes, x, z, wCorner, wc);
                }
            }
        }

        var touched = new HashSet<(int, int)>();
        foreach (var kv in floorVotes) { Put(ref room.Sectors[kv.Key.Item1, kv.Key.Item2].Floor, kv.Key.Item3, Mode(kv.Value)); touched.Add((kv.Key.Item1, kv.Key.Item2)); }
        foreach (var kv in ceilingVotes) Put(ref room.Sectors[kv.Key.Item1, kv.Key.Item2].Ceiling, kv.Key.Item3, Mode(kv.Value));

        // A wall corner without any information takes the most common value of the wall's other corners.
        foreach (var (x, z) in touched)
        {
            var s = room.Sectors[x, z];
            foreach (Corner c in Enum.GetValues<Corner>())
            {
                if (!floorVotes.ContainsKey((x, z, c)))
                    Put(ref s.Floor, c, Mode(Enum.GetValues<Corner>().Where(o => floorVotes.ContainsKey((x, z, o))).GroupBy(o => Mode(floorVotes[(x, z, o)])).ToDictionary(g => g.Key, g => g.Count())));
                if (!ceilingVotes.ContainsKey((x, z, c)))
                    Put(ref s.Ceiling, c, Mode(Enum.GetValues<Corner>().Where(o => ceilingVotes.ContainsKey((x, z, o))).GroupBy(o => Mode(ceilingVotes[(x, z, o)])).ToDictionary(g => g.Key, g => g.Count())));
            }
        }
        return touched.Count;
    }

    private static int Mode(Dictionary<int, int> votes) => votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

    private static int Get(SectorSurface surface, Corner c) => c switch
    {
        Corner.XnZn => surface.XnZn, Corner.XpZn => surface.XpZn, Corner.XnZp => surface.XnZp, _ => surface.XpZp,
    };

    private static void Put(ref SectorSurface surface, Corner c, int height)
    {
        switch (c)
        {
            case Corner.XnZn: surface.XnZn = (short)height; break;
            case Corner.XpZn: surface.XpZn = (short)height; break;
            case Corner.XnZp: surface.XnZp = (short)height; break;
            default: surface.XpZp = (short)height; break;
        }
    }
}
