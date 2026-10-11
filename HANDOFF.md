# Printable Pattern — Handoff

_Last updated: 2026-10-11 · Owner: Matthew Fevergeon_

Starting point for anyone (human or AI) picking this up. Read **§0** first.

## 0. Start here (state as of 2026-10-11, end of first session)

**First full draft is written; nothing has been pasted into Onshape yet.**

- `docs/SPEC.md` — what the feature does (from the owner's brief).
- `docs/DESIGN.md` — the overhang math, tilings, cell fitting, API confidence list.
- `tools/pattern_math.py` + `tools/test_pattern_math.py` — the math twin; all 8 tests pass
  (540-case matrix: 3D overhang, rib width, edge margin). `docs/previews/` shows the output.
- `src/PatternMaker.fs` — the feature (circles, hexagons and triangles all wired in).

**Next step (owner):** paste `src/PatternMaker.fs` into a new Feature Studio, check the
Notices panel, then run the §3 test matrix. Report Notices / screenshots; fix rounds follow.

## 1. Goal

One custom feature that cuts circle / hexagon / triangle hole patterns into planar faces or
sketch regions, shaped so every hole prints without supports for the chosen print direction,
max overhang angle and max bridge width.

## 2. How we work

Same rules as every Onshape project — see
[OS_FeatureScript_Reference/ONSHAPE_WORKFLOW.md](https://github.com/the-feve/OS_FeatureScript_Reference/blob/main/ONSHAPE_WORKFLOW.md):
code goes to Onshape by paste, API calls are scarce, math is checked locally first, gotchas are
logged in the reference repo.

## 3. Validation matrix (owner, in Onshape — free, no API)

Make a test Part Studio with a 80 × 60 × 10 mm block and a 80 × 10 × 60 mm wall.

| # | Selection | Print direction | Expect |
| --- | --- | --- | --- |
| 1 | Block top face, circles defaults | Top plane | Round holes through, ~info "any shape prints" |
| 2 | Wall's big face, circles defaults | Top plane | Horizontal holes, plain circles (cap 3.5 mm ≤ 5) |
| 3 | Same, cell 10 mm | Top plane | Flat-top teardrops |
| 4 | Same, max bridge 0 | Top plane | Pointed teardrops |
| 5 | Wall face, hexagons, cell 6 / 12 | Top plane | Flat-top honeycomb / pointy 45° hexes |
| 6 | Wall face, triangles | Top plane | Isogrid, down triangles peaked with 5 mm flat |
| 7 | Wall tilted 30° (rotate the wall), any pattern | Top plane | Pointed shapes, "no bridging" |
| 8 | Split face (Split → face by sketch), circles | Top plane | Only the split area patterned, margin respected |
| 9 | Sketch region on the block's top face | Top plane | Cuts the block |
| 10 | Blind 3 mm from the block's bottom face | Top plane | Warning about pocket roofs as bridges |
| 11 | Print direction = mate connector / edge, flip on | — | Info line names the source; pattern flips its "up" |

For #2–#7 also slice once and look at the slicer's overhang/support preview.

## 4. Decisions log

See `docs/DESIGN.md` §7 (D1–D8). Owner calls I made by default and would like confirmed:

1. **Cell size = inscribed diameter** for all patterns (a triangle of "5 mm" has 8.7 mm sides).
2. **Default cut direction = into the part** (against the region normal), also for sketches.
3. **Whole cells only**, no partial cells clipped at the edge.
4. **Plain circles count as printable** when the flat-ish top chord is ≤ max bridge.
5. Defaults: cell 5 mm, rib 1.6 mm, edge margin 2 mm, max bridge 5 mm, overhang 45°.
6. Pattern is centred on the region's bounding box; no offset control yet.

## 5. Status / roadmap

- [x] P0 Spec and design
- [x] P1 Math twin + tests + previews
- [x] P2 FeatureScript draft: all three patterns, faces + sketch regions, through all / blind,
      both sides, print direction from plane / face / edge / mate connector
- [ ] P3 First paste: clear Notices (see DESIGN.md §6 for the unverified calls)
- [ ] P4 Validation matrix (§3) + one test print of case #2/#5/#6
- [ ] P5 Surface output option (extrude the kept cells as surfaces instead of cutting)
- [ ] P6 Extras: lattice offset, clipped edge cells with roof-fixing, pointed blind-pocket
      floors, cylindrical faces, up-to-face bounds, variable density
- [ ] P7 Publish (icon, description) and log any gotchas in the reference repo

## 6. Open questions

- Are clipped partial cells at the region edge wanted (would need their edge-ceilings roofed)?
- For surface output: surfaces of the hole walls only, or capped hole bodies as surfaces?

## 7. File map

| Path | What |
| --- | --- |
| `src/PatternMaker.fs` | The feature (paste into a Feature Studio) |
| `tools/pattern_math.py` | Python twin of the pure math |
| `tools/test_pattern_math.py` | `python3 -m unittest tools.test_pattern_math` (~100 s) |
| `tools/render_previews.py` | Regenerates `docs/previews/*.svg` |
| `docs/SPEC.md`, `docs/DESIGN.md` | Spec and design |
