# PRJ2-Extractor — Geometry & Texture Documentation

This document tracks what PRJ2-Extractor implements, what's known to be broken or incomplete, and — critically — *why*, for both room geometry and face-texture assignment. It exists so future work (by Francy, another Claude instance, or anyone else) doesn't have to rediscover root causes that were already found and verified.

**Scope:** converting a compiled classic Tomb Raider level file (`.tr4`, `.trc`) into a Tomb Editor project file (`.prj2`), i.e. faithfully reconstructing TombLib's editable representation from the compiled binary. This is the reverse of what TombLib's own compiler does.

**Ground truth reference:** `alexhub2_orig.prj2` — the original, hand-authored level, converted to PRJ2 by Tomb Editor itself (not produced by this tool). All percentages below are measured against it using the validation harness in `PrjDiag/Program.cs`.

**Key principle used throughout:** TombLib's own local source (`TombLib/LevelData/...`, cloned separately) is the authoritative reference for format questions. Don't guess — read what TombLib actually writes when compiling PRJ2→TR4, and invert that math. TRosettaStone and trview are useful secondary sources but can diverge for TombLib-compiled files specifically.

---

## Part 1 — Geometry

### Status: 144/177 rooms (81%) byte-perfect; ~5% residual sector mismatch, believed structurally unresolvable

### What's implemented and verified
- Room dimensions, world position (X/Z), floor/ceiling bounds — all correct after fixing a recurring bug pattern: **TRosettaStone lists X before Z in the file, but the code historically read them swapped.** This bit `NumX`/`NumZ`, world `X`/`Z` position, `Blocks[]` indexing convention (`b = X_idx * NumZ + Z_idx`, X-major), and `GetBlockIndices`/portal marking — all had this same swap bug, found and fixed independently in different places.
- Corner index mapping for floor/ceiling triangulation, verified against trview's `Floordata.cpp`/`Sector.cpp`.
- Floor/ceiling world-Y formula: needed the room's `YBottom` term (`Position.Y` must be in world units via `Clicks.ToWorld`, not clicks; `XPos`/`ZPos` stay in sector units).
- Tilt/Roof (FloorData functions `0x02`/`0x03`): rewritten with max-relative (floor) / min-relative (ceiling) corner normalization, matching the triangulation convention, verified against `TombLib/Compilers/FloorData.cs`.
- Diagonal splits (Split1-4): applied unconditionally regardless of the `fixFdivs` flag (see below).
- Extra objects: Lights (Point/Spot 100% match; Sun direction unreliable in raw TR4 data — compiler limitation, not recoverable), Sound Sources (17/17 perfect), Sinks, Camera/Sink disambiguation (requires scanning FloorData Triggers for action type 0x01 vs 0x02 — the raw `Cameras[]` array alone gives no signal), Static cameras, Flyby cameras (22/22 exported without warnings).
- TR5 (`.trc`) support: TR5 compiled by TombLib does **not** follow vanilla Core Design layout documented by trlevel — `TombLib/LevelData/Compilers/Structs.cs` (`WriteTr5`) is the only reliable spec. Verified: geometry, sound sources, static/flyby cameras correct on TR5.

### `fixFdivs` flag (`ConvertToPrj(..., fixFdivs: false)`)
This is an NGLE/classic-PRJ-only workaround that stamps a synthetic non-zero FDiv/CDiv value onto almost every block. In TombLib, any non-zero `SetHeight(Floor2/Ceiling2, ...)` call creates **real diagonal-split geometry**, so leaving this on turns flat/tilted terrain into spiky, invalid sectors. Keep it `false`. Real splits from genuine TR4 FloorData (Split1-4) are applied regardless of this flag.

### Known unresolved cause — non-planar double-triangle ambiguity (~5%)
Some rooms have sectors where the raw FloorData for a non-planar (double-triangle) floor/ceiling split is **byte-identical** but requires a *different* correction depending on the room. This is an intrinsic ambiguity in the compiled encoding: the same bytes are consistent with more than one valid triangulation, and the raw data alone doesn't disambiguate.

**Hypothesis (not verified):** TombLib likely resolves this at PRJ2-import time by aligning ambiguous sectors against their neighbors across portals — a continuity heuristic, not a per-sector formula. This has not been replicated here. Attempting a pure per-sector fix is believed to have a low ceiling; the fix, if pursued, would need to look at neighbor/portal context, not just the local sector's own bytes.

### Diagonal geometry / ramps (deferred, not started)
Pits with multiple special FloorData floor functions and `RoomAbove`/`RoomBelow` ≠ 255: explicitly not handled. Faces where all three axes (X, Y, Z) vary are currently discarded by the classifier. Deferred by explicit request, to be addressed after consolidating the normal (non-diagonal) case.

---

## Part 2 — Textures

### Status: pipeline is now genuinely functional (was completely non-functional at the start of this work). ~46% face coverage vs the reference; where a texture *is* assigned, it is now correct.

This part had far more layers than expected. Each fix below was necessary but not sufficient — the visible symptom kept changing shape as deeper bugs were found. They're listed in the order discovered, since later fixes depended on earlier ones being in place.

### 2.1 — Wall tier ownership (QA / Middle / WS) — mostly solved

**Problem:** the classic PRJ format's `Block.Textures[]` has 14 slots. For a wall between two sectors, deciding which sector "owns" the QA (floor-step) vs WS (ceiling-step) vs Middle tier is not obvious from raw compiled geometry alone.

**Root cause found:** TombLib's own wall-tier assignment (`TombLib/LevelData/SectorGeometry/RoomExtensionMethods.cs`, `GetPositiveXWallData`/`GetNegativeXWallData`/etc., and `SectorWallData.cs`'s zero-height-face skip) is a **per-corner** rule, not a per-sector scalar comparison: a QA face exists on a sector only where that sector's floor is higher than the neighbor's *at a shared corner*, evaluated independently per corner — not by comparing a single flat height value per sector.

**Fix:** rewrote `ApplyWallFace` in `TrLevel.cs` to compare the two shared corners individually (`GetCornerFloorY`/`GetCornerCeilY`, honoring Tilt/Roof-derived per-corner data), matching TombLib's real algorithm. Corner index mapping for `FloorCorner`/`CeilCorner` verified against `ApplyFloorData`'s own Tilt/Roof/Split branches — **the two arrays use different corner orders** (`FloorCorner`: `[0]=XpZn [1]=XnZn [2]=XnZp [3]=XpZp`; `CeilCorner`: `[0]=XpZp [1]=XnZp [2]=XnZn [3]=XpZn`), a real trap if assumed symmetric.

**Result:** wall-tier match vs reference went from ~87.11% → 89.09% (ownership-agnostic seam comparison, `PrjDiag`).

**Sub-fix — border/solid neighbor:** a sector on the room's outer ring (x/z = 0 or max) or with the raw wall sentinel (`sector.Floor == -127`) has its `Block.Floor`/`Ceiling` **forced** to the room's absolute `YBottom`/`YTop` by `ConvertToPrj` (see `IsBorderOrSolid`), discarding real local data. When a real interior sector's own floor/ceiling happens to reach those same absolute extremes (common), the per-corner comparison sees "no difference" and silently drops real QA/WS faces. Fix: detect this case and fall back to classifying purely by position within the *own* sector's real span (a "sixth" heuristic — see 2.6 for its known limitation).

### 2.2 — `Prj2Exporter.cs` texture-writing pass didn't exist

Before this work, `TrLevel.cs` computed `Block.Textures[]` slot assignments but **nothing ever read them back out** into the actual `.prj2` file — the export "worked" only because this step was silently absent. Wrote `ApplyRoomFaceTextures`/`LoadTextureArea` in `Prj2Exporter.cs`, ported from TombLib's own `PrjLoader.LoadTextureArea` (UV construction, rotation, triangle-corner selection, flip/blend-mode flags — all copied from the same source that already solves this exact classic-PRJ→modern-TextureArea conversion). Requires `room.BuildGeometry(useLegacyCode: false)` to be called on every room before this pass (needed for `IsFaceDefined`/`GetFaceShape`, which the slot→SectorFace resolution depends on). `useLegacyCode: true` throws inside TombLib itself (`LegacyWallGeometry.GetVerticalFloorPartFaces`, index out of range) — consistent with this project never populating Floor2/Ceiling2, which the legacy path apparently assumes.

**Verified:** re-loading the exported `.prj2` via `Prj2Loader` shows real, valid `TextureArea` data, not just populated in-memory structures.

### 2.3 — "TEXTURE OUT OF BOUNDS" in Tomb Editor — atlas was missing data

**Symptom:** most faces showed Tomb Editor's "TEXTURE OUT OF BOUNDS" placeholder; the few real textures shown were garbled.

**Root cause:** `TrLevel.Load()`'s TR4 texture-page reader explicitly **skipped** the object-texture pixel block (`tex32.Seek(NumObjTextiles * 256 * 256 * 4, SeekOrigin.Current)`) instead of reading it. Room faces can and do legitimately reference tiles in what was the "object" range (verified: this test level has room faces using tile indices up to 12 against only `NumRoomTextiles=4`), so the atlas image we wrote was missing real data those faces needed.

**Fix:** read (not skip) the object-texticle block, positioned `[room][object][bump]` in that order in the atlas, matching the unified tile numbering that `ObjectTexture.TileAndFlag` indexes into.

### 2.4 — Atlas contaminated with WAD/mesh-only textures

The full room+object+bump atlas also contains textures used exclusively by moveables/statics (WAD content), never by room geometry. Added `BuildRoomTextureAtlas()`: scans actual room-face usage, keeps only tiles genuinely referenced by room geometry, compacts them into a dense `0..N-1` sequence with a remap table used when computing `TexInfo.Y`.

### 2.5 — Misdiagnosis: "bump map" tile range (found and reverted)

Initially believed tiles beyond `NumRoomTextiles+NumObjTextiles` were bump-map data (surface normal/height, not colour) and excluded them from the atlas. **This was wrong** — direct pixel inspection of that region showed ordinary, well-formed room textures (brick, wood, roof tiles, decorative reliefs), not bump data. Confirmed via TRosettaStone that TR4 bump-mapping is actually a **separate additive render pass** flagged by `NewFlags` bits 9-10 on the `ObjectTexture` itself, not a distinct tile range referenced directly — and none of the "high-tile" textures in this level had that flag set (`bumpLevel=0` on all samples). The exclusion was reverted; all referenced tiles are kept regardless of index range.

*Lesson: a plausible-sounding theory backed by documentation can still be wrong for the specific file in hand — the pixel-level visual check was what actually settled it, not the spec.*

### 2.6 — **The real root cause: 10-bit texture index truncation** (the big one)

**Symptom:** after fixing 2.3–2.5, textures were no longer missing/garbled in the "wrong region" sense, but were **stretched and showed the wrong image** — a small unrelated rectangle from a different texture, stretched across the whole face.

**Root cause:** `BlockTex.Index` (the intermediate classic-PRJ-style model this project uses before handing off to `Prj2Exporter`) was a `byte`, with 2 more bits packed into `Flags1` — a 10-bit field, max value 1023. This mirrors the real classic-PRJ on-disk format's own limit. But **this project doesn't need to round-trip through that on-disk format at all** — `Prj2Exporter` writes directly to TombLib's modern `Sector.SetFaceTexture`/`TextureArea`, which has no such limit. The intermediate model inherited a real file-format limit that no longer applied to how the data was actually being used.

Verified: **656/656 (100%)** of the distinct texture indices referenced by room faces in this level exceed 1023 (TombLib-compiled levels routinely have thousands of `ObjectTexture` entries — this level has 2692). Every single one was silently truncated (e.g. index 1969 → `1969 & 0x3FF = 945`), which doesn't crash (945 is a valid index) — it just silently points at a **different, unrelated texture**, explaining the "stretched wrong rectangle" symptom exactly.

