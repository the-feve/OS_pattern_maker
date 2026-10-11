"""Builds docs/Printable_Pattern_User_Guide.pdf: a one-page guide whose pattern
pictures are drawn from pattern_math, so they always match the feature.

Run:  python tools/make_user_guide.py   (from the repo root; needs Chrome or Edge)
"""

import math
import os
import re
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(__file__))
import pattern_math as pm  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..")
OUT = os.path.abspath(os.path.join(ROOT, "docs", "Printable_Pattern_User_Guide.pdf"))
BROWSERS = [
    os.environ.get("CHROME", ""),
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "/usr/bin/google-chrome",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
]

# Picture settings (stated in the guide's caption).
CELL, RIB, MAX_OH, MAX_BRIDGE, MARGIN = 8.0, 2.0, 45.0, 5.0, 2.0
REGION = [[(0, 0), (62, 0), (62, 24), (0, 24)]]
N = (0.0, 1.0, 0.0)
ROWS = [
    # tilt between print axis and hole axis, row title, row note
    (0, "Plate printed flat", "Holes run along the print axis, so any shape prints. Regular shapes; Pattern angle applies."),
    (90, "Wall printed upright", "Holes run across the print axis. Roofs slope at the overhang angle; flat tops stay within Max bridge width."),
    (65, "Wall tilted 65\u00b0", "A flat top here is a sloped overhang, not a bridge, so every roof comes to a point."),
]
CAPTIONS = {
    (pm.CIRCLES, 0): "Circles", (pm.TRIANGLES, 0): "Isogrid",
    (pm.CIRCLES, 90): "Flat-topped teardrops", (pm.TRIANGLES, 90): "Flat-topped down triangles",
    (pm.CIRCLES, 65): "Pointed teardrops", (pm.TRIANGLES, 65): "Peaked down triangles",
}


def swatch(kind, beta):
    """(svg, caption) for one pattern at one tilt, drawn with the Auto style."""
    Z = (0.0, math.cos(math.radians(beta)), math.sin(math.radians(beta)))
    frame = pm.pattern_frame(N, Z, MAX_OH)
    cs = pm.build_cell_set(kind, CELL, RIB, frame, MAX_BRIDGE)
    placed = pm.place_holes(cs, REGION, MARGIN)
    assert placed and not cs["warnings"], (kind, beta)
    if kind == pm.HEXAGONS:
        # Regular whatever the limits; the caption carries the overhang the label reports.
        over = re.search(r"roofs overhang (\d+) deg", cs["label"])
        caption = "Regular: roofs overhang %s\u00b0" % over.group(1) if over else "Regular, corner up"
    else:
        caption = CAPTIONS[(kind, beta)]
        for _, h in placed:
            assert not pm.check_printable(pm.outline(h, 180), frame, MAX_BRIDGE), (kind, beta)
    w, h = REGION[0][2]
    holes = " ".join(
        "M " + " L ".join("%.2f %.2f" % (x, h - y) for x, y in pm.placed_outline(c, s, 72)) + " Z" for c, s in placed)
    arrow = ""
    if frame["constrained"]:
        arrow = ('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="4.5" stroke="#c0392b" stroke-width="0.9" marker-end="url(#up)"/>'
                 % (w + 4, h - 2, w + 4))
    return ('<svg viewBox="-1 -1 %d %d" xmlns="http://www.w3.org/2000/svg">'
            '<defs><marker id="up" markerWidth="5" markerHeight="5" refX="2.5" refY="2.5" orient="auto">'
            '<path d="M0,0 L5,2.5 L0,5 z" fill="#c0392b"/></marker></defs>'
            '<rect width="%d" height="%d" rx="0.8" fill="#cbd6e2" stroke="#3d5a73" stroke-width="0.35"/>'
            '<path d="%s" fill="#fff" stroke="#22384d" stroke-width="0.25"/>%s</svg>'
            % (w + 9, h + 2, w, h, holes, arrow)), caption


CUT_DIAGRAM = '''
<svg viewBox="0 0 270 112" xmlns="http://www.w3.org/2000/svg" font-family="Segoe UI, Arial, sans-serif">
  <defs>
    <clipPath id="band"><path d="M40,100 Q130,10 220,100 L207,105 Q130,32 53,105 Z"/></clipPath>
    <marker id="cut" markerWidth="6" markerHeight="6" refX="3" refY="3" orient="auto">
      <path d="M0,0 L6,3 L0,6 z" fill="#1f6f8b"/></marker>
  </defs>
  <line x1="78" y1="24" x2="182" y2="24" stroke="#555" stroke-width="0.8" stroke-dasharray="3 2"/>
  <g stroke="#9aa7b4" stroke-width="0.5" stroke-dasharray="1.5 1.5">
    <line x1="86" y1="24" x2="86" y2="66"/><line x1="174" y1="24" x2="174" y2="66"/>
  </g>
  <path d="M40,100 Q130,10 220,100 L207,105 Q130,32 53,105 Z" fill="#cbd6e2" stroke="#3d5a73" stroke-width="0.9"/>
  <g clip-path="url(#band)" fill="#fff" stroke="#22384d" stroke-width="0.5">
    <rect x="86" y="20" width="9" height="90"/><rect x="102" y="20" width="9" height="90"/>
    <rect x="118" y="20" width="9" height="90"/><rect x="134" y="20" width="9" height="90"/>
    <rect x="150" y="20" width="9" height="90"/><rect x="165" y="20" width="9" height="90"/>
  </g>
  <line x1="12" y1="22" x2="12" y2="62" stroke="#1f6f8b" stroke-width="1.6" marker-end="url(#cut)"/>
  <text x="18" y="46" font-size="7.5" fill="#1f6f8b" font-weight="600">Cut direction</text>
  <text x="130" y="17" font-size="7" fill="#444" text-anchor="middle">pattern plane, square to the cut direction</text>
  <text x="130" y="92" font-size="7" fill="#444" text-anchor="middle">straight holes through the face</text>
  <text x="212" y="60" font-size="7" fill="#444">no cells on the</text>
  <text x="212" y="69" font-size="7" fill="#444">steep sides</text>
</svg>'''

