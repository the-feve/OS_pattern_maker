# Printable Pattern — Design

How `src/PatternMaker.fs` works and why. The pure math lives in two places that must stay in
step: `tools/pattern_math.py` (tested locally) and the bottom half of the `.fs` file (same
function names, camelCase).

## 1. Pipeline (per selected region)

```
evPlane(region) ──► patternFrame ──► buildCellSet ──► latticeCandidates
                     (u, v=up,        (lattice t1,t2,    (centres covering the
                      limits)          hole shapes)       region's bounding box)
        ──► one sketch with every candidate hole
        ──► keep a sketch region if: centroid on the region AND distance to region edges ≥ margin
        ──► opExtrude kept regions (tool bodies) ──► one opBoolean SUBTRACTION at the end
```

Only whole cells are kept. Everything except the last three steps is pure math.

## 2. The overhang reduction (the core idea)

Holes are prisms along the region normal **N**. Let **Z** be the print up axis.

- Split **Z** into its part along **N** and its part in the region plane:
  `zt = Z − (Z·N)N`, `s = |zt| = sin β` (β = angle between print axis and hole axis).
- Local frame: `v = zt/|zt|` ("up" in the plane), `u = cross(v, N)`. The sketch plane is
  `plane(origin, N, u)`, whose y axis `cross(N, u)` is exactly `v`.
- A hole wall is a ruled surface along **N**. Its outward normal (out of the hole, into the
  material) **m** lies in the plane, so `m·Z = m·zt = s·(m₂·v)`, where `m₂` is the 2D edge
  normal. The 3D overhang angle from vertical is `asin(m·Z)` when positive.
- So the 3D condition `asin(m·Z) ≤ maxOverhang` becomes, in 2D:

  `m₂·v ≤ sin(maxOverhang) / s`

  i.e. a ceiling edge must slope at least `θmin = 90° − asin(sin(maxOverhang)/s)` from
  horizontal. If `s ≤ sin(maxOverhang)` there is no constraint at all.
- A flat 2D ceiling is a horizontal 3D surface only when `s ≈ 1` (hole axis horizontal); only
  then is it a real bridge. For a tilted hole axis a flat ceiling is a sloped overhang, not a
  bridge, so it is never allowed.

`test_pattern_math.py` checks this independently: it lifts every wall normal back to 3D and
measures the true overhang against **Z**.

## 3. Shapes and tilings

Every hole is either a circle / teardrop of radius `r = cell/2`, or a polygon **circumscribed**
about that circle, built from a list of edge-normal angles (`circumscribed(normals, ρ)`:
vertex k = intersection of edges k and k+1, all at distance ρ).

The **tile** (rib centre-lines) is the same polygon circumscribed about `R = r + rib/2`. For a
circumscribed polygon, offsetting by `rib/2` is exactly "same normals, ρ + rib/2", so if the
tiles tessellate, every rib is at least `rib` wide.

| Pattern | Tile | Lattice | Hole(s) per lattice point |
| --- | --- | --- | --- |
| Hexagon, flat top | regular hex, normals 30°+60°k | `t1 = v0+v1, t2 = v1+v2` (any centrally symmetric hexagon tiles by these) | hex at r |
| Hexagon, point up | `pointyHex`: six equal sides, vertical sides at ±r, roofs sloped θ; tile = the same hexagon with every edge moved out rib/2; both turned by the tip angle. Default style: θ = 30° (regular) always. Stretched style: θ = max(30°, θmin + tip) | same | hex of width 2r |
| Circles / teardrops | pointy hex with roof θ = max(30°, cap slope) | `t1 = v5+v0, t2 = v0+v1` | circle or teardrop at r (fits inside the hex at r) |
| Triangles | up triangle, normals 90∓θ, 270 (θ = 60° or steeper) + its point reflection | `t1 = B−A, t2 = C−A` | up triangle at r; down triangle (point reflection) at offset `B+C`, optionally peaked |

**Teardrop:** circle + two lines tangent at slope θ meeting at `r / cos θ` above the centre;
"flat" variant cuts the cap where it is `maxBridge` wide (never lower than the tangent points).

**Roof (`roofClip`):** cut the top corners off a convex polygon with two half-planes sloped θ.
Used on down triangles, which keep their full height (same as the up triangles). When bridging
is possible, `maxBridge` of the top edge stays flat and the roof lines fall away from its two
ends; otherwise they meet in a peak at the top-centre. For a 5 mm cell at θ = 45° the hole
keeps 93% of its area with a 5 mm flat and 63% with a peak.