**Fix:** widened `BlockTex.Index` from `byte` to `int`, removed the `Flags1` bit-packing in `SetBlockTexture` and the corresponding reconstruction in `Prj2Exporter.LoadTextureArea`, and removed the `Math.Min(ObjectTextures.Length, 1024)` cap in `BuildPrjTextureTable`.

**This is the fix that made the pipeline visually correct.** Verified in Tomb Editor: room geometry now shows coherent stone/wood/roof textures matching the reference level's actual materials, not random stretched fragments.

*Lesson: a comment in the code (`SetBlockTexture` had one, verbatim, warning about this exact truncation and calling it "a known, temporary correctness gap") had already identified this as the real fix needed, months earlier — but `Prj2Exporter.cs` was written to read from the same lossy 10-bit intermediate anyway, inheriting the problem instead of bypassing it as originally planned. Read old TODO-style comments before re-deriving conclusions from scratch.*

### 2.7 — Coverage gap (measured after 2.6, before 2.8): ~46% (2944/6354 reference face-texture entries)

With fixes up to 2.6, texture *correctness* was solved; texture *coverage* was not. Breakdown by `SectorFace` type (ours / reference) at that point:

| Category | Coverage |
|---|---|
| Floor | 43.9% |
| Ceiling | 71.1% |
| Floor_Triangle2 | 29.7% |
| Ceiling_Triangle2 | 21.1% |
| QA (4 directions) | 9.7% – 59.9% |
| WS (4 directions) | 5.7% – 21.0% |
| Middle (4 directions) | **230% – 257%** (over-assigned) |
| Floor2 / Ceiling2 (8 variants) | 0.0% |

#### Middle over-assignment — diagnosed, then partially fixed (see 2.8)
Instrumented `ApplyWallFace` with temporary debug counters (since removed) to find where excess Middle assignments came from:

