# Knurl — spec and design

_Owner: Matthew Fevergeon · Drafted 2026-10-11 from the owner's request_

A second custom feature, separate from Printable Pattern: `src/Knurl.fs`, pasted into its own
Feature Studio ("Feature Studio 1" in the Onshape document). It cuts a knurl that wraps around a
cylindrical face, for grip texture.

Owner's request: "create a knurling pattern … this should wrap around a surface — typically this
will be a cylindrical surface for adding texture", as "a new feature script, separate from the
current tool".

## 0. Status (2026-10-11)

| Draft | Design | Result in Onshape |
| --- | --- | --- |
| 1 | True helical sweeps (`opHelix` + `opSweep`), trimmed to the face with two extra booleans | Compiled; regeneration churned until Onshape gave up |
| 2 | Straight V prisms cut off at their rows, one subtraction | Regenerated (1856 prisms on a Ø25 × 250 mm rod), but the grooves had visible steps where prisms met |
| 3 | Straight V prisms that overlap: on a shaft each runs until it leaves the surface | **Not yet pasted** |

Draft 2 confirmed in Onshape: the oblique `opExtrude`, `opPattern` with ~900 transforms per
call, `rotationAround(...) * transform(...)`, `evSurfaceDefinition(...).coordSystem`, and that one
subtraction with 1856 small prisms finishes.

## 1. Inputs

| Field | Default | Notes |
| --- | --- | --- |
| Cylindrical faces | — | Solid faces, multi-select. Shafts and bores. The knurl covers the face's length, all the way around; split a face first to knurl only a band. |
| Style | Diamond | Diamond (two crossed helices), Straight (along the axis), Helical (one direction). |
| Groove pitch | 2 mm | Spacing around the cylinder; adjusted so a whole number of grooves fits. |
| Groove depth | 0.5 mm | Measured square to the surface. |
| Helix angle | 30° | Between the grooves and the axis, 5–60°. Adjusted slightly so a whole number of rows fits the length. Not shown for Straight. |
| Smoother grooves (slower) | off | More, shorter rows (see §2). |
| Opposite hand | off | Helical only. |
| Groove angle | 90° | Included angle of the V, measured square to the groove. |
| End margin | 0 | Plain band left at each end of the face. 0 runs the grooves off both ends. |
| Print direction, Max overhang | +Z, 45° | Only for the overhang report (collapsed group). |
| Parts to cut | empty | Empty = the parts the faces belong to. |

## 2. How it is built (`knurlOneFace`)

A helical groove is made of straight V prisms, which have flat faces only. Flat faces are what
make the cut fast enough (draft 1 used swept surfaces and never finished).

1. `evSurfaceDefinition` gives the cylinder's axis and radius; `evBox3d` in the cylinder's own
   frame gives the face's extent along the axis; `evFaceTangentPlane` tells shaft from bore.
2. `knurlLayout` (mirrored in `tools/knurl_math.py`) divides the length into **rows**. A groove
   moves exactly one pitch around every `facets` rows, so every row is the row below it turned
   by a fixed `step`. The helix angle is nudged so a whole number of rows fits.
3. One V is sketched on a plane square to the axis at the middle of the first row and extruded
   both ways along a straight line lying along the helix there (`opExtrude` with an oblique
   direction). A diamond does this for each helix direction.
4. `opPattern` copies the seed `count` times around and once per row: a rotation about the axis
   combined with a translation along it. The seed itself is deleted; it is only a template.
5. One `opBoolean` subtracts every prism from the targets.

**Why the prisms overlap (draft 3).** A V cut along a helix twists as it goes: at each point it
opens square to the surface there. A straight prism cannot twist. In draft 2 each prism was cut
off at its row's boundaries, and the prism above started there with its V turned by the row's
`step` (5.6° on the owner's rod), so the two cross-sections did not match and left a 0.3 mm step
at the surface (reproduced in `tools/test_knurl_math.py`). The fix on a **shaft**: let every
prism run on until its tip has left the surface (`reach`). A straight line touching a cylinder
rises away from it, so the prism fades out by itself and its end face is never inside the part.
The groove is then the union of overlapping prisms, which has no steps, only faint creases. The
cost: the groove comes out up to about 7% wider at the surface than a true helical V, and each
prism is several rows long.

**Ends of the knurl.** A prism near an end of the knurl would run past it, so it is cut short
there (0.05–0.08 mm past the end). Rows that need the same pair of lengths share one seed, so
there are a handful of seeds per helix direction: one for all the middle rows and one for each
of the few rows near each end.

**Bores.** In a bore a straight prism digs deeper the further it runs, so the overlap trick is
not available: bore prisms still stop at their rows and still meet with small steps. The
message reports the step size; "Smoother grooves" reduces it.

**Other details.**

- A prism's direction is the helix direction at mid-depth
  (`tan(segHelix) = tan(helix) · r_mid / R`). Its tip is placed `sag/2` too deep at the middle of
  its row and is `sag/2` too shallow at the row's ends (`chordSag`), so the groove's depth
  ripples by `sag`. Rows are made short enough that `sag` is at most 15% of the depth (5% with
  "Smoother grooves").
- The two helix directions use different end overlaps so their end faces never share a plane.
- Limit: 4000 prisms per face.

Not done: trimming to a face that is only part of the way around a cylinder. The grooves always
go all the way around.

## 3. Printing

With the cylinder standing along the print direction, a groove wall overhangs
`asin(cos(grooveAngle/2) · sin(segHelix))` from vertical: about 20° at the defaults, and never
more than 45° for a 90° groove. Straight grooves have vertical walls. The info message reports
the figure and the feature warns if it is over the limit. If the axis is more than 5° off the
print direction the message says the overhang was not checked (the underside of a lying cylinder
is an overhang problem of its own).

If the grooves are wider than the gaps between them (`land < 0`) the peaks get cut below the
surface; the feature still builds and warns.

## 4. Out of scope for now

- Surfaces other than cylinders (cones, general surfaces of revolution).
- A face that covers only part of the circumference.
- Step-free grooves in bores (would need lofted, twisted segments).
- Raised knurls (adding material instead of cutting).
- Rounded or flat-bottomed grooves.

## 5. Open risks for draft 3

| Risk | What to do if it happens |
| --- | --- |
| The longer, overlapping prisms make the subtraction too slow (same prism count as draft 2, but each overlaps about four neighbours) | Raise `FACET_TOL` so rows are longer; or knurl a shorter band |
| `groups[g].rows = append(...)` (assigning into a map inside an array) or `abs()` on a length is rejected | Rebuild the group map and reassign the array element; compare lengths with `<`/`>` |
| Creases along the groove walls are more visible than expected | Turn on "Smoother grooves"; or tighten `FACET_TOL` |
| The slightly wider groove (up to ~7%) matters | Narrow the sketched V to compensate (the simulation in the tests gives the amount) |
