using System.IO;
using System.Numerics;
using TombLib;
using TombLib.LevelData;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;
using TombLib.Utils;

namespace PRJ2_Extractor.Core;

/// <summary>
/// Room face textures of a .ten level. The compiler packs every texture into atlases and writes, for each room polygon, its vertices and the
/// UV of every vertex; TombLib's own room geometry knows which sector face covers which vertices, so each polygon is matched to its face by
/// vertex positions and the UVs are turned back into the face's TextureArea (pixel coordinates inside the atlas page).
/// </summary>
internal static class TenTextures
{
    internal sealed class Stats
    {
        public int Faces, FacesTextured, FacesFromParts, FacesWithoutPolygon, Polygons, PolygonsUnmatched, MissingAtlas;
    }

    /// <summary>Writes the PNG atlases next to the project and registers them as level textures. DDS (BC1 / BC3) atlases are not supported yet.</summary>
    public static LevelTexture?[] RegisterAtlases(Level level, List<TenAtlas> atlases, string directory, string prefix, List<string> warnings)
    {
        var result = new LevelTexture?[atlases.Count];
        Directory.CreateDirectory(directory);
        for (int i = 0; i < atlases.Count; i++)
        {
            var blob = atlases[i].ColorMap;
            bool png = blob.Length > 8 && blob[0] == 0x89 && blob[1] == 0x50 && blob[2] == 0x4E && blob[3] == 0x47;
            if (!png)
            {
                warnings.Add($"Texture atlas {prefix}{i} is not a PNG (DDS atlases are not supported yet); its faces stay untextured.");
                continue;
            }
            string path = Path.Combine(directory, $"{prefix}{i:D2}.png");
            File.WriteAllBytes(path, blob);
            var texture = new LevelTexture(level.Settings, level.Settings.MakeRelative(path, VariableType.LevelDirectory), false);
            level.Settings.Textures.Add(texture);
            if (texture.LoadException != null)
                warnings.Add($"Texture atlas '{path}' failed to load: {texture.LoadException.Message}");
            result[i] = texture;
        }
        return result;
    }

    private static string Key(float x, float y, float z) => $"{(int)Math.Round(x)},{(int)Math.Round(y)},{(int)Math.Round(z)}";

    private static Vector3 ParseKey(string key)
    {
        var s = key.Split(',');
        return new Vector3(int.Parse(s[0]), int.Parse(s[1]), int.Parse(s[2]));
    }

    /// <summary>Dot product of the polygon normal with the normal of the face's first triangle (vertices in the file's coordinate system).</summary>
    private static float Facing(string[] corner, Vector3 polygonNormal)
    {
        var p0 = ParseKey(corner[0]); var p1 = ParseKey(corner[1]); var p2 = ParseKey(corner[2]);
        var n = Vector3.Cross(p1 - p0, p2 - p0);
        return Vector3.Dot(n, polygonNormal);
    }
    private sealed record Poly(TenBucket Bucket, TenPolygon Polygon, string[] Keys);