- `middle:reliable_neighbor_no_qa_no_ws_at_all` — 4683 — genuinely flat wall, no height difference at either shared corner. **Believed correct**, not a bug.
- `middle:unreliable_neighbor_sixth_miss` — 2493 — the border/solid-neighbor "sixth" heuristic (2.1's sub-fix) only classified the top/bottom 1/6 of the span as QA/WS; the middle 4/6 always fell to Middle by default, even when it shouldn't.
- `middle:reliable_neighbor_qa_or_ws_existed_but_avgY_outside_band` — 543 — a real QA/WS band existed but the specific quad's average Y fell just outside the computed range.
- `middle:unreliable_neighbor_zero_span` — 63 — degenerate, negligible.

The dominant fixable contributor (~3036 of the gap) was the coarse "sixth" heuristic. Fixed in 2.8 below.

#### Floor2/Ceiling2 — investigated, mostly not recoverable
Two hypotheses tested:

1. **"Hidden FloorData we're not parsing"** — investigated by dumping raw sector FloorData for reference sectors with Floor2/Ceiling2 populated. All 8 manually-inspected cases involved a `Floor=-127`/`Ceiling=-127` (wall sentinel) neighbor. No distinct FloorData signature found beyond what's already parsed.
2. **"Extra real compiled quads we're not classifying"** (i.e. the same phenomenon as the Middle over-assignment, just for a 5-tier instead of 3-tier split) — tested directly: of 711 reference sectors with Floor2/Ceiling2, **555 (78%) have zero real compiled quads at that seam at all.** Only 156 (22%) have 1+ real quads to potentially recover.

**Conclusion: the large majority of Floor2/Ceiling2 is vestigial authored texture data on faces that never got compiled into visible TR4 geometry** — same category as the general "texture behind a portal/solid wall, never compiled" limitation already known elsewhere in this project. Structurally, `Ceiling2` and `WS` also share the *same* classic-PRJ storage slot (slot 3) as mutually-exclusive alternates (verified in `PrjLoader.cs` lines ~1727-1763) — they are not simply "5 tiers stacked instead of 3"; which one gets used depends on TombLib's own modern per-sector second-tier height data (`Sector.SetHeight(Floor2/Ceiling2, ...)`), which this project's raw-TR4-based pipeline never computes at all. Implementing this properly (even for just the 22% with real geometry) would require deriving that second-tier height data from somewhere in the compiled file — not yet found, may not exist.

**Recommendation:** not pursued further. The ceiling on recoverable value (≤22% of an already-small category) doesn't justify the engineering cost of replicating TombLib's mutual-exclusivity logic without the underlying height data it depends on.

### 2.8 — Shape-aware rank-based tier assignment for border/solid-neighbor walls (fixes part of 2.7's Middle over-assignment)

**Motivation:** before investing in Floor2/Ceiling2 (see above, low ceiling), tested an alternative that stays entirely within the existing 3-slot QA/Middle/WS model: instead of the old "sixth" heuristic (splitting *own's own span* into fixed fractions when the neighbor is unreliable), use the **real compiled quads already present at that seam** to determine tier boundaries.

**Validation before implementing:** measured how many real compiled quads actually exist at border/solid-neighbor seams, level-wide (not just the Floor2/Ceiling2 subset). Of 2184 such X-direction seams, 326 have 1+ real quads, and of those, **84.4% (275/326) have 3 or fewer** — a count that maps naturally onto QA/Middle/WS by position, no guessing needed.

**Implementation (`TrLevel.cs`):** restructured wall-face texture assignment into two passes per room. `ApplyRoomFaceTexture`/`ApplyWallFace` no longer write immediately for the "unreliable neighbor" case; instead they accumulate `(avgY, textureIndex, face)` per seam key `(ownX, ownZ, isXDirection)` into a `pending` dictionary. After all faces in the room are processed, `FlushUnreliableWallSeams` sorts each seam's quads by `avgY` (descending — raw TR Y is positive-down, so the largest value is physically closest to the floor) and assigns by rank: first (closest to floor) → QA, last (closest to ceiling) → WS, everything else → Middle. A single-quad seam still can't be disambiguated and defaults to Middle, same as before. Seams with more than 3 real quads still can't be fully represented (same structural ceiling as Floor2/Ceiling2), but the QA/WS boundary is now exact — taken from real geometry — rather than an arbitrary fraction of own's span.

**Result:**
- Wall-tier match vs reference: 89.09% → **89.67%**.
- Middle over-assignment reduced, unevenly across directions: `Wall_PositiveX_Middle` 230%→170.8%, `Wall_PositiveZ_Middle` ~230%→161.3%, but `Wall_NegativeX_Middle` (233.9%) and `Wall_NegativeZ_Middle` (256.6%) barely moved. **The X/Z-positive vs -negative asymmetry is unexplained** — not yet investigated, worth a look before further tuning in this area.
- Total texture entries written dropped slightly (2944 → 2773). Not a regression: for seams with 4+ quads, the old heuristic could scatter writes across QA/Middle/WS somewhat arbitrarily (each quad tested independently against a fixed fraction), while the new rank-based approach deterministically collapses every non-extreme quad into the *same* Middle slot (later writes overwrite earlier ones at that slot), so fewer distinct final writes survive per seam — but each surviving write is better-reasoned.

Debug counters used to produce the diagnosis above were temporary and have been removed from the codebase after the fix landed.

### 2.9 — Sloped floor/ceiling faces were being silently dropped entirely (the biggest single coverage fix)

**Investigation approach:** rather than attacking the wall tiers further, switched to Floor coverage (43.9% at the time) since walls and floor/ceiling are largely independent problems. First fix: triangular floor/ceiling faces were unconditionally routed to the `Floor_Triangle2`/`Ceiling_Triangle2` slot (8/9) whenever the compiled `RoomFace` happened to be a triangle, regardless of whether the sector had a genuine two-different-texture split. Since TR4 renders many ordinary (non-split) floors as a lone triangle (e.g. partial coverage at a diagonal room boundary), this left the primary `Floor`/`Ceiling` slot (0/1) empty for those sectors. Fixed: route triangles to the primary slot by default, only falling back to the Triangle2 slot when the primary slot is already occupied by a **different** texture (the real signature of a genuine split).

**Second, bigger finding, discovered while validating the first fix:** `Floor_Triangle2`/`Ceiling_Triangle2` coverage dropped to a suspicious **exact 0%** after the first fix. Investigating why (dumping the real compiled triangles at a reference `Floor_Triangle2` sector) revealed the actual pattern: a genuine split there wasn't "two flat triangles with different textures" as assumed — it was **one flat triangle and one sloped triangle** (Y varying by 256 units across its vertices) sharing the sector. The sloped triangle's bounding box is large in both X and Z (like any floor/ceiling face) but also varies in Y — and `ApplyRoomFaceTexture`'s branching at the time required near-zero Y variance (`epsilon=8`) just to be *considered* a floor/ceiling face at all. Failing that check, it then failed both wall checks too (its X and Z extents are both far larger than the wall epsilon), so it matched **none** of the method's three branches and was silently discarded — not just misclassified into the wrong slot, but never touched by any texture-assignment logic whatsoever.

This is a distinct, more fundamental gap than any wall-tier issue: any tilted/sloped floor or ceiling face (from Tilt/Roof/Split FloorData) was invisible to the classifier entirely, on top of (and largely explaining) the low `Floor`/`Ceiling`/`Floor_Triangle2`/`Ceiling_Triangle2` coverage numbers.

**Fix:** restructured `ApplyRoomFaceTexture`'s branch order. Instead of gating floor/ceiling classification on "is this face flat" (`Math.Abs(maxY-minY) <= epsilon`), gate it on "is this face NOT a wall" (fails both the X-narrow and Z-narrow wall tests) — a floor/ceiling face, flat or sloped, always has large extent in *both* X and Z, which a wall face by definition does not. Classification (which of Floor/Ceiling a face belongs to) now uses each covered sector's real per-corner-averaged floor/ceiling reference (`GetAvgCornerFloorY`/`GetAvgCornerCeilY`, built from the same `GetCornerFloorY`/`GetCornerCeilY` helpers the wall-tier code already uses) instead of the old flat `-Block.Floor*256` scalar — for a genuinely flat sector this reduces to exactly the same value as before (`FloorCorner`/`CeilCorner` are all zero there), so no regression for the already-working flat case, while now also correctly handling sloped sectors.

**Result (both fixes combined):**

| Category | Before | After |
|---|---|---|
| Floor | 43.9% | **82.1%** |
| Floor_Triangle2 | 29.7% | **73.4%** |
| Ceiling | 71.1% | **108.0%** (slight over-assignment, not yet investigated -- minor) |
| Ceiling_Triangle2 | 21.1% | **78.9%** |

Overall level coverage: ~46% → **~61.4%** (3899/6354 reference face-texture entries). Wall-tier match unaffected (still 89.67%), as expected — this fix only touched the floor/ceiling branch.

### 2.10 — Triangle-texture 4th vertex could hold garbage, inflating rendered texture size 2-3x ("texture bigger than 64x64")

**Symptom:** in Tomb Editor, some floor areas showed a texture rendered visibly larger than the standard 64x64 tile size — e.g. a big, non-repeating-looking sand/dirt patch where the surrounding floor showed normal small tiled squares.

**Investigation:** TR4 legitimately allows some faces to use higher- or lower-resolution textures than 64x64 (not inherently wrong), so the first step was confirming this specific case genuinely was a bug and not a legitimate design choice. Compared our exact `Block.Textures[0]` (Floor slot) assignment for a reported room/sector directly against the reference `alexhub2_orig.prj2`'s `Sector.GetFaceTexture(Floor)` for the *same* sector: reference showed a clean, standard 64x64 UV; ours showed 128x127, 191x128, etc. — confirming a real bug, not intentional variable-resolution texturing.

**Root cause:** verified byte-level vertex parsing was correct (every vertex's low "coordinate" byte was cleanly 0 or 255, per TRosettaStone's `Xcoordinate`/`Ycoordinate` spec — ruling out a parsing bug). Inspecting one specific `ObjectTexture` record directly (`tex=2106`) revealed the real cause: its first 3 vertices formed a tight, correct triangle exactly matching the reference's expected 64x64 region, but the **4th vertex was a distant, unrelated outlier** (e.g. `(128,0)` when the real triangle sat around `(0-63, 64-127)`). `ToPrjTexInfo`'s bounding-box computation (`Vertices.Min/Max`) used all 4 vertices unconditionally, so this one bad vertex dragged the computed width/height to roughly double or triple the real size.

Confirmed via TRosettaStone (`miscellany.asc`) that **bit 15 of `ObjectTexture.NewFlags`** explicitly marks "this texture is used on a triangle face" — a real, documented flag, not something to infer from vertex duplication patterns (which don't reliably apply here: TombLib-compiled files don't always duplicate the 3rd vertex into the 4th slot for triangles the way some vanilla-compiled files might).

**Fix:** `ToPrjTexInfo` now checks `(texture.NewFlags & 0x8000) != 0` and, when set, computes the UV bounding box from only `Vertices[0..2]`, ignoring the unreliable 4th slot entirely.

**Result:** verified on the reported case — UV size corrected from 128x127/191x128/etc. down to the expected 63x63, matching the reference exactly. No regression: wall-tier match (89.67%) and total texture entry count (3899) both unchanged, since this fix only corrects the *size* of UVs already being assigned, not which faces get textures.

### 2.11 — Sign error in `GetCornerFloorY`: FloorCorner was added instead of subtracted (affected both floor/ceiling AND wall classification)

**Symptom:** tracing a specific "missing Floor" sector (room2, sector(1,3)) that genuinely has real compiled floor geometry there showed the texture was being written to the **Ceiling** slot (1) instead of Floor (0) — not missing, misclassified.

**Root cause:** the real compiled quad at that sector has `Yrange=[-5120,-4608]`. `Block.Floor=20` alone (no corner adjustment) correctly gives `-5120`, matching the quad's highest point exactly. But for a corner with `FloorCorner=2`, `GetCornerFloorY`'s formula (`-(block.Floor + FloorCorner[idx]) * 256`) gave `-5632` — *more* negative, i.e. computed as *higher* than the flat reference, which is geometrically impossible (a sloped corner can't be higher than the sector's own flat baseline in this convention). The real quad's lowest point is `-4608`, which is exactly what `-(block.Floor - FloorCorner[idx]) * 256` gives instead. Cross-checked against the already-validated geometry-building code in `Prj2Exporter.cs` (`floorBase - block.FloorCorner[i]`, used to build the real, verified `.prj2` sector heights) — confirms subtraction is correct; `GetCornerFloorY` had the sign backwards. (`GetCornerCeilY` was checked the same way against `ceilBase + block.CeilCorner[i]` and found already correct — only the floor version had the bug.)

This function is shared by **both** floor/ceiling classification (2.9's fix) and wall QA/WS classification (2.1) — the wrong sign was silently degrading both for any sector with a genuinely tilted floor, not just the floor/ceiling branch.

**Fix:** one-line sign flip in `GetCornerFloorY`.

**Result:**

| Category | Before 2.11 | After 2.11 |
|---|---|---|
| Floor | 82.1% | **92.3%** |
| Floor_Triangle2 | 73.4% | **88.7%** |
| Ceiling | 108.0% (over-assigned) | **99.6%** |
| Ceiling_Triangle2 | 78.9% | 78.9% (unchanged) |
| Wall-tier match | 89.67% | **89.80%** |

Notably this also fixed the Ceiling over-assignment noted in 2.9 (108%→99.6%) — same root cause. Overall level coverage: ~61.4% → **~72.9%** (4173/6354 reference face-texture entries). Floor/ceiling coverage is now essentially complete; remaining gaps are concentrated entirely in the wall tiers (QA 6.7–64.8%, WS 6.7–22.1%, Middle still over-assigned 161–267%, Floor2/Ceiling2 still 0% per 2.7's established ceiling on recoverability).

### 2.12 — Extended rank-based (2.8) tier assignment from "border/solid neighbor only" to "any seam with 3+ real compiled quads"

**Motivation:** after 2.11, wall QA jumped for the two "Negative" directions specifically (roughly 10%→65%) while WS and the "Positive" directions barely moved, and Middle over-assignment got slightly worse. The reliable-neighbor per-corner algorithm (2.1) inherently caps at 3 tiers; seams with more real quads than that were already known (from Floor2/Ceiling2's investigation) to need more bands than QA/Middle/WS can hold.

**Fix:** added a per-room pre-pass (`CountWallQuadsPerSeam`) that counts real compiled wall quads per seam key before the main classification pass, then extended `neighborUnreliable` (the condition that routes a seam to the rank-based FlushUnreliableWallSeams path instead of the per-corner algorithm) to also trigger whenever a seam has 3 or more real quads, regardless of whether the neighbor is border/solid.

**Threshold check:** tried both `>= 3` and `>= 4` as the cutoff. `>= 4` gave a *worse* overall result (89.67%→90.02% vs `>=3`'s 89.67%→90.70%) and, tellingly, produced **identical** Middle over-assignment numbers to `>=3` — proving the Middle regression comes from seams with 4+ quads specifically (affected either way), while exactly-3-quad seams were a net *positive* contribution that `>=4` throws away. Kept `>=3`.

**Result:** wall-tier match 89.80% → **90.70%**, but with an uneven, only partially satisfying profile (QA_Negative* jumped to ~65%, QA_Positive* and all WS barely moved, Middle over-assignment got marginally worse on 2 of 4 directions) — this incompleteness is what led directly to investigating 2.13 below.

### 2.13 — The QA/WS Positive-direction asymmetry was a missing mirrored-ownership case, not noise

**Investigation:** 2.12 fixed "Negative"-direction QA but left "Positive"-direction QA and WS far behind (QA_Positive ~15-22% vs QA_Negative ~65%). Traced one specific case (`alexhub2` room0, seam x=1/x=2 at z=3) where the reference wants `Wall_PositiveX_QA` at sector (1,3). Checked whether the data existed anywhere in our own output: it didn't — neither on the "own" (higher-index, x=2) sector's slot 2 nor on (1,3) itself. Digging into the raw block data revealed the real cause: `own` (x=2) has a flat floor (17, no corner data); the **neighbor** (x=1) has the sloped floor (`FloorCorner=[5,0,0,5]`, base 27). The one real compiled quad at that seam (`Yrange=[-5632,-4352]`) matches exactly: -4352 is own's flat floor, -5632 is the neighbor's deepest sloped corner. This is a genuine QA-shaped step — but the *neighbor's* floor is the taller one, not own's.

The existing `hasQaOnOwn`/`hasWsOnOwn` tests (2.1) only checked "own's floor/ceiling exceeds neighbor's" — they never checked the mirrored direction ("neighbor's floor/ceiling exceeds own's"). Per the classic-PRJ storage convention (verified earlier in `PrjLoader.cs`), slot 2/5 (QA) and slot 3/6 (WS) are **always physically stored on the higher-index sector** regardless of which side geometrically owns the step — TombLib decides which side's data it represents at *load* time via `IsFaceDefined`. Since our own classification only tested the "own owns it" direction, any seam where the *neighbor* had the real height difference fell through entirely to Middle, even with perfectly good compiled geometry sitting right there.

**Fix:** extended `hasQaOnOwn`/`hasWsOnOwn` (and their Y-band computation) to also accept the mirrored direction (`neighFloorA < ownFloorA`, etc.), using the same shared physical slot either way — matching PrjLoader's actual storage convention instead of only testing one of its two directions.

**Result:** wall-tier match 90.70% → **92.18%** (biggest single jump of the wall-tier work). The QA Positive/Negative asymmetry is now gone: all four directions sit in a tight 64.4%–67.2% band (previously 9.7%–65%). WS improved and became far more uniform too: 21.1%–27.7% across all four directions (previously 5.7%–22.1%, badly skewed). Overall level coverage: ~72.9% → **~71.4%**

### 2.14 — WS-without-ceiling-diff: hypothesis tested and REJECTED (structural limit, not a bug)

**Hypothesis (carried over from prior session):** WS is "the upper band of any real step," not
strictly ceiling-difference-driven. Confirmed as a real pattern: of 253 reference seams with QA or WS
present, 168 have WS with no ceiling difference at all (vs 85 where WS coincides with a real ceiling
delta). Two candidate fixes were tried and both failed:

**Attempt 1 — defer every QA-without-ceiling-diff seam with 2+ real compiled quads to the existing
rank-based path (FlushUnreliableWallSeams):** wall-tier match 92.18% → 91.99% (regression). WS false
negatives did drop (234→215 / 281→264 across the two directions) but WS/Middle false positives grew
faster (94→150 / 101→180), because the rule forced a QA+WS split on every qualifying seam regardless
of whether the reference actually wanted one there.

**Attempt 2 — check whether real quad count (raw or distinct-texture) separates the seams that
genuinely want a WS split from those that don't**, before trying a narrower version of the same fix.
Measured directly on the 275 seams (our own per-corner test: QA present, no ceiling diff) where the
reference DOES want WS vs the 680 where it doesn't:

| | refHasWs=True (n=275) | refHasWs=False (n=680) |
|---|---|---|
| quadCount=1 | 114 (41%) | 466 (69%) |
| quadCount=2 | 87 (32%) | 200 (29%) |
| quadCount>=3 | 74 (27%) | 14 (2%) |

Distinct-texture quad count gave essentially the same distribution (no improvement over raw count).
Two disqualifying findings: (a) 41% of the true-WS seams have only 1 real compiled quad -- no
rank-based split can ever recover these, since ranking needs 2+ items; (b) at quadCount=2 the two
populations overlap almost exactly (32% vs 29%), so even a stricter ">=2 real quads" gate would still
misfire on roughly as many negatives as it fixed positives.

**Conclusion:** WS-without-ceiling-diff is not derivable from the geometry this extractor reconstructs
(corner heights + compiled quad boundaries). The likely real signal is how the seam was actually drawn
in the original Tomb Editor project (explicit QA/WS split placement), which does not survive TR4
compilation in any form we can recover. Treated as a structural limit alongside Floor2/Ceiling2 (see
2.7) rather than a bug to keep chasing -- do not retry a quad-count-based rule here without a new,
different signal.

### 2.15 — Middle over-assignment "asymmetry": confirmed as spillover from the QA/WS gap, not an independent bug

**Question:** the by-SectorFace-type coverage check shows `Wall_*_Middle` over-assigned 161–256%,
worse on Negative directions (236–256%) than Positive (161–171%). Is this its own bug, or downstream
of the already-known QA/WS gap (QA ~65–67%, WS ~21–28%)?

**Method:** `ApplyWallFace`'s `slot` variable starts at `middleSlot` and is only overridden when the
per-corner test detects a real QA or WS band there -- so any seam where the reference wants QA/WS but
our per-corner test fails to detect it (the same gap already tracked in 2.13/2.14) falls through to
Middle by default, rather than being dropped. Tested directly: for every sector where our export
writes a `Wall_*_Middle` face the reference does NOT have there (a Middle false positive), checked
whether the reference has QA or WS at that *exact same* sector face instead.

| Direction | Middle FP | ...of which ref has QA/WS there instead | |
|---|---|---|---|
| NegativeX | 141 | 107 | 75.9% |
| PositiveX | 132 | 99 | 75.0% |
| NegativeZ | 192 | 139 | 72.4% |
| PositiveZ | 117 | 81 | 69.2% |
| **Total** | **582** | **426** | **73.2%** |

**Conclusion:** confirmed. ~73% of Middle over-assignment is direct spillover from the QA/WS gap --
the Negative/Positive asymmetry simply mirrors which direction has the worse QA/WS coverage (see
2.13's numbers), not a separate Middle-specific bug. Closing the remaining QA/WS gap would
automatically shrink most of this, so **do not attack Middle over-assignment directly** -- it is not
an independently fixable target.

**Remaining ~27% (156 of 582) IS a genuinely separate issue**: Middle written where the reference has
*neither* QA, WS, nor Middle at that sector face at all (not just misclassified -- spurious). Not yet
investigated; a real, smaller, separately-addressable bug for a future session.

### 2.16 — The 156 spurious-Middle cases: one real contributing pattern found (29.9%), majority still unexplained

**Question:** of the 156 spurious Middle entries flagged in 2.15 (ours has Middle, ref has NEITHER
QA/WS/Middle at that exact face), what's actually going on?

**Check 1 — ownership flip:** does the reference instead express the same physical seam via the
MIRROR (opposite-owner) sector face? Only 2 of 156 (1.3%) -- not an ownership-side resolution issue,
ruled out almost entirely. 154 (98.7%) are genuinely phantom on both sides.

**Check 2 — sector Type misclassification.** Deep dive on one concrete case (room 3, sector (3,1),
`Wall_PositiveZ_Middle`) found: our raw TR4 parsing classifies this sector's `Block.Id = 0x0E`
(solid Wall), while the reference classifies the *same sector* as `Type=Floor` with a real Floor
texture -- a genuine sector-classification disagreement, not a texture-layer bug at all. Room 3's
error profile (2.2, "Top 15 rooms": 71/71 errors flat, 0 sloped) is consistent with this kind of
wholesale Type mismatch rather than a per-seam texture-logic issue.

Generalizing this check across all 154 truly-phantom cases (own `Block.Id` is Wall/BorderWall
*and* the reference sector is `Type=Floor`): only **46 (29.9%)** match. Real and worth fixing, but
not the majority explanation -- the room-3 sample doesn't generalize.

**Status:** open. The 46-case Wall/BorderWall-vs-Floor pattern points at `TrLevel`'s raw sector
Id/FloorData classification (upstream of `Prj2Exporter`'s texture logic entirely -- see
`Prj2Exporter.cs`'s `sector.Type` switch), not at wall-texture assignment; fixing it belongs with
sector-type work, not `ApplyWallFace`. The remaining ~108 cases (70.1%) are still unexplained and
spread across 46 different rooms (heaviest: room 27 with 18, room 3 with 16, rooms 24/25/26/28
clustered) -- no second pattern identified yet.

### 2.17 -- Singleton-quad "unreliable" seams were discarding a real, usable ceiling/floor difference

**Investigation:** picked up the 22 flat-floor WS false negatives (out of 216) that DO have a real
ceiling difference (1-9 clicks) our per-corner test should already catch -- distinct from the 194
that are 2.14's structural limit. Sampled 8 concrete cases: 7 of 8 involved a neighbor (or own)
sector with `Id=0x1E`/`0x06` (BorderWall). In every sample, `Mid slot Tipo=7` (assigned) while
`QA slot Tipo=0` and `WS slot Tipo=0` (never even attempted).

**Root cause:** `IsBorderOrSolid` correctly flags these seams as "unreliable" (the neighbor's
Floor/Ceiling is room-boundary-flattened, not real per-sector data -- see 2.1's sub-fix and
`IsBorderOrSolid`'s own doc comment), routing them to `FlushUnreliableWallSeams`'s rank-based path
instead of the per-corner test. But for a seam with only ONE real compiled quad, that path had no
rank to compute and unconditionally defaulted to Middle -- discarding the own/neighbor height data
entirely, even though (per the doc comment's own reasoning) a genuine NONZERO difference there is
real, physically-derived geometry, not the coincidental-equality false-negative case the
"unreliable" flag was actually meant to guard against.

**Fix:** `FlushUnreliableWallSeams`'s singleton-quad branch now attempts the same per-corner QA/WS
test `ApplyWallFace` uses (via the shared `GetCornerFloorY`/`GetCornerCeilY` helpers) before giving
up to Middle: a clean single-sided difference (floor differs but not ceiling, or vice versa) picks
QA/WS; both differing (the lone quad spans the full range) or neither differing still falls back to
Middle exactly as before, so seams the old behavior already got right are unchanged.

**Result:** wall-tier match 92.18% -> **92.38%**, no regression on any tier. QA/WS coverage improved
on 3 of 4 directions (`Wall_PositiveX_WS` 27.7%->30.6%, `Wall_PositiveZ_WS` 22.1%->24.1%,
`Wall_PositiveX_QA` 67.2%->73.4%, `Wall_PositiveZ_QA` 66.9%->70.1%; Negative directions unchanged --
the pattern this fix catches happens to skew Positive-owned in this level). Middle over-assignment
dropped on the same two directions (`Wall_PositiveX_Middle` 170.8%->142.5%,
`Wall_PositiveZ_Middle` 161.3%->138.7%), consistent with 2.15's spillover finding.

### 2.18 — The validation harness itself had a room-matching bug that understated the real score by ~2.4 points (92.38% -> 94.75%)

**Discovery path:** integrated TombIO (TRLevelControl, extracted from TR-Rando, LostArtefacts) as an
independent, engine-accurate cross-check for our own Floor/Ceiling corner reconstruction. A first pass
appeared to show a catastrophic, level-wide bug: 485 sectors with a clean, uniform per-sector height
offset from TombIO's values, concentrated in exactly the rooms already flagged all session as worst
for wall-tier errors (3, 9, 24-28, 43, 52...). Chasing this down (deep dive on room 3, sector (1,1))
found the room-matching predicate used throughout `PrjDiag/Program.cs` --
`Math.Abs(rr.Position.X*1024-r1.X)<1100 && Math.Abs(rr.Position.Z*1024-r1.Z)<1100` -- matches on X/Z
only. **The level has 28 groups of rooms sharing the same X/Z footprint but different Y** (vertically
stacked rooms, e.g. a corridor with a room directly below or above it), and `FirstOrDefault` silently
picks whichever one happens to be first in the reference room list. Every one of the level's most
error-heavy rooms this whole session (3, 9, 24, 25, 26, 27, 28, 43, 52, 102, and more) belongs to one
of these 28 groups.

**This is a bug in the validation harness (`PrjDiag/Program.cs`), not in the production code**
(`TrLevel.cs` / `Prj2Exporter.cs`). Confirmed directly: once the room-match predicate also requires
`Math.Abs(rr.Position.Y + r1.YBottom) < 300`, the "485 uniform-offset sectors" TombIO finding collapses
to **zero**, and the primary wall-tier metric (section 1) jumps from **92.38% to 94.75%** on the exact
same production code, with FN roughly halved (1004 -> 523) and `Mid` FN nearly eliminated (124 -> 17).
No production code changed between these two numbers -- only how the harness paired our rooms with
the reference's.

**Consequence for earlier findings:** any section in this document that investigated a *specific named
room* (2.9's room 2, 2.16's room 3, and others) may have been comparing against the wrong stacked
neighbor for that room. The room's *identity* (X/Z) was right, but if it belongs to one of the 28
X/Z-duplicate groups, the *specific reference data* compared against could have been a different
physical room. Section 1-5's current numbers (94.75% et al.) are now trustworthy; conclusions tied to
a specific room+sector pair from before this fix should be re-verified before being relied on, since
some fraction of them may turn out to be comparing against the wrong stacked room.

**Fix applied:** `PrjDiag/Program.cs` now has a single `RoomMatches(rr, r1)` helper (X, Z, and Y) used
everywhere a reference room is looked up, replacing the ad-hoc X/Z-only inline predicate that had been
copy-pasted across every section. **Never remove the Y term** -- see the file's own header comment.

**TombIO itself has a real, separate bug** worth noting if it's ever used again: `FDControl.
GetTriangulationFloor`/`GetTriangulationCeiling`'s sign-extension (`hadj |= 0xFFF0`) doesn't fully sign-
extend a negative nibble to a 32-bit `int`, producing huge spurious values (~2^24) for certain H1/H2
triangulation-adjustment inputs. Filtered out as `|diff| > 1,000,000` in the (now-removed) cross-check
code; not otherwise consequential to this codebase since TombIO was only ever a diagnostic dependency,
never a production one.

### 2.19 — Real, narrow fix found along the way: outer-ring sectors with a real portal were losing their true Floor/Ceiling

While investigating the (ultimately mostly-false-alarm) uniform-offset pattern, found one genuine,
smaller bug: `ConvertToPrj` unconditionally flattens every sector on a room's outer ring (`j==0 ||
j==NumX-1 || k==0 || k==NumZ-1`) to BorderWall (`Id=0x1E`, `Floor=YBottom`, `Ceiling=YTop`), discarding
the sector's real compiled Floor/Ceiling/FloorData -- with no check for whether a real portal (a
connection to another room) touches that exact sector. Verified concretely: room 3's sector (0,3) sits
in the outer ring (column x=0) but has a genuine wall portal to room 6 on its own row; it was being
flattened despite carrying real walkable floor data.

**Fix:** before the per-sector loop, `ConvertToPrj` now computes `portalTouchedSectors` -- for each of
the room's real portals, the sector(s) on the boundary that portal actually touches (vertical wall
portals mark both sectors adjacent to the portal's grid line; horizontal floor/ceiling portals mark
their exact footprint, no halo). Outer-ring sectors in this set skip the BorderWall-flattening branch
entirely, keeping their real data.

**Impact:** correctness-only for now -- confirmed via raw data dump that the fix changes the preserved
Floor/Ceiling for the affected sectors, but it doesn't move the presence-based wall-tier or coverage
metrics (those only check whether a texture KEY exists, not whether the underlying height is correct),
so it's invisible to every percentage number in this document. Kept anyway since it's a genuine
geometry-accuracy fix, scoped to a small, well-understood case (confirmed: only 4 sectors in room 3 are
actually outer-ring AND portal-touched, out of the 59 sectors a portal's bounding box loosely overlaps).

### 2.20 -- Floor texture Rotation/mirror was always 0: derived and implemented the real formula

**Symptom (reported by Francy):** floor tiles that should be rotated or mirrored in certain spots
all face the same "natural" direction instead, visibly wrong for tileable/directional floor textures
(e.g. a directional plank or relief pattern that should alternate orientation doesn't).

**Root cause:** `SetBlockTexture` hard-coded `blockTex.Rotation = 0` unconditionally, and never set
the mirror-flip bit (`Flags1 & 0x80`) either. `ToPrjTexInfo` also only ever computes a plain
axis-aligned bounding box (`X/Y/Right/Bottom`) from the texture's 4 vertices, discarding their
original per-corner UV *winding* entirely -- so by the time `Prj2Exporter.LoadTextureArea` runs,
the rotation/mirror information the raw TR4 data actually encodes has already been thrown away
upstream, on top of never being computed in the first place.

**Derivation:** `Prj2Exporter.LoadTextureArea` is ported verbatim from TombLib's own
`PrjLoader.LoadTextureArea` decode, so it's an authoritative, already-correct reference for what a
given `Rotation`/flip *produces*. Worked the decode backwards on one concrete real sector (`alexhub2`
room0, sector(1,1)): compared the raw `RoomFace` vertices (world corners) and their paired
`ObjectTexture.Vertices` (UV, vertex-index-for-vertex-index) against the reference's already-correct
`TexCoord0-3` for the same sector, loaded via TombLib. Confirmed self-consistently across all 4
corners: for a Floor quad, TombLib's decode assigns `TexCoord0/1/2/3` to the FIXED world corners
`XnZn/XnZp/XpZp/XpZn` (in that order), reading from a `uv[]` array built from the texture's own
axis-aligned bounding box (`uv[0]`=top-left, `uv[1]`=top-right, `uv[2]`=bottom-right, `uv[3]`=bottom-
left) -- after first swapping `uv[0]<->uv[1]` and `uv[2]<->uv[3]` if the mirror-flip bit is set, then
cyclically rotating the array by `Reff=(Rotation+2)%4` steps. Solving for the box-index the raw data
assigns to world corner `XnZn` gives `Rotation=(1-bXnZn) mod 4` in the non-mirrored case (a mirrored
case is detected separately: the 4 corners' box-indices decrease by 1 around the cycle instead of
increasing, matching a reversed winding).

**Fix:** added `ComputeFloorQuadRotation` (`TrLevel.cs`) -- for a quad Floor face, finds each of the
4 world corners' raw UV (by matching `RoomVertex` min/max X/Z against `face.Vertices[]`, paired
position-for-position with `ObjectTexture.Vertices[]`), determines which of the 4 raw UVs maps to
which box corner, and applies the derivation above. Returns `(0, false)` -- the old, safe default --
for any face whose winding isn't a clean rotation or mirror of its own bounding box, rather than
guessing. `SetBlockTexture` now takes optional `rotation`/`flip` parameters and actually sets
`blockTex.Rotation`/`Flags1 & 0x80` instead of hard-coding them. Wired in only for quad (non-
triangle) Floor faces (slot 0) in `ApplyRoomFaceTexture`'s floor/ceiling branch, computed once per
face (rotation/mirror is a property of the whole face's UV mapping, not per covered sector) --
Ceiling and triangulated Floor pieces are unaffected for now (still `(0, false)`, same as before);
the same derivation would need to be redone separately for those, since `LoadTextureArea`'s decode
differs for them (different baseline offset, different final TexCoord-index mapping, and for
triangles an entirely different 3-step rotation cycle).

**Result:** validated directly (not just via the wall-tier/coverage metrics, which only check texture
KEY presence and wouldn't show this at all) by comparing our exported `TexCoord0-3` against the
reference's for every Floor quad sector both sides have: **850/857 (99.2%) now match exactly**, up
from an unmeasured but clearly much lower baseline (every one of these was previously wrong whenever
the real rotation/mirror wasn't coincidentally 0). The remaining 7 mismatches (rooms 52, 112, 156,
164) show a *completely different* texture region, not just a wrong rotation of the same one --
indicating a separate, pre-existing "wrong quad selected" issue at those specific sectors, unrelated
to this fix and not investigated further this session.

**Ceiling: first attempt failed (0%), then solved to 89.2% (306/343) -- and how, since the dead ends
are instructive.**

*Attempt 1 (0%, reverted):* applied the same method as Floor -- read `LoadTextureArea`'s Ceiling
branch (no "+2" baseline, final TexCoord indices [2,1,0,3] instead of Floor's [3,0,1,2]), derived a
world-corner assignment from 3 real flat Ceiling sectors (rooms 8 and 10), wired it in. Validation
gave 0/343 exact matches, even for sectors the function itself assigned Rotation=0. The 3 derivation
samples had all happened to be `Rotation=0` identity cases, so the derivation was never actually
tested against a non-trivial rotation.

*Attempt 2 (47.2% best, methodologically flawed):* a brute-force search over all 668 real flat quad
Ceiling sectors -- but it scored candidates by comparing against a per-sample `requiredReff` derived
from the reference's own box-index at slot 0 alone, which is not the right objective (it doesn't
simulate the full 4-slot decode). Its 47.2% ceiling was an artifact of that scoring, not evidence
the approach was unfixable, and led to the wrong conclusion that the cause was inside
`Room.BuildGeometry` in the precompiled DLL.

*What actually unblocked it:* Francy pointed out the full TombLib source is available locally
(`C:\Users\Checkm8ra1n\Desktop\Projects\C#\Tomb-Editor\TombLib`). Reading it directly settled the
theory: `RoomGeometry.BuildFloorOrCeilingFace`/`AddQuad` use the SAME fixed world-corner-to-vertex
order for Floor and Ceiling, and `Sector.GetFaceTexture`/`SetFaceTexture` are plain pass-throughs
(no hidden swap) -- so Ceiling's STORED `TexCoord0-3` use the exact same convention as Floor's
(`TexCoord0=XnZn, 1=XnZp, 2=XpZp, 3=XpZn`). The `TexCoord0<->TexCoord2` "ceiling swap" in
`RoomGeometry.Build` only touches the render-mesh triangle list (a backface-culling concern), not
the stored value. That ruled out the BuildGeometry hypothesis and pointed back at the scoring.

*Attempt 3 (what shipped):* redid the brute-force search with a CORRECT objective -- for each real
sample and each candidate `(reference corner, constant k, flip)`, compute a predicted Rotation,
simulate the FULL Ceiling decode (all 4 TexCoord slots, using `LoadTextureArea`'s actual rotate loop
and Ceiling index array [2,1,0,3]), and require all 4 slots to match the reference's box-index
pattern. Result, split by whether `ComputeCornerBoxIndices` sees a pure-rotation winding (box-index
steps of +1 around the corner cycle) or a reversed one (steps of -1):
  - non-reversed winding: `Rotation = (0 - bXpZn) % 4`, flip=false -- 248/338 (73.4%) on its own
  - reversed winding:     `Rotation = (3 - bXpZn) % 4`, flip=false -- 312/330 (94.5%) on its own
  - never setting the flip bit outperformed setting it, for both groups
Notably the "reversed"/"mirrored" label is a misnomer for Ceiling: it correlates with the compiled
quad's own winding direction for a downward-facing polygon (a structural fact about how the TR4
compiler emits down-facing faces), not with an artist choosing to mirror the texture -- which is why
Floor's "swap-based mirror" handling (and the flip bit) doesn't carry over.

**Result (real export, full validation):** `Ceiling` quad faces: **306/343 (89.2%) exact
`TexCoord0-3` match** against the reference, up from 0%. `Floor` unchanged at 850/857 (99.2%),
wall-tier unchanged at 94.75% -- no regressions. The residual ~11% (37 sectors) is not understood
(possibly a third winding subgroup, or the kind of "wrong quad selected" issue also seen in Floor's
7 mismatches); `ComputeCeilingQuadRotation`'s doc comment records this. Kept flat-quad-only, like Floor.
**Triangles (Floor_Triangle2/Ceiling_Triangle2) not attempted this session** -- `LoadTextureArea`'s
triangle branch is a materially different decode (`blockTex.Triangle` 0-3 picks which 3 of the 4 box
corners are used, a separate 3-step rotation cycle via `%3`, and `SplitDirectionIsXEqualsZ`-dependent
baselines that differ again between Floor/Ceiling/Ceiling_Triangle2) needing its own from-scratch
derivation, not a simple extension of the quad formula above.

### 2.21 -- Lone-quad WS seams: the quad's real Y range IS a signal (partially overturns 2.14)

**What 2.14 missed:** it tested quad *count* only. Measured with a throwaway probe (`PrjProbe`, not part of
the solution): on 466 interior seams covered by exactly one compiled quad, with a floor step (QA) and no ceiling
step by our per-corner test, 77 have WS in the reference -- and **none of those 77 has a QA face at all** (the
reference holds only WS there). Cross-tab of the quad's Y range against the floor-step band `[loF,hiF]` (built
as in `ApplyWallFace`, tolerance 8 units):

| quad minY vs loF | quad maxY vs hiF | reference WS | no WS |
|---|---|---|---|
| 0 (matches) | 0 (matches) | 2 | 348 |
| below | below | 60 | 20 |
| 0 | below | 11 | 4 |
| below | 0 | 3 | 11 |
| 0 | above | 0 | 6 |
| below | above | 1 | 0 |

A quad that coincides with the step band is QA (2 WS out of 350). A quad on the smaller-Y side with its far end
short of the band's far end is the reference's WS (71 of 95). Note this population (77/389) differs from 2.14's
(114/466); the probe's seam selection does not replicate 2.14's exactly, so the direction of the signal is solid but
the exact percentages are indicative.

**Fix:** `QuadAboveFloorStep(minY, maxY, loStep, hiStep)` = `maxY < hiStep - 8 && minY <= loStep + 8`. When a seam has
exactly one compiled quad, a real floor step and no ceiling step, such a quad goes to `wsSlot` instead of `qaSlot`/Middle.
Applied in both paths: `ApplyWallFace` (reliable neighbor) and the lone-quad branch of `FlushUnreliableWallSeams`.
`minY`/`maxY` are now carried through the `pending` tuples.

**Result (PrjDiag):** wall-tier **94.75% -> 95.18%** (FP 1020 -> 957, FN 523 -> 460). WS FN 191/213 -> 158/175;
QA FP 205/220 -> 184/196; QA FN +7/+1 (small cost). Per-face coverage table unchanged: those numbers are
direction/ownership-sensitive, and the extra WS seams land on the opposite ownership side from the reference (see
the ownership question, still open).

**Not done:** the `0/below` (11 vs 4) bucket is included in the rule; the `below/0` bucket (3 vs 11) is not. No attempt
for seams with 2 quads. Rule not yet checked on a second level.
### 2.22 -- Border/solid lone-quad seams, and the metric-vs-deliverable trap (the tier metric is NOT the deliverable)

**Census of the 333 WS false negatives (PrjProbe, throwaway):** ~270 are on seams where one side is a room-border
or solid sector (reference types BorderWall/Floor, Wall/Floor). The reference's hidden wall-sector heights
(WF = wall floor, WC = wall ceiling; stored in the PRJ2 even though TR4 has no such data) partition the REAL
sector's floor-to-ceiling span: QA below WF, Middle between WF and WC, WS above WC. The compiled quad's extent
against the real side's floor/ceiling reveals which tiers exist. On 361 one-sided lone-quad seams: quad spans
floor+ceiling -> reference WS 175 / QA 41 / none 33 / QA+WS+Mid 3 (a Middle-only face never occurs in the
reference); touches floor only -> QA 46 / none 37 / WS 1; touches ceiling only -> none 13 / WS 9 / QA 2.

**Fix 1 (`TrLevel.ApplyWallFace`):** new `borderSide` (1 = own is border/solid, 2 = neighbor is). `IsBorderOrSolid`
was only ever checked on the NEIGHBOR, so own-side border seams went through the per-corner test with flattened
heights. For a lone quad with exactly one unreliable side: touches ceiling (incl. spans both) -> WS, touches floor
only -> QA, else Middle.

**The trap:** after Fix 1 (and 2.21) the label metric rose (94.75% -> 96.01%) while the number of wall faces
actually textured in the EXPORTED prj2 (PrjDiag section 5, sum of `Wall_*` rows) FELL: 1866 -> 1823 (2.21) ->
1671 (Fix 1). Cause: `Prj2Exporter` only writes a slot's texture when TombLib defines that tier face on OUR
geometry, and our Wall/BorderWall sectors are exported with flat Floor=YBottom/Ceiling=YTop, so a full-height
wall is a single Middle face for us (the reference reaches the same visible wall through WS). A texture labelled WS
on a seam where only Middle is defined was silently dropped. Tier labels only matter where the exported geometry
defines that tier.

**Fix 2 (`Prj2Exporter.PlaceWallSeam`):** per seam, a texture whose own tier face is not defined is moved to the
nearest defined tier (order QA, Middle, WS) that has no texture of its own; same-tier placement is unchanged
(own Negative face if defined, else neighbor Positive).

**Result:** textured wall faces in the exported prj2 **1866 -> 2162** (reference defines 3487), label metric 96.01%
(FP 835, FN 338). Floor/Ceiling rows unchanged.

**Rule for future work:** judge wall changes by BOTH numbers. The label metric (PrjDiag section 1) measures
agreement with the reference's tier labels; the textured-face count (section 5) measures what Tomb Editor will show.
The ~1300 reference wall faces we still lack mostly come from hidden wall-sector heights we do not synthesize.
### 2.23 -- What is left on walls: compiled quads without a face to carry them (placement census)

**Measured (PrjProbe, now the placement census; reloads our exported prj2 and counts per interior seam):** every one
of the 1904 interior seams covered by a compiled quad gets at least one wall texture in our Blocks (100%), so the
slot assignment is not what is missing. The gap is in the exported geometry: of 3057 compiled quads that need a face
(capped at 3 per seam), our flat Wall/BorderWall sectors define a face for only 1820 (textured: 1819). Deficit: **1237
quads**.

| compiled quads on the seam | faces our geometry defines | seams |
|---|---|---|
| 1 | 1 | 611 |
| 2 | 1 | 430 |
| 3 | 1 | 227 |
| 4+ | 1 | 109 |
| 2 | 2 | 67 |
| 4+ | 3 | 46 |

Seams with 2/3/4+ stacked quads but one defined face: 766 (430+227+109). Cause: `Prj2Exporter` flattens Wall/BorderWall
sectors to Floor=YBottom, Ceiling=YTop, so TombLib builds a single Middle face; the TR4 mesh (like the reference, whose
hidden wall heights split the span into QA/Middle/WS) has up to 3 stacked quads, and the extra textures have no face.
This also explains why Middle is over-assigned (about 2.5x the reference) and WS/QA under-assigned in the per-face table.

**Not done (decision pending):** synthesize hidden wall-sector heights from the sorted quad boundaries of each seam
(n=2: WF=WC=boundary -> QA+WS; n=3: WF, WC = the two boundaries -> QA+Middle+WS). Expected: QA+Middle+WS always tile
[floor, ceiling], so the visible wall shape does not change; only texture band boundaries do. Known risks: a wall
sector corner is shared by two seams (X and Z edges) that may want different split heights, and the Floor/Ceiling
corner sign/unit conventions of TombLib would have to be re-derived. Seams with 4+ quads can carry at most 3 faces.
### 2.24 -- Hidden wall-sector heights synthesized from the compiled quad stack (closes most of the 2.23 gap)

**Oracle (throwaway probe against `alexhub2_orig.prj2`):** on seams between a reference Floor sector and a reference
Wall/BorderWall sector with 3 compiled quads and a straight wall edge, the wall's stored floor/ceiling (WF/WC) equal
the quad boundaries (WF = top of the lowest quad, WC = bottom of the highest) in **235 of 248** cases. Single quads match
WF/WC with an edge in 334 of 374. For 2 quads the reference mostly has ONE face (377 of 456 seams): TombLib's compiler
splits a tall face at its midpoint into two quads, each half with its own object texture. Heights are in the same frame as
the sector heights the exporter writes (`-TR Y + room YBottom`).

**Implementation (`Prj2Exporter.SynthesizeWallHeights`, called before `NormalizeRoomY`):** for every seam with exactly one
Wall/BorderWall sector and one real sector and 2+ compiled quads, set the wall sector's two edge corners:
2 quads -> WF = WC = their shared edge (QA + WS); 3+ quads -> WF = top of the first, WC = bottom of the last (QA + Middle + WS).
Values rounded to 256. A wall corner shared by two seams keeps the first claim. QA + Middle + WS always tile the real
sector's full height, so the visible wall shape is unchanged; only the texture bands move.
`TrLevel.ApplyWallFace`: an OWN-side border sector with 2+ quads now also uses the rank-based QA/Middle/WS pass (before,
all its quads wrote to the same Middle slot and overwrote each other).

**Result (PrjDiag + placement census):**

| | before | after |
|---|---|---|
| compiled quads with a defined face in the exported prj2 | 1820 / 3057 | 2754 / 3057 |
| textured wall faces | 2162 (reference defines 3487) | **3256** |
| wall-tier label metric | 96.01% (FN 338) | **96.52%** (FN 179) |
| WS coverage per direction | 21-31% | 87-92% |

Floor/Ceiling rows unchanged. QA is now 140-148% of the reference (we texture each half of a split tall face, the
reference has one authored face) and Middle 156-173%: expected, those are real compiled quads.

**Left:** 303 quads still lack a face: 156 seams with 2 quads over one defined face are, almost certainly, two halves of
a single tall QA face between two REAL sectors (TombLib cannot split a face, so one half cannot be textured);
the rest are 4+ quad stacks capped at 3 faces. About 23 seams define a face without a texture (20 with 1 quad over 2
faces). Floor2/Ceiling2 stay a structural limit (2.7). Only alexhub2 has been checked.
### 2.25 -- Geometry fidelity check of the synthesized wall heights (and what it did NOT fix)

**Method (`PrjProbe`, now the fidelity probe; the 2.23 placement census is in git history at 2c472bb):** reload the
exported prj2 with TombLib, take the Y extent of every defined QA/Middle/WS face on each Wall/BorderWall <-> real sector
seam from TombLib's own `RoomGeometry` (`GetFaceLowestPoint`/`GetFaceHighestPoint`), and compare with the compiled TR4
quads of the same seam (absolute Y, tolerance 40). Run on the 2.23 baseline (2c472bb) and on 2.24.

| verdict (seams) | baseline 2c472bb | 2.24 |
|---|---|---|
| same extent, faces == quads | 4 + 29 + 71 + 236 | 14 + 192 + 284 + 236 |
| same extent, faces merge/split quads | 149 + 211 + 284 + 15 | 139 + 48 + 71 + 15 |
| face extends beyond TR4 stack | 82 | 82 |
| HOLE (a TR4 quad not covered by any face) | 31 | 31 |
| NO face defined | 27 | 27 |

(per quad-count bucket 4+/3/2/1.) The synthesis only converts "merged" seams into one-face-per-quad seams; the extension,
hole and no-face counts are identical before and after, so it introduced no new geometry error. Exact samples of the
remaining cases: a real sector's ceiling at 7936 with the TR4 stack ending at 6912 (our WS/Middle reaches the ceiling, the TR4
mesh stops, probably a portal opening above) and seams where a shared wall corner was claimed by another seam
(`R0 (7,1)Z`: TR4 stack 4352..7936, ours one face 5376..7936). These are PRE-EXISTING (the flat placeholder heights
produced the same counts) and are not part of the texture work.

**Not verified visually:** Tomb Editor does load the exported prj2 (project opened without error), but the desktop
screenshot tool returned more than its size limit, so there is no eyeball check of the room views. The check above is the
substitute.
### 2.26 -- Wall quad rotation/mirror derived directly from the TR4 corner UVs

**Problem:** wall faces always had rotation 0 and no mirror (floors/ceilings got theirs in 2.20). PrjProbe (now the wall UV
check) compares TexCoord0-3 of every wall face that both our exported prj2 and the reference have textured at the same
sector and key: 911 of 2378 exact (38.3%); about 280 more covered the same UV region with the corners in a different order
(rotation or mirror).

**TombLib facts (read from TombLib/LevelData/SectorGeometry and RoomGeometry.AddQuad):** a wall quad is built as P0 = top at
the wall's start, P1 = top at its end, P2 = bottom at its end, P3 = bottom at its start (the same for QA, Middle and WS).
Start/end by direction: PositiveX z..z+1, NegativeX z+1..z, PositiveZ x+1..x, NegativeZ x..x+1. `AddQuad` maps P0..P3 to
TexCoord1, 2, 3, 0, and with the `LoadTextureArea` decode that puts texture-box corner (j - rotation) mod 4 on Pj
(0=TL, 1=TR, 2=BR, 3=BL; mirrored: the box index is XOR 1).

**Fix:** `Prj2Exporter.TryComputeWallQuadOrientation`. It takes the raw TR4 face (`BlockTex.SourceFace`/`SourceTexture`, new,
set by `SetBlockTexture`; `RoomFace.Owner`, new, set in `ConvertToPrj`), finds the corner at each of P0..P3, reads its raw UV
(`>> 8`), classifies it into the texture box and solves for rotation and mirror. It runs at export time because the same
TR4 quad needs opposite orientations on a sector's Negative face and on the neighbor's Positive face (start and end swap).
Pure rotation or pure mirror only; anything else, triangles and missing sources keep the old rotation 0.

**Result (alexhub2):** exact **911 -> 1147 of 2378 (38.3% -> 48.2%)**; same region but different corner order about 280 -> 49,
of which 34 are triangular wall faces (not handled) and 15 are quads. The tier label metric is unchanged (96.52%).
The remaining mismatch is mostly different regions (about 1180), see the 2.23/2.24 analysis: halves of split faces,
tier labels, and 268 faces whose reference texture is absent from the seam.

**Left:** triangular wall faces (34 wrong orientation), the 15 leftover quads, and floor/ceiling TRIANGLES, which the same
direct method can cover.
### 2.27 -- Floor/ceiling TRIANGLE texture coordinates derived directly from the TR4 corner UVs

**Why a new metric:** comparing TexCoord order face by face mixes two things: faces whose triangle geometry is the same in
our room and the reference, and faces where it is not. PrjProbe (now the render-equivalence probe) therefore reloads both
prj2 files, builds TombLib's `RoomGeometry` and compares the UV that ends up on each (X, Z) vertex of a face, only for faces
with the same vertex set.

**TombLib facts (RoomGeometry.AddTriangle, the ceiling reversal loop, Compilers/Rooms.cs):** vertex Pj of a triangle gets the
face's TexCoordJ. For ceilings, RoomGeometry reverses the vertex order after building (and swaps TexCoord0/2 in
`TriangleTextureAreas`), and the compiler undoes it with `texture.Mirror(true)`, so the STORED TexCoordJ of a ceiling
triangle belongs to geometry vertex 2 - J.

**Fix (`Prj2Exporter.TryComputeTriangleTexCoords`):** for a triangular floor/ceiling face, take the three vertices from
`RoomGeometry.VertexRangeLookup`, match each by its (X, Z) corner to the compiled TR4 triangle's vertex, read that vertex's raw
UV (`>> 8`), classify it onto a texture-box corner (0=TL, 1=TR, 2=BR, 3=BL; the three must be distinct) and use that
box corner as the TexCoord (reversed for ceilings). The old Triangle-index / split-direction / rotation decode stays as the
fallback when a vertex does not match, the source is not a triangle or the UVs are not on three distinct corners.

**Result (alexhub2, same-vertex-set faces; faces whose triangle geometry differs from the reference are not comparable):**

| face | old decode: renders same / differs | direct: renders same / differs |
|---|---|---|
| Floor (triangle) | 45 / 126 | **123 / 48** |
| Floor_Triangle2 | 4 / 156 | **48 / 112** |
| Ceiling (triangle) | 5 / 11 | **15 / 1** |
| Ceiling_Triangle2 | 0 / 13 | **1 / 12** |

Overall 54 -> 187 matching faces. TexCoord-order exact match over all floor/ceiling faces 59.9% -> 67.5% before the ceiling
fix. The tier-label metric is unchanged (96.52%).

**Not explained (do not chase without new information):** 173 floor/ceiling triangles still render as an exact
horizontal mirror (flipU) of the reference. Raw TR4 triangles have the same handedness (UV order against world order) in
essentially every case, while the reference has the opposite handedness on those faces, and the TR4 compiler
(TexInfoManager) only rotates, never mirrors, so the information is not in the TR4. The direct method reproduces what
the TR4 file shows; the reference's mirror is probably an authoring choice or a compile quirk we cannot see.

**Also found, not looked at:** 686 floor/ceiling triangle faces whose vertex set differs from the reference
(334 Floor, 315 Floor_Triangle2, 20 and 17 ceiling), i.e. our exported sector splits the surface into triangles differently.
About 70 faces (Floor 32, Ceiling 22, Ceiling_Triangle2 17) fall back to the old decode because a vertex did not match.
Ceiling QUADS: 33 of 444 still render differently from the reference (25 as a pure vertical mirror).
### 2.28 -- The 686 "different" triangle faces, the ceiling split direction, and a rejected portal-opacity fix

**Where the 686 come from:** floor/ceiling triangle faces whose vertex set differs between our prj2 and the reference
(2.27). Heights and `DiagonalSplit` are equal on those sectors; `SplitDirectionIsXEqualsZ` differs on about 60% of them.
PrjProbe (now the triangulation probe) compares each sector's floor/ceiling triangles with the TRIANGLES COMPILED IN THE TR4
(horizontal faces with a 1024 x 1024 extent, classified floor/ceiling by height) instead of with the reference.
Result: where the vertex set differs from the reference, OUR triangulation agrees with the TR4 in 451 of 491 floor sectors, the
reference's own TombLib geometry in none of them. So on those faces the reference prj2 and the compiled TR4 disagree and our
export is not at fault (building the reference with TombLib's legacy geometry to test a compile-mode explanation crashes in
`LegacyWallGeometry` on this level, so that explanation is untested).

**Real defect found: ceiling split direction.** `SplitDirectionIsXEqualsZ` was only set from FloorData triangulation
functions; for ceilings it disagreed with the TR4 mesh. Of the ceiling sectors with two compiled triangles, 17 of 30 differed.
`Prj2Exporter.ApplyCompiledTriangleSplits` now sets the flag of a non-planar floor/ceiling sector with `DiagonalSplit.None` from the
diagonal the TR4 triangles actually use (two triangles whose missing corners are opposite; missing XpZn or XnZp -> diagonal
XnZn-XpZp -> x == z). `SectorSurface` is a struct, so the flag is written through `sector.Floor` / `sector.Ceiling`.
Result: 17 -> 0 ceiling sectors that differ; the 414 floor sectors with two triangles stay equal to the TR4. Tier-label metric and
per-face coverage are unchanged; ceiling triangle "different vertex set" faces against the reference drop from 37 to 3.

**Remaining floor mismatches with the TR4 (not fixed):** 39 sectors have two triangles in the TR4 but one in our room, because a
triangular floor portal (`GetFloorRoomConnectionInfo` -> TriangularPortalXnZn/XpZn/XnZp/XpZp) makes `RoomGeometry` drop the
triangle over the portal; and 30 sectors have one triangle in the TR4 and two in ours. That is the cause of most of the missing
`Floor_Triangle2` textures (486 of 548).

**Rejected: `PortalOpacity.TraversableFaces` on portals that still show faces.** Setting it fixes the 39, but `HasTexturedFaces`
is per portal, not per sector, so every sector of the portal then gets its faces. Measured on the exported prj2: it added 185
floor faces textured in ours only, over portals the reference leaves at `Opacity None`, i.e. floor drawn over portal holes.
In the reference 237 faces belong to `TraversableFaces` portals and others to `None`, with no sector-level evidence that tells
them apart. Reverted. Revisit only with a per-portal signal (for example the door quad's own texture, if the TR4 keeps one).
### 2.29 -- Triangular WALL faces use the direct TexCoord derivation too

The triangle method of 2.27 (`TryComputeTriangleTexCoords`) was written for floors and ceilings, which tell their three corners apart by (X, Z).
A wall triangle has vertices that share (X, Z) and differ only in Y, so the match returned "ambiguous" and the face fell back to the old
decode. The matcher now also compares Y when two source vertices share (X, Z): TR4 Y grows downward, TombLib's `VertexPositions` are relative to the
room and grow upward, so the test is `-rawY` against `position.Y + room.Position.Y` (tolerance 16). For triangular wall faces TombLib's
AddTriangle uses the same vertex-to-TexCoord relation as for floors (vertex Pj gets TexCoordJ), so nothing else changes.

**Result (wall UV check, same sector and key as the reference):** wall triangles with the right UV region but a different corner order
34 -> 6 (QA 5, WS 1); exact TexCoords 1147 -> **1170 of 2378 (49.2%)**; the 15 leftover quads are unchanged. Tier-label metric unchanged
(96.52%).
### 2.30 -- Generalization check on 29 other TR4 levels (no reference prj2 available) and the flag comparison

**Flags:** for the 4910 face keys that both our export and the reference have textured, DoubleSided and BlendMode are equal in all of them
(everything is Normal / not double-sided in the reference, so alexhub2 does not exercise additive or double-sided textures; this
says nothing about levels that use them).

**Method:** every number so far was measured on alexhub2. PrjProbe (now the generalization probe; set `PROBE_TR4` to a .tr4 path) exports
a level with the real `Prj2Exporter`, reloads it with TombLib and compares OUR exported geometry with the compiled TR4 itself, so no
reference is needed: (1) wall quad-sector pairs (capped 3 per seam) that end up with a defined AND textured face, (2) wall faces vs the
compiled quads (hole / face beyond the TR4 stack / no face), (3) floor/ceiling triangulation vs the compiled triangles. It was run on the 28
original TR4 game levels (`Tomb-Raider-4\01..29`, compiled by Core's tools, never touched by TombLib) and on `TEST1.TR4`; alexhub2 as the control
reproduces the earlier numbers exactly.

**Result (28 levels, 0 crashes):**
- Wall quads with a defined, textured face: **77.6% to 98.2%, about 92% typical** (alexhub2: 89.9%). The weakest are 12-Desert-Railroad
  (77.6%, 114 export warnings) and 29-Menkaures-Pyramid (85.6%).
- Sectors with two compiled triangles whose triangulation differs from ours: **10 of about 6000** (floor and ceiling together).
  Every other level reports 0. So the split-direction fix of 2.28 and the floor behavior hold outside alexhub2.
- Wall geometry vs the compiled quads: holes 1-66 seams per level and "face beyond the TR4 stack" 1-148, except 07-Temple-of-Karnak with 228
  holes; the same two kinds of defect as on alexhub2 (2.25), not new ones.
- Export warnings are mostly "Portal overlaps another" (up to 114 on 12-Desert-Railroad); they belong to the portal export, not to the textures.

**Not covered:** orientation of the textures (rotation/mirror) and the tier labels need the reference, so they are only validated on alexhub2.
These levels are original TR4 files, not TombLib compiles, so a level compiled by TombLib from a prj2 is the closer match to alexhub2.
### 2.31 -- Floor/ceiling QUAD texture coordinates derived directly too (28 ceiling quads fixed)

**Attribution first:** the 33 ceiling quads that rendered differently from the reference (2.27) could be our error, a TR4/reference
disagreement like the floor triangles, or both. PrjProbe (now the quad attribution probe) compares, for single-sector quads, the position of each
corner's UV inside the texture box in three places: the TR4 raw face, what TombLib draws for our face and what it draws for the reference.
Ceiling quads (378): ours == TR4 in 344, differs in 34; in 28 of those the reference == TR4, so those 28 were OUR errors in the old
rotation/mirror arithmetic. Floor quads (916): ours == TR4 in 908; the 8 that differ (and 6 ceiling ones) also differ from the reference under this
normalization, probably symmetric or degenerate UV boxes; not investigated.

**Fix (`Prj2Exporter.TryComputeFloorCeilingQuadTexCoords`):** the same direct method as the triangles. `AddQuad` gives TexCoord((j + 1) mod 4) to
corner pj and stores the vertices as p1, p2, p0, p3, p0, p2; for ceilings the first and last vertex of each triangle are then swapped, giving
p0, p2, p1, p2, p0, p3. The corner positions are read from `RoomGeometry` at those slots, matched by (X, Z) to the TR4 quad's vertices, and each raw UV
(`>> 8`) is classified onto a box corner. Only quads that span exactly one sector; anything else (multi-sector quads, unmatched corners) keeps the
old arithmetic.

**Result (alexhub2):** ours == TR4 for ceiling quads 344 -> 372 of 378 with no case left where the reference agrees with the TR4 and we do not; ceiling quads
rendering differently from the reference 33 -> 4; floor quads unchanged (1042 same, 908 == TR4). Tier-label metric unchanged (96.52%). The
generalization probe of 2.30 still runs on 01-Angkor-Wat and 12-Desert-Railroad with the same wall coverage and no crash.
### 2.32 -- Portal areas: the bounding-box union made holes in the floor and ceiling

**Cause of the "holes":** `Prj2Exporter` grouped all raw doors with the same (direction, target room) and added ONE portal covering the
bounding box of their sector areas. When the doors to the same room were not contiguous (several authored portals between the same two rooms,
common with flooded rooms), the box also covered sectors the TR4 never opened, so TombLib treated them as portal and built no floor/ceiling there.
PrjProbe (now the portal-area probe) compares which sectors carry a floor/ceiling portal in our export and in the reference.
Before: floor portal sectors in both 1094, **64 extra** in ours; ceiling 1007 in both, **24 extra**; none missing.

**Fix (`Prj2Exporter.DecomposeIntoRectangles`):** the grouping now collects the SECTOR CELLS of every door and splits that set into disjoint rectangles
that cover exactly those cells (row-wise greedy, first along Z then along X). A single rectangle comes back unchanged; no portal covers more than
the union of its doors, and the rectangles of one group cannot overlap each other.
After: floor portal sectors in both 1094, **0 extra, 0 missing**; ceiling 1007, **0 extra, 0 missing**. The portal rectangles in the cases inspected equal
the reference's own rectangles. The 88 holes are gone. Tier-label metric and texture coverage are unchanged (96.52%; Floor 1648/1782).
On 28 original levels the wall coverage and the number of skipped portals are the same, except 03-The-Tomb-of-Seth: 34 -> 46 warning lines, because a group that
was already skipped as one "Portal overlaps another" (a flipped room overlapping another portal) is now reported once per rectangle; nothing new is lost.

**Textures over portals: why NOT cover them.** What remains "missing" in the floor table is the faces over portals to WATER rooms. The TR4 does
contain a quad there (double-sided, attribute 2, one of ~16 animated object textures #2234-#2244), but it is the compiler's automatic water surface,
not an authored face: with the reference's own prj2, normal-over-water floor portals with `Opacity None` have such a compiled quad in 159 sectors and
with `TraversableFaces` in 146, so the TR4 cannot tell them apart. Setting `PortalOpacity.TraversableFaces` wherever the TR4 draws a quad gave 186
floor faces textured in ours only (and 200 -> 38 missing), i.e. a floor drawn over portals the reference leaves open. Tried twice (per portal "any drawn", "all
drawn", "drawn and no hole") and removed. A portal is a legitimate opening; what the player sees there is the water surface, which TombLib creates.
### 2.33 -- Wrong reference rooms in every comparison, and the second triangle of sloped sectors

**The comparison bug (affects the numbers of 2.7-2.31 that use the reference):** every probe and `PrjDiag` picked the reference room with
`FirstOrDefault` over a position match (X, Z within 1100, Y within 300). Flipped/alternate rooms share a position: **40 of 177 rooms have 2 or 3
candidates, and in 20 of them the first candidate is not the room that fits** (same footprint, closest floor heights). Those rooms were compared with
the wrong twin. `PrjDiag` now uses `BestRef` (same footprint, minimum floor-height difference, per-sector clamp 4096). Effect on the primary metric:
**96.52% -> 97.01%, FP 845 -> 800, FN 179 -> 87**, 29397 -> 29631 compared seams. The probes that load our prj2 index it by room (`ours[i]` is exactly
TR4 room i). Percentages in 2.23-2.31 that compared against the reference were measured with the first-candidate rule and may move by a few points; the
checks against the TR4 itself (2.25, 2.28, 2.30, 2.31's attribution) and the geometry facts do not depend on it.

**What is really missing on floors (correct matching, `PrjProbe`):** the reference textures 2330 floor faces (Floor + Floor_Triangle2) and we texture 2149
of them (92.2%). 218 gaps remained: 137 flat floors over full portals and 39 over triangular portals, all `TraversableFaces` portals to water rooms (the TR4
quad there is the engine-made water surface, see 2.32; not recoverable), and **42 faces in non-planar SLOPED sectors where our geometry defines the
triangle but it had no texture**.

**Cause of the slope gaps:** a non-planar sector is two triangles in TombLib. When the TR4 only compiled ONE of them (the other one touches the ceiling, zero
height, or was not compiled) there is no texture to copy, and the other face stayed bare although the reference textures it.

**Fix (`Prj2Exporter.FillMissingSplitTriangles`):** both triangles are halves of one texture square, so the bare one is completed from its textured sibling:
the two shared corners keep the sibling's UV and the remaining corner takes the fourth corner of the sibling's UV right triangle (P + Q - R, R the
right-angle vertex), mapped through the same stored-TexCoord/vertex relation as 2.27 (reversed for ceilings). Applies to Floor/Floor_Triangle2 and
Ceiling/Ceiling_Triangle2 when exactly one of the two defined triangular faces is textured.
Result on the export: `Floor_Triangle2` 486 -> **550** textured faces (reference 548), `Ceiling_Triangle2` 30 -> **38** (reference 38); the 42 bare slope
faces drop to 5 (triangular portal sectors where the face is defined next to a portal). Tier metric unchanged.

**Honest limit:** this is a continuation, not recovered data. For the 37 completed faces the render probe could compare with the reference, none renders the
same as the reference (mostly "other" mappings, not a simple mirror): the reference's authors used a different layout on the missing triangle. Visually the
sector is covered by one continuous texture; it is not the original's choice. There is no TR4 information to do better.
### 2.34 -- Wall orientation leftovers, re-measured with the correct reference rooms

**Re-measure (wall UV check with `BestRef`-style matching, 2.33):** 2346 wall faces textured in both our export and the reference, **1148 exact (48.9%)**;
only **20** more cover the same UV region with the corners in a different order (11 quads and 4 + 5 triangles); the other ~1180 are different regions (halves of
split faces, tier labels), analysed in 2.23/2.24. The 2.26 figures (1170 of 2378) came from the first-candidate room matching.

**Who is at fault, for each of the 20 (a debug record of the path that built every texture):** 11 were built by the DIRECT method
(`quad-direct`), so they equal what the TR4 shows and the reference differs from the TR4 (authoring or compile quirk, nothing to fix); 9 were built by the
OLD decode because a direct attempt failed.

**What the 141 fallback wall faces were:** 185 wall faces (before this change) were TombLib QUADS whose TR4 source is a TRIANGLE: a collapsed
quad (one end of the wall has zero height, two corners coincide) or one half of a non-planar wall quad (the other half is a second compiled triangle).
`TryComputeWallQuadOrientation` needs a four-corner source and left rotation 0.
**Fix (`Prj2Exporter.TryComputeCollapsedWallQuadTexCoords`):** match the four geometry corners (p1, p2, p0, p3, p0, p2) in 3D to the triangle's three vertices; a
collapsed quad matches every corner (one vertex twice), a half quad leaves one corner unmatched, which takes the unused corner of the texture box.
TexCoordK belongs to corner p((K + 3) mod 4) as for floor/ceiling quads.
Result: 93 wall faces now take this path (before: rotation 0); the comparison with the reference moves by one face (21 -> 20), because most of these already agreed
with it by luck of rotation 0 or differ for the authoring reason above. 141 wall faces still use the old decode, of which 9 are compared with the reference and differ
(wall triangles whose vertex does not match, and collapsed quads with two unmatched corners).

**Conclusion for orientation:** about 99% of compared wall faces now have the same UV region and corner order as the reference or differ only where the TR4 itself
disagrees with the reference. Nothing worth chasing is left here.
### 2.35 -- Re-measurement of 2.20-2.32 with the correct rooms

Two matching defects were behind many figures: (a) the reference room was the first position match (2.33), and (b) the probes that compare OUR export with the
TR4 itself also located our room by position, so with twin rooms or null slots they could pick or skip the wrong one (the 28 "unmatched rooms" of
alexhub2). Our room `i` is exactly TR4 room `i`. Every probe below was re-run on the current code with `ours[i]` and the best-fitting reference room.
Old figures are the ones written in the section named; "now" mixes the correction with the code changes made since (portal decomposition 2.32, slope fill 2.33,
collapsed wall quads 2.34), which is why a few moved more than the matching alone would explain.

| section | quantity | written then | now |
|---|---|---|---|
| 2.20 / 2.31 | floor quads rendering like the reference | 1042 of 1042 | **1094 of 1094** |
| 2.31 | ceiling quads == TR4 and == reference | 372 of 378 (6 "both differ") | **456 of 456** |
| 2.31 | floor quads == TR4 | 908 of 916 (8 "both differ") | **1094 of 1094** |
| 2.27 | ceiling quads that render differently from the reference | 4 | 7 |
| 2.27 | floor triangle / Floor_Triangle2 rendering like the reference (same vertex set) | 123 / 48 and 48 / 112 | 134 / 50 and 49 / 130 |
| 2.27 | ceiling triangle / Ceiling_Triangle2 | 15 / 1 and 1 / 12 | 23 / 15 and 3 / 32 |
| 2.22 / 2.24 | seams with 3 quads and a straight edge: reference WF/WC equal the quad boundaries | 235 of 248 (94.8%) | **244 of 249 (98.0%)** |
| 2.22 | single-quad seams whose edge matches WF/WC | 334 of 374 (89.3%) | 355 of 392 (90.6%) |
| 2.22 | two-quad seams where the reference has ONE face | 377 of 456 (82.7%) | 406 of 483 (84.1%) |
| 2.23 / 2.24 | compiled wall quads (capped 3 per seam) with a defined face | 2754 of 3057 (90.1%), 28 rooms skipped | **3260 of 3493 (93.3%)**, all rooms; textured 3256 |
| 2.25 | wall seams with a HOLE / face beyond the TR4 stack / no face | 31 / 82 / 27 | 21 / 86 / 18 |
| 2.28 | floor sectors with two compiled triangles, ours == TR4 | 414, 0 differ | **475, 0 differ** |
| 2.28 | ceiling sectors with two compiled triangles, ours == TR4 | 30 after the fix (17 before) | **35, 0 differ** |
| 2.28 | floor sectors with 2 triangles in the TR4 and 1 in ours (triangular portal) | 39 | 39 |
| 2.30 | wall quads with a defined, textured face, 28 levels | 77.6% to 98.2%, about 92% | **86.7% to 97.9%, median 94.9%** |
| 2.30 | two-triangle sectors differing from the TR4, 28 levels | 10 of about 6000 | **0 of 6835** |
| 2.30 | 12-Desert-Railroad wall coverage | 77.6% | 91.3% |
| 2.32 | floor / ceiling portal sectors in both, extra, missing | 1094 / 1007, 0, 0 | 1058 / 1058, **0, 0** |

**What changed in meaning:** the "unexplained" quads of 2.31 (6 ceiling, 8 floor that differed from both the TR4 and the reference) were an artifact of comparing against the
wrong room, not symmetric UV boxes: with the right rooms every single-sector floor and ceiling quad agrees with the TR4 and the reference. The synthesis of hidden wall
heights (2.24) is better supported than first measured (98% on three-quad seams). The Desert-Railroad weakness was mostly a matching and portal artifact (77.6% -> 91.3%).
**What did not change:** the portal conclusions of 2.32 and 2.33, the 39 triangular-portal sectors, the 137 + 39 water-portal floor faces, and the
flipU disagreement between the TR4 and the reference on floor/ceiling triangles (2.27), which still has the same shape (about 0.4 of the compared triangles).
PrjProbe is now the wall-height oracle of 2.22 (it was never committed before).
---

## Key structural lessons (apply to future work on this codebase)

- **TombLib local source is authoritative.** For any format question, find what TombLib's *compiler* writes (PRJ2→TR4) and invert it, rather than guessing from TRosettaStone/trview alone (though both are good secondary checks, and TRosettaStone caught the real bump-mapping mechanism in 2.5).
- **Recurring X/Z swap bug pattern**: treat any new field involving both axes as suspect until verified against TombLib source.
- **A format limit that seems obviously true for the on-disk format may not apply to an in-memory intermediate model** — the 10-bit index bug (2.6) happened because a real classic-PRJ file limit got carried into a data structure that no longer needed to obey it.
- **A plausible theory backed by real documentation can still be wrong for the file in hand** — always verify with a direct data check (pixel dump, raw byte inspection) before committing to a fix based on a spec alone (2.5).
- **Distinguish "no data exists to recover this" from "we have a bug"** before investing engineering effort — several categories here (non-planar ambiguity, vestigial portal/solid-wall textures, most of Floor2/Ceiling2) are the former, and pattern-match on: near-solid/border neighbor sectors, zero real compiled geometry at the seam, or byte-identical-but-differently-resolved raw encodings.
- **Old comments in the code are often already the answer.** The 10-bit truncation fix (2.6) was already flagged, in detail, in a comment that got walked past when writing the exporter.
- **Validate a heuristic's applicability with real data before implementing it.** The 2.8 rank-based fix was only attempted after measuring that 84.4% of the target seams had ≤3 real quads — confirming the approach could work before spending effort building it, rather than tuning blind.
- **When a value looks like garbage, check for a documented flag before writing a heuristic to guess around it.** The 2.10 triangle-UV bug could have been "fixed" with a fragile heuristic (e.g. "drop the 4th vertex if it's far from the other 3"), but the real, robust fix was a single documented bit flag (`NewFlags` bit 15) that says outright whether the 4th vertex is meaningful. Always check the spec for an explicit marker before inferring intent from data shape.
- **A new formula that reuses an already-validated field deserves a direct sign check against the validated code, not just "it produces plausible-looking numbers."** The 2.11 sign bug in `GetCornerFloorY` shipped and contributed to the wall-tier score all session without being caught, because its output still looked reasonable in isolation. It was only caught by comparing one concrete case's computed value against the real compiled geometry's actual Y-range and noticing the result fell outside physically possible bounds. When reusing a field (`FloorCorner`) that another, already-verified code path (`Prj2Exporter.cs`) also consumes, diff the two formulas' signs directly rather than assuming a new use of the same data is correct by association.
- **The validation harness's own room-matching can silently corrupt every number downstream of it.** This level has 28 groups of rooms sharing an X/Z footprint but stacked at different Y (2.18); an X/Z-only match against the reference picks whichever room happens to come first, and the resulting wrong comparisons look like plausible per-room bugs (concentrated in "problem rooms," consistent-looking offsets) rather than an obviously broken diagnostic. Before trusting a finding tied to a specific room, re-derive that room's identity from ALL its distinguishing coordinates (X, Z, *and* Y/YBottom here), not just the two that are usually enough to be unique.
- **An independent, differently-sourced ground truth is worth the integration cost when a metric plateaus.** TombIO (TRLevelControl, from TR-Rando/LostArtefacts) — engine-accurate, tested on real files, and *not* derived from this codebase or TombLib — caught the 2.18 harness bug that a whole session of self-consistent internal reasoning against the single existing reference (`alexhub2_orig.prj2`) could not, because every comparison in this document until 2.18 used the same flawed room-matching. A second, structurally different oracle exposes bugs a single source of truth can't.

---

## Tools & validation

- **Build:** `dotnet build "PRJ2 Extractor.slnx" -c Debug`
- **Validation harness:** `dotnet run --project PrjDiag -c Debug` — loads `alexhub2.tr4`, exports via the real `Prj2Exporter.Export` path, reloads the result with `Prj2Loader` (via precompiled `TombLib.dll`), and compares against `alexhub2_orig.prj2` sector-by-sector and face-by-face. Most diagnostics in this document were produced by temporary blocks appended to `PrjDiag/Program.cs` during investigation — check git history / diffs there for the exact probes if reproducing a measurement.
- **Ground truth files:** `alexhub2.tr4` (input), `alexhub2_orig.prj2` (reference, hand-authored), `C:\Tomb Editor\TombLib.dll` (precompiled, used only for `Prj2Loader.LoadFromPrj2` validation).