SETTINGS = [
    ("Faces or sketch regions", "What to pattern; each selection is patterned on its own. Split a face first to pattern only part of it."),
    ("Pattern and style", "Circles, hexagons or triangles. For circles and triangles, <b>Auto</b> picks a shape that prints. Hexagons are regular with a corner up unless you pick another style."),
    ("Tip angle", "Hexagons only: turns the upper corner away from straight up, up to 30° either way."),
    ("Cell size", "Diameter of the largest circle that fits in a hole (5&nbsp;mm): a hexagon's width, a triangle's inscribed circle."),
    ("Rib width", "Smallest wall left between holes (1.6&nbsp;mm)."),
    ("Edge margin", "Smallest solid left between a hole and the region's edge (2&nbsp;mm)."),
    ("Print direction", "Which way is up on the printer: a plane, planar face, straight edge or mate connector. Empty means +Z. The arrow flips it."),
    ("Max overhang angle", "Measured from vertical, as slicers do (45°)."),
    ("Max bridge width", "Widest flat ceiling a hole may have (5&nbsp;mm). 0 allows no bridges."),
    ("Pattern angle", "Rotates the pattern. Used only when every orientation prints."),
    ("Cut direction", "The hole axis. Empty means the face's own normal. Required for curved faces."),
    ("End type", "<b>Through all</b>, or <b>Blind</b> with a depth. Cuts into the part; the arrow reverses it, <b>Both sides</b> cuts both ways."),
    ("Parts to cut", "Empty means the face's own part (every part, for sketch regions)."),
    ("Keep pattern sketch", "Leaves the cell outlines behind as a sketch."),
]

STEPS = [
    "Start <b>Printable Pattern</b> and select one or more <b>faces or sketch regions</b>.",
    "Choose the <b>pattern</b>, then <b>cell size</b>, <b>rib width</b> and <b>edge margin</b>.",
    "Set the <b>print direction</b>: pick the plane or face that sits on the build plate.",
    "Check <b>max overhang angle</b> and <b>max bridge width</b> against your printer.",
    "Choose <b>Through all</b> or <b>Blind</b> and accept.",
    "Read the blue message: it gives the cell count, the shape chosen and why. A yellow message means a forced style will not print cleanly.",
]

NOTES = [
    "<b>Whole cells only.</b> A cell that would cross the region's edge is left out, so no hole is clipped.",
    "<b>Hexagons are regular, corner up.</b> On an upright wall their roofs overhang 60°; the message says so when that is over your limit. To stay within it, choose <b>Point up, stretched to print</b>, or <b>Flat top</b> to bridge.",
    "<b>No cells?</b> Reduce the cell size, rib width or edge margin.",
    "<b>Limit:</b> 4000 cells per region. Increase the cell size if you reach it.",
]

CURVED = [
    "<b>Through all</b> cuts everything behind the face. On a tube, that is both walls.",
    "<b>Blind</b> makes pockets whose floor follows the face: the depth is measured square to the surface.",
    "Cells keep the edge margin from the face's edges and from where the face curves out of view.",
]

