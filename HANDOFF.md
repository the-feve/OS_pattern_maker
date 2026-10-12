# Printable Pattern — Handoff

_Last updated: 2026-10-11 · Owner: Matthew Fevergeon_

Starting point for anyone (human or AI) picking this up. Read **§0** first.

## 0. Start here (state as of 2026-10-11, end of first session)

**The feature compiles and cuts patterns in Onshape. Paste 4 (2026-10-11) had equal-sided
hexagons, Tip angle, and blind pockets on curved faces in it; the owner showed Tip angle −30°
on a wall face (so the nested precondition `if` and `hexTipAngle` work) and rejected the look:
stretched, not a honeycomb hexagon.**

**Paste 5 (2026-10-11)** made the default hexagon the **regular** one with a corner up, at every
limit, with Tip angle only turning it; the owner reports it "working great" (math twin: 13
tests pass). Its roofs can exceed the overhang limit (60° on an upright wall); the info message
says so. The stretched, always-printable hexagon is still there as a manual style. The repo's
`src/PatternMaker.fs` is the pasted file; its STATUS comment predates this paste.

**Owner, later the same day:** "tried the different cases, and it's working well." That was
after being asked about curved faces (through all and blind pockets) in particular. No
screenshots and no case-by-case list, so §3 is not ticked off line by line, and nothing has
been printed yet.

**Second feature, Knurl (2026-10-11): third draft written, NOT YET PASTED. This is the open
item.** `src/Knurl.fs` is a separate feature for its own Feature Studio (owner's call; it is
"Feature Studio 1" in the Onshape document). It cuts diamond, helical or straight V grooves
that wrap around cylindrical faces. History, design and open risks: `docs/KNURL.md` §0, §2, §5.