All holes are convex, so there are no ceiling "valleys" (a valley starts material mid-air).

## 4. Fitting cells to the region

`latticeCandidates` covers the region's bounding box (computed by `evBox3d` in the pattern
frame) with lattice points, centred on the box centre. All candidates go into one sketch; each
resulting sketch region is kept if

1. `evDistance(cell, region edges) ≥ edge margin` (a cell crossing an edge has distance 0;
   inner loops are edges too), and
2. its `evApproximateCentroid` lies on the region face (`evDistance` ≈ 0).

Exact for any shape, including teardrops and concave regions. The Python twin does the same
with polygon distances.

### 4a. Cut direction and curved faces (`regionSetup`, `cutCellsProjected`)

The path above (`cutCellsOnFace`) is used when a planar region is cut along its own normal. A
curved face, or a cut direction that is not the face normal, goes through `cutCellsProjected`:

- Hole axis `N` = the cut direction, turned to point out of the face (face normal at its
  parameter centre), so "into the part" stays the default. All the overhang math is unchanged:
  holes are prisms along `N`, whatever the face does.
- The sketch plane is square to `N` and touches the face's extreme on the side the cut starts
  from (`evBox3d` z-range in the pattern frame).
- A candidate is drawn only if rays along `N` hit the face from its centre and from every
  `outlineSamples` point (outline corners pushed `edgeMargin` out; mirrored and tested in
  Python). This is what keeps cells off a silhouette, where there is no edge to measure to.
- All drawn cells are extruded, then each tool body must be `edgeMargin` from the face's edges
  (`evDistance(body, edges)`: a prism along `N` is as far from an edge as its shadow is).
  Failing bodies are deleted.
- Blind depth (`pocketTools`): the face is thickened along its normals into a skin as thick as
  the depth, twice. Skin A minus the prisms is the skin with holes; skin B minus that leaves
  only the pockets, which become the tools. The owner suggested extruding up to an offset
  surface; this gives the same floor but cannot fail when a cell near the edge of the face
  hangs past the (smaller) offset surface. `opBoolean` `SUBTRACT_COMPLEMENT` might do it with
  one skin; untested, so two plain subtractions are used.

Known gaps: ray samples are corners only, so a silhouette with a sharp inward notch narrower
than a cell could slip through; and an inner loop lying wholly inside one cell, further than
the margin from its walls, is only caught if `evDistance` treats a solid body as filled.

## 5. Cut