CSS = '''
@page { size: Letter; margin: 0; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }
body { font-family: "Segoe UI", Arial, Helvetica, sans-serif; font-size: 8.1pt; line-height: 1.27; color: #1d2733; }
.page { width: 8.5in; padding: 0.3in 0.5in 0.2in; }
header { display: flex; justify-content: space-between; align-items: flex-end; border-bottom: 2.2pt solid #22384d; padding-bottom: 5pt; }
h1 { font-size: 20pt; margin: 0; letter-spacing: -0.2pt; color: #16222e; }
.lede { font-size: 9.6pt; margin: 2pt 0 0; color: #3b4a5a; }
.meta { font-size: 7.3pt; color: #5b6b7b; text-align: right; white-space: nowrap; }
h2 { font-size: 10pt; margin: 8pt 0 3.5pt; color: #1f6f8b; text-transform: uppercase; letter-spacing: 0.6pt; }
.cols { display: grid; grid-template-columns: 0.8fr 1.2fr; column-gap: 0.3in; }
ol { margin: 0; padding-left: 14pt; }
ol li { margin-bottom: 3pt; }
ul { margin: 0; padding-left: 11pt; }
ul li { margin-bottom: 2.6pt; }
table { border-collapse: collapse; width: 100%; }
td { vertical-align: top; padding: 1.4pt 0; border-bottom: 0.5pt solid #d5dce4; }
td.k { font-weight: 600; white-space: nowrap; padding-right: 8pt; color: #16222e; }
.install { margin-top: 6pt; padding: 5pt 7pt; background: #eef3f7; border-left: 2.5pt solid #1f6f8b; font-size: 7.9pt; }
.grid { display: grid; grid-template-columns: 2.05in repeat(3, 1fr); column-gap: 0.14in; row-gap: 2pt; align-items: center; }
.grid .head { font-weight: 600; font-size: 8.6pt; color: #16222e; text-align: center; padding-right: 0.22in; }
.rowlab b { display: block; font-size: 8.8pt; color: #16222e; }
.rowlab span { font-size: 7.6pt; color: #4a5969; }
.cell svg { width: 100%; display: block; }
.cell .cap { font-size: 7.3pt; color: #4a5969; text-align: center; padding-right: 0.22in; margin-top: 1pt; }
.legend { font-size: 7.3pt; color: #5b6b7b; margin-top: 4pt; }
.legend .red { color: #c0392b; font-weight: 600; }
.curved { display: grid; grid-template-columns: 2.55in 1fr 1.25fr; column-gap: 0.25in; align-items: start; }
.curved svg { width: 100%; display: block; }
p { margin: 0 0 4pt; }
'''


def build_html():
    grid = ['<div></div>'] + ['<div class="head">%s</div>' % t for t in ("Circles", "Hexagons", "Triangles")]
    for beta, title, note in ROWS:
        grid.append('<div class="rowlab"><b>%s</b><span>%s</span></div>' % (title, note))
        for kind in (pm.CIRCLES, pm.HEXAGONS, pm.TRIANGLES):
            grid.append('<div class="cell">%s<div class="cap">%s</div></div>' % swatch(kind, beta))
    return '''<!doctype html><html><head><meta charset="utf-8"><title>Printable Pattern - User Guide</title>
<style>%s</style></head><body><div class="page">
<header><div><h1>Printable Pattern</h1>
<p class="lede">An Onshape custom feature that cuts hole patterns shaped to print without supports.</p></div>
<div class="meta">User guide &middot; feature version of 2026-10-11<br>FeatureScript 3083 &middot; units: mm</div></header>

<div class="cols">
<div>
<h2>Quick start</h2>
<ol>%s</ol>
<div class="install"><b>Install:</b> paste <b>PatternMaker.fs</b> into a Feature Studio and commit. The feature then appears in the Part Studio toolbar under custom features.</div>
<h2>Good to know</h2>
<ul>%s</ul>
</div>
<div>
<h2>Settings</h2>
<table>%s</table>
</div>
</div>

<h2>How the holes adapt to the print direction</h2>
<div class="grid">%s</div>
<div class="legend"><span class="red">Red arrow</span> = print up. Pictures are drawn by the feature's own math: cell %g mm, rib %g mm, edge margin %g mm, max overhang %g°, max bridge %g mm.</div>

<h2>Curved faces</h2>
<div class="curved">%s
<div>
<p>Select the curved face, then set <b>Cut direction</b> to a plane, planar face or straight edge. The holes go straight through along that direction.</p>
<p>Which way the direction points does not matter: the cut goes into the part.</p>
</div>
<ul>%s</ul>
</div>
</div></body></html>''' % (
        CSS,
        "".join("<li>%s</li>" % t for t in STEPS),
        "".join("<li>%s</li>" % t for t in NOTES),
        "".join('<tr><td class="k">%s</td><td>%s</td></tr>' % kv for kv in SETTINGS),
        "".join(grid),
        CELL, RIB, MARGIN, MAX_OH, MAX_BRIDGE,
        CUT_DIAGRAM,
        "".join("<li>%s</li>" % t for t in CURVED),
    )


def main():
    browser = next((b for b in BROWSERS if b and os.path.exists(b)), None)
    if browser is None:
        sys.exit("Chrome or Edge not found; set the CHROME environment variable to its path.")
    with tempfile.TemporaryDirectory() as tmp:
        html = os.path.join(tmp, "guide.html")
        with open(html, "w", encoding="utf-8") as f:
            f.write(build_html())
        url = "file:///" + html.replace("\\", "/")
        common = [browser, "--headless=new", "--disable-gpu", "--user-data-dir=" + os.path.join(tmp, "profile")]
        subprocess.run(common + ["--no-pdf-header-footer", "--print-to-pdf=" + OUT, url], check=True, capture_output=True)
        if len(sys.argv) > 1:  # optional: also save a PNG of the page for a visual check
            subprocess.run(common + ["--hide-scrollbars", "--force-device-scale-factor=2", "--window-size=816,1056",
                                     "--screenshot=" + os.path.abspath(sys.argv[1]), url], check=True, capture_output=True)
    print("wrote", OUT)


if __name__ == "__main__":
    main()
