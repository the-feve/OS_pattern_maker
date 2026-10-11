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
| Hexagon, pointy | normals 0, 90∓θ, 180, 270∓θ (θ = roof slope, 30° = regular) | same | hex at r |
| Circles / teardrops | pointy hex with roof θ = max(30°, cap slope) | `t1 = v5+v0, t2 = v0+v1` | circle or teardrop at r (fits inside the hex at r) |
| Triangles | up triangle, normals 90∓θ, 270 (θ = 60° or steeper) + its point reflection | `t1 = B−A, t2 = C−A` | up triangle at r; down triangle (point reflection) at offset `B+C`, optionally peaked |

**Teardrop:** circle + two lines tangent at slope θ meeting at `r / cos θ` above the centre;
"flat" variant cuts the cap where it is `maxBridge` wide (never lower than the tangent points).

**Peaked roof (`roofClip`):** clip a convex polygon by two half-planes sloped θ meeting at
its top-centre, then optionally flat at `maxBridge` width. Used on down triangles: keeps ~60%
of the hole versus ~15% for simply truncating it to the bridge width.

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

## 5. Cut

- Direction: `−N` by default (into the part for a face; for a sketch drawn on a part's face).
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

**UNVERIFIED** (first suspects if the Notices panel complains):

| Call | Fallback if wrong |
| --- | --- |
| `evBox3d(..., "cSys" : coordSystem(...))` returning the box in that frame | transform `evBox3d` corners of a world box, or sample `evVertexPoint`s |
| `evDistance` with a `Vector` point as one side | build an `opPoint` per centroid (slower) |
| `evApproximateCentroid` | `evDistance` side0 point from the lattice centre (holes are centred) |
| `qAllModifiableSolidBodies()` | `qBodyType(qEverything(EntityType.BODY), BodyType.SOLID)` |
| `evMateConnector(...).zAxis`, `evAxis(...).direction` | each wrapped in `try silent`; remove the branch |
| `roundToPrecision`, `concatenateArrays`, `sort(array, fn)`, `qNothing()`, `floor`/`ceil` | hand-rolled helpers |
| Filter `BodyType.MATE_CONNECTOR` inside an `||` filter expression | drop mate connectors from the filter |
| `isAngle` with a negative lower bound (`[-180, 0, 180]`) | `[0, 0, 360]` |

## 7. Decisions (and why)

| # | Decision | Why |
| --- | --- | --- |
| D1 | Sketch + `opExtrude` + `opBoolean`, not calling the std `extrude` feature | Feature-calls-feature needs a complete definition map (fragile); op-level calls already proven in Rocket_Builder. Up-to-face bounds can come later via std `extrude`. |
| D2 | Cell size = inscribed diameter for every pattern | One rule for all shapes and the circumscribed-polygon math; similar-looking sizes across patterns. |
| D3 | Whole cells only | Clipped cells get the region edge as a ceiling — the main way a "printable" pattern would fail. |
| D4 | Plain circles OK when the too-flat cap chord ≤ max bridge | Matches the owner's "circles tend to work fine"; it's a heuristic (the cap is curved, not flat), documented. |
| D5 | Bridges only with a horizontal hole axis (±1°) | Elsewhere a flat ceiling is a sloped overhang, which slicers won't bridge. |
| D6 | Down triangles get a peaked roof, not truncation | Keeps far more open area; no bridge needed at all. |
| D7 | Pattern angle only when orientation is free | Any rotation would break the overhang rule otherwise. |
| D8 | Python twin of the math | FeatureScript has no local interpreter; this keeps the API budget for real-geometry checks. |
