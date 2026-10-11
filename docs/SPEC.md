# Printable Pattern — Spec

_Owner: Matthew Fevergeon · Drafted 2026-10-11 from the owner's brief_

An Onshape custom feature (FeatureScript) that quickly cuts lightweighting / decorative hole
patterns into a part, where every hole is guaranteed to print on an FDM printer **without
supports** for a chosen print orientation.

## 1. Owner's brief (verbatim intent)

- Patterns: **triangles, holes (circles), hexagons**. Start with circles: they need little
  overhang handling.
- Select the area to pattern: a **sketch region**, or a **face of a solid part** (the owner
  splits faces to define the area).
- Cut with extrude-style bounds: **through all** or a set depth. Solids only to start; later,
  optionally create extruded **surfaces** instead.
- Orient the pattern from a **print direction**: e.g. a plane whose normal is the printer's Z.
- **Max overhang angle**, default 45° (as in slicers).
- **Max bridge width**: where a ceiling is flatter than the overhang limit, keep the bridge it
  needs within this width.
- The pattern must end up printable without supports.

## 2. Inputs (feature dialog)

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| Faces or sketch regions | planar solid faces / sketch regions, multi-select | — | Each selection patterned independently. |
| Pattern | Circles / Hexagons / Triangles | Circles | |
| Hole shape (circles) | Auto / Circle / Teardrop / Flat-top teardrop | Auto | Auto picks the plainest shape that prints. |
| Hexagon style | Auto / Flat top / Pointy top | Auto | |
| Down-pointing triangles | Auto / Full / Peaked / Up only | Auto | |
| Cell size | length | 5 mm | Diameter of the circle each hole is drawn around (inscribed diameter). |
| Rib width | length | 1.6 mm | Minimum wall between neighbouring holes. |
| Edge margin | length | 2 mm | Minimum solid between any hole and the region's edge (incl. inner loops). |
| Print direction | plane / planar face / line edge / mate connector | world +Z | Build-plate normal. |
| Flip print direction | bool | off | |
| Max overhang angle | angle, 5–85° | 45° | From vertical, as slicers measure it. |
| Max bridge width | length | 5 mm | 0 disables bridging. |
| Pattern angle | angle | 0° | Only applies when every orientation prints; otherwise ignored with a note. |
| End type | Through all / Blind | Through all | |
| Depth | length | 5 mm | Blind only. |
| Opposite direction | bool | off | Default cuts against the region normal (into the part). |
| Both sides | bool | off | Same depth/through-all both ways. |
| Parts to cut | solid bodies | empty | Empty: a face's own part; sketch regions cut every modifiable part. |
| Keep pattern sketch | bool | off | Debug: leaves every candidate cell as a sketch. |

## 3. Behaviour

1. **Printability rule.** For each region, let β be the angle between the print axis and the
   hole axis (region normal).
   - If β ≤ max overhang, every wall of every hole is within the limit: any shape prints, the
     pattern angle applies.
   - Otherwise each hole's "ceiling" edges (the part of its outline with material above, in
     the region plane's projected-up direction) must slope at least
     `θmin = 90° − asin(sin(maxOverhang) / sin β)` from horizontal.
   - Flat ceilings are only allowed as **bridges**, and only when the hole axis is horizontal
     (β within 1° of 90°), up to the max bridge width.
2. **Shape selection (Auto).**
   - Circles: plain circle if the too-flat part of its top (a chord `2r·sin θmin`) is a
     bridge within the limit; else a teardrop with a flat top of the bridge width; else (no
     bridging possible) a pointed teardrop with `θmin` cap slope.
   - Hexagons: flat-top regular honeycomb if its top edge (`0.577 × cell`) is a bridge within
     the limit and its 60° sides pass; else pointy-top hexagons, stretched vertically so the
     roof slopes `max(30°, θmin)`.
   - Triangles: isogrid (equilateral, sides steepened to `θmin` if needed). Up triangles always
     print. Down triangles have a flat top: kept full if it is a bridge within the limit, else
     given a peaked roof at `θmin` (flat-topped at the bridge width when bridging is possible).
   - Forcing a style that cannot print still builds it, with a warning.
3. **Uniform ribs.** Holes are offsets of a tiling, so every wall is at least the rib width.
4. **Whole cells only.** A cell is cut only if it lies entirely inside the region with at least
   the edge margin to every region edge. (Clipped partial cells would create unchecked flat
   ceilings along the region edge.)
5. **Layout.** The lattice is centred on the region's bounding box, rows aligned to "up".
6. **Cut.** Kept cells are extruded into tool bodies (through all = enough to clear every
   target, starting 0.05 mm outside the face) and subtracted from the target parts.
7. **Blind pockets.** If a blind pocket's floor ends up facing down beyond the overhang limit,
   the feature warns (bridge too wide, or overhang angle) — it does not reshape the floor yet.
8. **Report.** One info message: print-up source, per-region count, chosen shape and why
   (tilt, required roof angle, bridging allowed). Warnings switch it to a warning message.
9. **Errors.** Nothing selected; no parts to cut; unreadable print direction; no whole cell
   fits; more than 4000 candidate cells.

## 4. Out of scope for v1 (planned later — see HANDOFF.md §5)

- Surface output (extruded surfaces instead of a cut).
- Non-planar faces (cylinders etc.).
- Partial cells clipped at the region edge.
- Lattice offset / phase control, variable density.
- Reshaping blind pocket floors (pointed/chamfered floors).
- Up-to-face / up-to-next bounds.

## 5. Acceptance

- Math twin (`tools/test_pattern_math.py`) passes: overhang (2D rule and independent 3D wall
  angle), rib width, edge margin, over patterns × tilts × limits × concave regions with holes.
- In Onshape (owner): regenerates without Notices on the test matrix in HANDOFF.md §3; a
  sliced test print of the side-wall case shows no support needed in the slicer's overhang view.