- Direction: `−N` by default (into the part for a face; for a sketch drawn on a part's face).
  `N` is the region normal, or the selected cut direction (§4a).
- Through all: depth = diagonal of the box around targets + regions, + 1 mm. The tool starts
  `0.05 mm` outside the face so its cap is never coplanar with the face.
- Blind: `depth`. Both sides: same each way.
- Targets: explicit selection, else the face's owner part, else (sketch regions) every
  modifiable solid. One `opBoolean` SUBTRACTION for all regions; candidate sketches deleted.
- Blind pockets whose floor faces downward beyond the limit produce a warning (§3.7 of the spec).

## 6. FeatureScript API used — confidence

Confirmed in Rocket_Builder (same FS 3083): `newSketchOnPlane`, `skLineSegment`, `skCircle`
(map form), `skArc` (start/mid/end), `skSolve`, `qSketchRegion`, `opExtrude` BLIND both ends,
`opBoolean` SUBTRACTION with targets, `opDeleteBodies`, `qSketchFilter`, `qAdjacent`,
`evPlane`, `reportFeatureInfo/Warning`, `try silent {}`.

Paste 3 (2026-10-11) compiled with the cut-direction code in it and cut point-up hexagons on
the wall face; the owner then got the (since removed) "blind not supported" error on a curved
face, so `regionSetup` reaches `cutCellsProjected` there. Whether Through all on a curved face
cuts correctly has not been reported.

Confirmed here by paste 2 (2026-10-11; wall face, triangles and hexagons, Top plane as print
direction, through all): the file compiles, so every call below resolves, and that run used
`evBox3d` with `cSys`, `evDistance` with a point side, `evApproximateCentroid`,
`roundToPrecision`, `concatenateArrays`, `sort(array, fn)`, `floor`/`ceil`, the
`BodyType.MATE_CONNECTOR` filter and the negative `isAngle` bound.

**UNVERIFIED** (written but not yet run in Onshape):

| Call | Fallback if wrong |
| --- | --- |
| `evRaycast(context, { "entities", "ray" : line(...), "closest" : true })` returning `[]` on a miss (signature checked against the std source; `includeIntersectionsBehind` cannot be combined with `closest`) | `evDistance` from an `extend`ed line to the face |
| `evFaceTangentPlane(..., "parameter" : vector(0.5, 0.5)).normal` pointing out of the solid | wrapped in `try silent`; only sets which way "into the part" is |
| `evDistance` between a solid tool body and edges (0 when an edge passes through it) | distance to `qOwnedByBody(body, EntityType.FACE)` |
| `opExtrude` of all `qSketchRegion` cells giving one body per cell | extrude per cell |
| Region filter `EntityType.FACE && BodyType.SOLID` accepting curved faces | add `GeometryType` alternatives |
| `qAllModifiableSolidBodies()` (sketch-region path, not yet exercised) | `qBodyType(qEverything(EntityType.BODY), BodyType.SOLID)` |
| `evMateConnector(...).zAxis`, `evAxis(...).direction` (not yet exercised) | each wrapped in `try silent`; remove the branch |
| `opThicken(context, id, { "entities" : face, "thickness1" : outward, "thickness2" : inward })` on a face of a solid (fields checked against the std source; `thickness1` runs along the face normal) | `opExtractSurface` with `offset`, then thicken the sheet |
| `qCreatedBy(skinB)` + `qCreatedBy(pockets boolean)` finding every pocket after skin B is cut into many pieces | `qSplitBy`, or collect bodies by position |
| Precondition `if (definition.hexStyle != HexStyle.FLAT_TOP)` nested in the pattern `if` | show Tip angle for every hexagon style |
| Blind depth on a planar face, both sides, opposite direction, `qNothing()` (not yet exercised) | — |

## 7. Decisions (and why)

| # | Decision | Why |
| --- | --- | --- |
| D1 | Sketch + `opExtrude` + `opBoolean`, not calling the std `extrude` feature | Feature-calls-feature needs a complete definition map (fragile); op-level calls already proven in Rocket_Builder. Up-to-face bounds can come later via std `extrude`. |
| D2 | Cell size = inscribed diameter for every pattern | One rule for all shapes and the circumscribed-polygon math; similar-looking sizes across patterns. |
| D3 | Whole cells only | Clipped cells get the region edge as a ceiling — the main way a "printable" pattern would fail. |
| D4 | Plain circles OK when the too-flat cap chord ≤ max bridge | Matches the owner's "circles tend to work fine"; it's a heuristic (the cap is curved, not flat), documented. |
| D5 | Bridges only with a horizontal hole axis (±1°) | Elsewhere a flat ceiling is a sloped overhang, which slicers won't bridge. |
| D6 | Down triangles keep full height: top corners cut at θmin, with a flat of max bridge width where bridging is possible, else a peak | Owner request 2026-10-11 (the first draft cut the peak down to the bridge width, leaving short triangles). Keeps the most open area and matches the up triangles' height. |
| D7 | Pattern angle only when orientation is free | Any rotation would break the overhang rule otherwise. |
| D8 | Python twin of the math | FeatureScript has no local interpreter; this keeps the API budget for real-geometry checks. |
| D9 | Hexagons default to the **regular** hexagon with a corner up, at every limit; the tip angle only turns it | Owner 2026-10-11, after seeing the stretched version: "a symmetric hex, all angles and lengths the same (like honeycomb)", and no stretching when turned. This is the only default that can break the overhang limit (60° on an upright wall), so the info message states the overhang and the limit. No warning: it is the owner's chosen default and would be yellow on every use. |
| D12 | "Point up, stretched to print" stays as a manual style | Six equal sides, roofs at the limit, and the tip angle steepens them further. Keeps a hexagon that is guaranteed to print; the test matrix runs hexagons in this style. Warns when it would need more than 80°. |
| D10 | Curved faces are cut along one selected direction (projection), not wrapped | Owner request 2026-10-11. Keeps holes prismatic, so the same overhang math holds. Wrapping is a later item. |
| D11 | Blind depth on the projected path = pockets cut from a thickened skin of the face | Owner request 2026-10-11 (floor = face offset by the depth). Thicken + two subtractions instead of up-to-surface: see §4a. |
