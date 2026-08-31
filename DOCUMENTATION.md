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

---

## Tools & validation

- **Build:** `dotnet build "PRJ2 Extractor.slnx" -c Debug`
- **Validation harness:** `dotnet run --project PrjDiag -c Debug` — loads `alexhub2.tr4`, exports via the real `Prj2Exporter.Export` path, reloads the result with `Prj2Loader` (via precompiled `TombLib.dll`), and compares against `alexhub2_orig.prj2` sector-by-sector and face-by-face. Most diagnostics in this document were produced by temporary blocks appended to `PrjDiag/Program.cs` during investigation — check git history / diffs there for the exact probes if reproducing a measurement.
- **Ground truth files:** `alexhub2.tr4` (input), `alexhub2_orig.prj2` (reference, hand-authored), `C:\Tomb Editor\TombLib.dll` (precompiled, used only for `Prj2Loader.LoadFromPrj2` validation).