    public static Stats Apply(TenLevel ten, Room[] rooms, LevelTexture?[] roomTextures, LevelTexture?[] animatedTextures, List<string> warnings)
    {
        var stats = new Stats();
        for (int ri = 0; ri < ten.Rooms.Count; ri++)
        {
            var t = ten.Rooms[ri];
            var room = rooms[ri];
            TenWalls.Infer(t, room);
            room.BuildGeometry();
            var geometry = room.RoomGeometry;
            int roomY = room.Position.Y;

            // Polygons by the set of their vertex positions.
            var polygons = new Dictionary<string, List<Poly>>();
            foreach (var bucket in t.Buckets)
            foreach (var poly in bucket.Polygons)
            {
                var keys = poly.Indices.Select(i => Key(t.Positions[i].X, t.Positions[i].Y, t.Positions[i].Z)).ToArray();
                string setKey = string.Join("|", keys.OrderBy(k => k, StringComparer.Ordinal).Distinct());
                if (!polygons.TryGetValue(setKey, out var list)) polygons[setKey] = list = [];
                list.Add(new Poly(bucket, poly, keys));
                stats.Polygons++;
            }
            var used = new HashSet<string>();
            var pending = new List<(SectorFaceIdentity Id, VertexRange Range, string[] Corner)>();

            // Two faces back to back (the two sides of a thin wall) have the same vertices; the polygon whose normal agrees with the face's own
            // winding belongs to it. The agreement sign is the one that most single-owner faces show.
            var faceCorners = new List<(SectorFaceIdentity Id, VertexRange Range, string[] Corner, string SetKey)>();
            var facesPerKey = new Dictionary<string, int>();
            foreach (var entry in geometry.VertexRangeLookup)
            {
                var range = entry.Value;
                if (range.Count != 3 && range.Count != 6) continue;
                var corner = new string[range.Count];
                for (int k = 0; k < range.Count; k++)
                {
                    var v = geometry.VertexPositions[range.Start + k];
                    corner[k] = Key(v.X, -(v.Y + roomY), v.Z);
                }
                string setKey = string.Join("|", corner.OrderBy(k => k, StringComparer.Ordinal).Distinct());
                faceCorners.Add((entry.Key, range, corner, setKey));
                facesPerKey[setKey] = facesPerKey.GetValueOrDefault(setKey) + 1;
            }
            int agree = 0, disagree = 0;
            foreach (var f in faceCorners)
                if (facesPerKey[f.SetKey] == 1 && polygons.TryGetValue(f.SetKey, out var single) && single.Count == 1)
                {
                    if (Facing(f.Corner, single[0].Polygon.Normal) > 0) agree++; else disagree++;
                }
            bool positive = agree >= disagree;

            foreach (var (id, range, corner, setKey) in faceCorners)
            {
                stats.Faces++;
                if (!polygons.TryGetValue(setKey, out var found)) { pending.Add((id, range, corner)); continue; }
                used.Add(setKey);

                // Several polygons on the same vertices: the face's own one by orientation; a second, mirrored copy of the same face means double sided.
                var own = found.Where(f => (Facing(corner, f.Polygon.Normal) > 0) == positive).ToList();
                var first = own.Count > 0 ? own[0] : found[0];
                bool doubleSided = facesPerKey[setKey] == 1 && found.Count > 1;
                if (Assign(ten, room, roomTextures, animatedTextures, id, range, corner, first.Bucket, doubleSided,
                        c => { int j = Array.IndexOf(first.Keys, c); return j < 0 ? null : first.Polygon.Uv[j]; }, stats))
                    stats.FacesTextured++;
            }
            // The compiler may cut a face into several polygons (a wall face with a sloped top becomes a quad plus a triangle): take the
            // polygons that lie inside the face and read every corner's UV from whichever polygon has a vertex there.
            var unused = polygons.Where(kv => !used.Contains(kv.Key)).SelectMany(kv => kv.Value).ToList();
            foreach (var (id, range, corner) in pending)
            {
                var corners = corner.Distinct().Select(ParseKey).ToArray();
                if (corners.Length < 3) { stats.FacesWithoutPolygon++; continue; }
                var normal = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
                if (normal.LengthSquared() < 1e-3f) { stats.FacesWithoutPolygon++; continue; }
                normal = Vector3.Normalize(normal);
                var min = new Vector3(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)) - Vector3.One * 2;
                var max = new Vector3(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)) + Vector3.One * 2;
                bool Inside(string key)
                {
                    var p = ParseKey(key);
                    return Math.Abs(Vector3.Dot(normal, p - corners[0])) < 2 && p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
                }
                var inside = unused.Where(u => u.Keys.All(Inside)).ToList();
                // A double-sided face has a second, mirrored polygon on the same vertices: only the first one carries the face's own UVs.
                var parts = inside.GroupBy(u => string.Join("|", u.Keys.OrderBy(k => k, StringComparer.Ordinal))).Select(g => g.First()).ToList();
                if (parts.Count == 0) { stats.FacesWithoutPolygon++; continue; }

                Vector2? Uv(string c)
                {
                    foreach (var part in parts)
                    {
                        int j = Array.IndexOf(part.Keys, c);
                        if (j >= 0) return part.Polygon.Uv[j];
                    }
                    return null;
                }
                if (corner.Distinct().Any(c => Uv(c) == null)) { stats.FacesWithoutPolygon++; continue; }
                if (Assign(ten, room, roomTextures, animatedTextures, id, range, corner, parts[0].Bucket, false, Uv, stats))
                {
                    stats.FacesTextured++;
                    stats.FacesFromParts++;
                    foreach (var part in inside) unused.Remove(part);
                }
            }
            stats.PolygonsUnmatched += unused.Count;
        }
        return stats;
    }

    private static bool Assign(TenLevel ten, Room room, LevelTexture?[] roomTextures, LevelTexture?[] animatedTextures, SectorFaceIdentity id,
        VertexRange range, string[] corner, TenBucket bucket, bool doubleSided, Func<string, Vector2?> uvOf, Stats stats)
    {
        var atlas = bucket.Animated ? (bucket.Texture < ten.AnimatedAtlases.Count ? ten.AnimatedAtlases[bucket.Texture] : null)
                                    : (bucket.Texture < ten.RoomAtlases.Count ? ten.RoomAtlases[bucket.Texture] : null);
        var levelTexture = bucket.Animated ? (bucket.Texture < animatedTextures.Length ? animatedTextures[bucket.Texture] : null)
                                           : (bucket.Texture < roomTextures.Length ? roomTextures[bucket.Texture] : null);
        if (atlas == null || levelTexture == null) { stats.MissingAtlas++; return false; }

        Vector2 Uv(string cornerKey)
        {
            var uv = uvOf(cornerKey) ?? Vector2.Zero;
            return new Vector2(uv.X * atlas.Width, uv.Y * atlas.Height);
        }

        bool ceiling = id.Face is SectorFace.Ceiling or SectorFace.Ceiling_Triangle2;
        var area = new TextureArea { Texture = levelTexture, BlendMode = (BlendMode)bucket.BlendMode, DoubleSided = doubleSided };
        if (range.Count == 6)
        {
            // Vertex of each TexCoord, from UpdateFaceTexture / the compiler's polygon vertex order.
            int v0 = ceiling ? 5 : 3, v1 = ceiling ? 0 : 2, v2 = ceiling ? 2 : 0, v3 = 1;
            area.TexCoord0 = Uv(corner[v0]); area.TexCoord1 = Uv(corner[v1]);
            area.TexCoord2 = Uv(corner[v2]); area.TexCoord3 = Uv(corner[v3]);
        }
        else
        {
            int v0 = ceiling ? 2 : 0, v2 = ceiling ? 0 : 2;
            area.TexCoord0 = Uv(corner[v0]); area.TexCoord1 = Uv(corner[1]);
            area.TexCoord2 = Uv(corner[v2]); area.TexCoord3 = area.TexCoord2;
        }
        room.Sectors[id.Position.X, id.Position.Y].SetFaceTexture(id.Face, area);
        return true;
    }
}