- Draft 1 (true helical sweeps) compiled but never finished regenerating.
- Draft 2 (straight V prisms cut off at their rows) regenerated on the owner's Ø25 × 250 mm rod
  (pitch 5, depth 1: 1856 prisms) but the owner saw steps where prisms met ("the grooves aren't
  continuous"). Cause: a V along a helix twists, a straight prism cannot.
- Draft 3 (in the repo now): on a shaft each prism runs on until it leaves the surface, so the
  groove is a union of overlapping prisms. The math twin simulates this and finds no steps
  (`tools/test_knurl_math.py`, 10 tests pass). Bores still have small steps.

**Next step for the knurl (owner):** paste `src/Knurl.fs` over "Feature Studio 1" and regenerate
the same rod. Report (1) whether the steps are gone, (2) how long it takes, since the prisms now
overlap and the cut may be slower, (3) any Notices. It is not in the user guide yet.

Also new: `docs/Printable_Pattern_User_Guide.pdf`, a one-page guide built by
`python tools/make_user_guide.py` (pictures come from the math twin; rebuild after changes).

- Onshape document: [Printable Pattern](https://cad.onshape.com/documents/00017844b4d3d67d1d1fe858/w/27782954c89d9a7430469def/e/5862b397c8f31bc376fb5c81)
  (did `00017844b4d3d67d1d1fe858`, wid `27782954c89d9a7430469def`, eid `5862b397c8f31bc376fb5c81`)

- `docs/SPEC.md` — what the feature does (from the owner's brief).
- `docs/DESIGN.md` — the overhang math, tilings, cell fitting, API confidence list.
- `tools/pattern_math.py` + `tools/test_pattern_math.py` — the math twin; all 8 tests pass
  (540-case matrix: 3D overhang, rib width, edge margin). `docs/previews/` shows the output.
- `src/PatternMaker.fs` — the feature (circles, hexagons and triangles all wired in).

**Next step (owner):** a sliced test print of one upright-wall case (#2, #5 or #6), and the
less-used §3 cases if not already tried (#9 sketch region, #10 blind from below, #11 mate
connector / edge as print direction). Then pick from the roadmap (§5). Report Notices / screenshots; fix rounds follow.

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
| 5 | Wall face, hexagons, cell 6 / 12 | Top plane | Regular corner-up hexes; info line says "roofs overhang 60 deg, limit 45". Stretched style: taller, 45° roofs. Flat top style: honeycomb, with a warning at cell 12 |
| 6 | Wall face, triangles | Top plane | Isogrid, down triangles as tall as the up ones, 5 mm flat top with 45° corner cuts |
| 7 | Wall tilted 30° (rotate the wall), any pattern | Top plane | Pointed shapes, "no bridging" |
| 8 | Split face (Split → face by sketch), circles | Top plane | Only the split area patterned, margin respected |
| 9 | Sketch region on the block's top face | Top plane | Cuts the block |
| 10 | Blind 3 mm from the block's bottom face | Top plane | Warning about pocket roofs as bridges |
| 11 | Print direction = mate connector / edge, flip on | — | Info line names the source; pattern flips its "up" |

| 12 | Curved face (e.g. a filleted or lofted panel), cut direction = a plane roughly facing it | Top plane | Holes straight through along the direction, whole cells only, margin from the face edges |
| 13 | Outer face of a tube, cut direction square to its axis | Top plane | Holes through both walls, none on the tube's side silhouette |
| 14 | Curved face, no cut direction | — | Clear error asking for one |
| 15 | Curved face, Blind 2 mm | Top plane | Separate pockets, each 2 mm deep measured square to the surface, floor following the face |
| 16 | Wall face, hexagons, Tip angle 15° / −30° | Top plane | Regular hexes turned, not stretched; info line says "tip turned 15 deg". At ±30° they sit flat-topped |

For #2–#7 also slice once and look at the slicer's overhang/support preview.

## 4. Decisions log

See `docs/DESIGN.md` §7 (D1–D8). Owner calls I made by default and would like confirmed:

1. **Cell size = inscribed diameter** for all patterns (a triangle of "5 mm" has 8.7 mm sides).
2. **Default cut direction = into the part** (against the region normal), also for sketches.
3. **Whole cells only**, no partial cells clipped at the edge.
4. **Plain circles count as printable** when the flat-ish top chord is ≤ max bridge.
5. Defaults: cell 5 mm, rib 1.6 mm, edge margin 2 mm, max bridge 5 mm, overhang 45°.
6. Pattern is centred on the region's bounding box; no offset control yet.
7. The default hexagon is regular, corner up, in every case; cell size is its width. It does
   not respect the overhang limit (owner's choice): the info line reports the overhang.
8. On a curved face, Through all cuts everything behind the face along the cut direction (a
   tube gets both walls). Blind depth there is measured along the face normal, not along the
   cut direction.
9. Tip angle only turns the regular hexagon. In the stretched style it also steepens the
   roofs by the same angle, to stay within the overhang limit.

## 5. Status / roadmap

- [x] P0 Spec and design
- [x] P1 Math twin + tests + previews
- [x] P2 FeatureScript draft: all three patterns, faces + sketch regions, through all / blind,
      both sides, print direction from plane / face / edge / mate connector
- [x] P3 First paste: Notices clear, planar faces cut (2026-10-11)
- [x] P3b Cut direction, curved faces by projection, blind pockets on curved faces, regular
      hexagons with tip angle: pasted; owner reports the cases working (2026-10-11)
- [ ] P4 Validation matrix (§3) case by case + one test print of case #2/#5/#6
- [ ] P5 Surface output option (extrude the kept cells as surfaces instead of cutting)
- [ ] P6 Extras: lattice offset, clipped edge cells with roof-fixing, pointed blind-pocket
      floors, wrapping around cylindrical faces, up-to-face bounds, variable density
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
| `src/Knurl.fs` | The Knurl feature (paste into a second Feature Studio) |
| `tools/knurl_math.py`, `tools/test_knurl_math.py` | Knurl math twin and its tests |
| `docs/KNURL.md` | Knurl spec, design and unverified calls |
| `tools/render_previews.py` | Regenerates `docs/previews/*.svg` |
| `tools/make_user_guide.py` | Builds `docs/Printable_Pattern_User_Guide.pdf` (needs Chrome or Edge) |
| `docs/SPEC.md`, `docs/DESIGN.md` | Spec and design |
