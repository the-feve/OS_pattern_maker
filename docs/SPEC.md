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
| Faces or sketch regions | solid faces (planar or curved) / sketch regions, multi-select | — | Each selection patterned independently. Curved faces need a cut direction. |
| Pattern | Circles / Hexagons / Triangles | Circles | |
| Hole shape (circles) | Auto / Circle / Teardrop / Flat-top teardrop | Auto | Auto picks the plainest shape that prints. |
| Hexagon style | Regular point up / Regular flat top / Point up stretched to print | Regular point up | The default is the regular hexagon whatever the limits (owner's choice). |
| Tip angle | angle, ±30° | 0° | Hexagons, point up: turns the upper corner away from "up". The regular hexagon only turns; the stretched style also steepens its roofs by the same angle. |
| Down-pointing triangles | Auto / Full / Peaked / Up only | Auto | |
| Cell size | length | 5 mm | Diameter of the circle each hole is drawn around (inscribed diameter). |
| Rib width | length | 1.6 mm | Minimum wall between neighbouring holes. |
| Edge margin | length | 2 mm | Minimum solid between any hole and the region's edge (incl. inner loops). |
| Print direction | plane / planar face / line edge / mate connector | world +Z | Build-plate normal. |
| Flip print direction | bool | off | |
| Max overhang angle | angle, 5–85° | 45° | From vertical, as slicers measure it. |
| Max bridge width | length | 5 mm | 0 disables bridging. |
| Pattern angle | angle | 0° | Only applies when every orientation prints; otherwise ignored with a note. |
| Cut direction | plane / planar face / line edge / mate connector | empty | The hole axis. Empty: each planar face's own normal. Required for curved faces. |
| End type | Through all / Blind | Through all | On a curved face, Blind makes pockets whose floor follows the face. |
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
   - Hexagons: the default is the regular hexagon with a corner up, at every limit, turned
     rigidly by the tip angle. This is the one default that can exceed the overhang limit (its
     roofs slope 30°, a 60° overhang on an upright wall); the info message then says
     "roofs overhang N deg, limit M" instead of raising a warning. "Point up, stretched to
     print" keeps six equal sides but slopes the roofs `max(30°, θmin + |tip angle|)`, so it
     always prints. Cell size is the hexagon's width. The flat-top honeycomb warns if its top
     edge (`0.577 × cell`) is not a bridge within the limit.
   - Triangles: isogrid (equilateral, sides steepened to `θmin` if needed). Up triangles always
     print. Down triangles have a flat top: kept full if it is a bridge within the limit, else
     their top corners are cut at `θmin`. They stay as tall as the up triangles: the max bridge
     width of the top stays flat when bridging is possible, otherwise the cuts meet in a peak.
   - Forcing a style that cannot print still builds it, with a warning.
3. **Uniform ribs.** Holes are offsets of a tiling, so every wall is at least the rib width.
4. **Whole cells only.** A cell is cut only if it lies entirely inside the region with at least
   the edge margin to every region edge. (Clipped partial cells would create unchecked flat
   ceilings along the region edge.)
4a. **Cut direction and curved faces.** With a cut direction, holes are prisms along it: the
   pattern is laid out on a plane square to the direction and cut through the face. "Whole
   cell" is then judged by the cell's shadow on the face along that direction, and cells stay
   the edge margin away from where the face curves out of view (e.g. the sides of a cylinder
   seen side-on). The printability rule uses the cut direction as the hole axis. The
   direction's sign does not matter: the default cut goes into the part. Blind depth here
   gives pockets whose floor is the face offset by the depth (measured along the face normal).
5. **Layout.** The lattice is centred on the region's bounding box, rows aligned to "up".
6. **Cut.** Kept cells are extruded into tool bodies (through all = enough to clear every
   target, starting 0.05 mm outside the face) and subtracted from the target parts.
7. **Blind pockets.** If a blind pocket's floor ends up facing down beyond the overhang limit,
   the feature warns (bridge too wide, or overhang angle) — it does not reshape the floor yet.
8. **Report.** One info message: print-up source, per-region count, chosen shape and why
   (tilt, required roof angle, bridging allowed). Warnings switch it to a warning message.
9. **Errors.** Nothing selected; no parts to cut; unreadable print or cut direction; curved
   face without a cut direction; cut direction lying in a planar face; blind depth too deep
   for the face's curvature; no whole cell fits; more than 4000 candidate cells.

## 4. Out of scope for v1 (planned later — see HANDOFF.md §5)

- Surface output (extruded surfaces instead of a cut).
- Wrapping a pattern around a curved face (holes normal to the surface). Curved faces are
  cut along one straight direction only.
- Partial cells clipped at the region edge.
- Lattice offset / phase control, variable density.
- Reshaping blind pocket floors (pointed/chamfered floors).
- Up-to-face / up-to-next bounds.

## 5. Acceptance

- Math twin (`tools/test_pattern_math.py`) passes: overhang (2D rule and independent 3D wall
  angle), rib width, edge margin, over patterns × tilts × limits × concave regions with holes.
- In Onshape (owner): regenerates without Notices on the test matrix in HANDOFF.md §3; a
  sliced test print of the side-wall case shows no support needed in the slicer's overhang view.
