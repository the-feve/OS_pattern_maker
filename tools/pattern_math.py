"""Pure-math twin of src/PatternMaker.fs.

Everything here is plain 2D/3D vector math with no Onshape context, so it can be
checked locally at zero API cost (ONSHAPE_WORKFLOW.md section 3). The
FeatureScript functions with the same names mirror these one for one; when you
change one, change the other.

Units: mm and degrees at the API boundary, radians inside.

Conventions (see docs/DESIGN.md section 2):
  - Each region has a plane normal N. Holes are prisms along N.
  - Z is the printer's up axis. Its component in the region plane, normalised,
    is the pattern's local "up" v. Local "right" u = cross(v, N), so the
    pattern plane is plane(origin, N, u) and its sketch y axis is v.
  - A hole wall with in-plane outward normal m (pointing out of the hole, into
    material) has a 3D overhang of asin(max(0, m.v) * s) from vertical, where
    s = |Z projected into the plane| = sin(angle between Z and the hole axis).
    So the 2D rule is: every ceiling edge needs a slope from horizontal of at
    least theta_min = 90 - asin(sin(maxOverhang) / s).
"""

import math

EPS = 1e-9
BRIDGE_TOL_DEG = 1.0  # hole axis within this of horizontal -> flat ceilings are true bridges
MIN_FEATURE = 0.3  # mm; smaller truncated holes are dropped


# ---------------------------------------------------------------- vectors

def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def scale(a, k):
    return tuple(x * k for x in a)


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a):
    return math.sqrt(dot(a, a))


def unit(a):
    n = norm(a)
    return scale(a, 1.0 / n)


# ---------------------------------------------------------------- frame

def pattern_frame(N, Z, max_overhang_deg, pattern_angle_deg=0.0, plane_x=(1.0, 0.0, 0.0)):
    """Local 2D frame and printability limits for one region.

    Returns u, v (in-plane right/up), s (sin of angle between print axis and
    hole axis), constrained, theta_min (rad, min ceiling slope), bridges
    (True when flat ceilings are true horizontal bridges), angle_ignored.
    """
    N = unit(N)
    Z = unit(Z)
    zt = sub(Z, scale(N, dot(Z, N)))
    s = norm(zt)
    sin_oh = math.sin(math.radians(max_overhang_deg))
    constrained = s > sin_oh + EPS
    if s > 1e-9:
        v = unit(zt)
    else:
        v = unit(cross(N, plane_x))
    angle_ignored = False
    if not constrained:
        a = math.radians(pattern_angle_deg)
        v = add(scale(v, math.cos(a)), scale(cross(N, v), math.sin(a)))
    elif abs(pattern_angle_deg) > EPS:
        angle_ignored = True
    u = cross(v, N)
    if constrained:
        max_oh_eff = math.asin(min(1.0, sin_oh / s))
        theta_min = math.pi / 2 - max_oh_eff
    else:
        theta_min = 0.0
    bridges = constrained and s >= math.cos(math.radians(BRIDGE_TOL_DEG)) - EPS
    return {
        "u": u, "v": v, "N": N, "s": s,
        "constrained": constrained, "theta_min": theta_min, "bridges": bridges,
        "angle_ignored": angle_ignored,
    }


# ---------------------------------------------------------------- 2D shapes

def circumscribed(normals_deg, rho):
    """CCW polygon whose edges have the given outward-normal angles and sit at
    distance rho from the origin. Vertex k joins edges k and k+1."""
    ns = sorted(a % 360.0 for a in normals_deg)
    pts = []
    for k in range(len(ns)):
        pi_ = math.radians(ns[k])
        pj = math.radians(ns[(k + 1) % len(ns)])
        d = math.sin(pj - pi_)
        x = rho * (math.sin(pj) - math.sin(pi_)) / d
        y = rho * (math.cos(pi_) - math.cos(pj)) / d
        pts.append((x, y))
    return pts


def hex_normals(flat_top, theta_deg):
    """flat_top: regular hex with horizontal top/bottom edges.
    pointy: vertical side edges, upper/lower edges sloped theta from horizontal
    (theta = 30 is the regular pointy hex)."""
    if flat_top:
        return [30, 90, 150, 210, 270, 330]
    t = theta_deg
    return [0, 90 - t, 90 + t, 180, 270 - t, 270 + t]


def tri_up_normals(theta_deg):
    t = theta_deg
    return [90 - t, 90 + t, 270]


def clip_halfplane(pts, nx, ny, c):
    """Part of convex polygon pts with nx*x + ny*y <= c (Sutherland-Hodgman, one plane)."""
    out = []
    n = len(pts)
    for i in range(n):
        a, b = pts[i], pts[(i + 1) % n]
        fa = nx * a[0] + ny * a[1] - c
        fb = nx * b[0] + ny * b[1] - c
        if fa <= 0:
            out.append(a)
        if (fa < 0 < fb) or (fb < 0 < fa):
            t = fa / (fa - fb)
            out.append((a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])))
    return out


def roof_clip(pts, roof_deg, flat_width=None):
    """Replace a convex polygon's top with a peaked roof: two lines sloped
    roof_deg from horizontal meeting at the top of the polygon above its
    x-centre. flat_width (optional) cuts the peak flat where it is that wide."""
    xs = [p[0] for p in pts]
    px = (min(xs) + max(xs)) / 2
    py = max(p[1] for p in pts)
    th = math.radians(roof_deg)
    sn, cs = math.sin(th), math.cos(th)
    out = clip_halfplane(pts, sn, cs, px * sn + py * cs)
    out = clip_halfplane(out, -sn, cs, -px * sn + py * cs)
    if flat_width is not None:
        out = clip_halfplane(out, 0.0, 1.0, py - 0.5 * flat_width * math.tan(th))
    return out


def poly_shape(pts):
    return {"kind": "poly", "pts": pts}


def teardrop_shape(r, theta_deg, flat_width=None):
    """Circle of radius r plus a cap whose two lines are tangent to the circle
    with slope theta from horizontal. flat_width (if given) cuts the cap flat
    where it is that wide; if that is at or below the tangent points the
    shape is the circle with its top chord cut flat."""
    th = math.radians(theta_deg)
    apex = r / math.cos(th)
    y_t = r * math.cos(th)
    y_clip = None
    if flat_width is not None:
        y_clip = max(apex - 0.5 * flat_width * math.tan(th), y_t)
    return {"kind": "teardrop", "r": r, "theta": th, "apex": apex, "y_tan": y_t, "y_clip": y_clip}


def circle_shape(r):
    return {"kind": "circle", "r": r}


def teardrop_profile(shape):
    """Profile as sketch primitives: ('arc', start, mid, end) / ('line', a, b).
    Mirrors sketchHole in the .fs file."""
    r, th = shape["r"], shape["theta"]
    tr = (r * math.sin(th), shape["y_tan"])
    tl = (-tr[0], tr[1])
    items = [("arc", tr, (0.0, -r), tl)]
    if shape["y_clip"] is None:
        top = (0.0, shape["apex"])
        items += [("line", tl, top), ("line", top, tr)]
    else:
        yc = shape["y_clip"]
        half = (shape["apex"] - yc) / math.tan(th)
        cl, cr = (-half, yc), (half, yc)
        if yc - shape["y_tan"] > 1e-9:
            items += [("line", tl, cl), ("line", cl, cr), ("line", cr, tr)]
        else:
            items += [("line", tl, tr)]
    return items


def outline(shape, n=96):
    """CCW polygon approximating the hole boundary (checks/rendering only)."""
    k = shape["kind"]
    if k == "poly":
        return list(shape["pts"])
    if k == "circle":
        r = shape["r"]
        return [(r * math.cos(2 * math.pi * i / n), r * math.sin(2 * math.pi * i / n)) for i in range(n)]
    # teardrop, walked CCW: from the left tangent point down around the
    # bottom to the right tangent point, then up over the cap and back.
    r, th = shape["r"], shape["theta"]
    a1 = math.pi / 2 + th  # left tangent point
    span = 2 * math.pi - 2 * th
    m = max(8, int(n * span / (2 * math.pi)))
    pts = [(r * math.cos(a1 + span * i / m), r * math.sin(a1 + span * i / m)) for i in range(m + 1)]
    if shape["y_clip"] is None:
        pts.append((0.0, shape["apex"]))
    else:
        yc = shape["y_clip"]
        half = (shape["apex"] - yc) / math.tan(th)
        if yc - shape["y_tan"] > 1e-9:
            pts.append((half, yc))
            pts.append((-half, yc))
    return pts


def bound_radius(shape):
    k = shape["kind"]
    if k == "poly":
        return max(math.hypot(x, y) for x, y in shape["pts"])
    if k == "circle":
        return shape["r"]
    if shape["y_clip"] is None:
        return max(shape["r"], shape["apex"])
    half = (shape["apex"] - shape["y_clip"]) / math.tan(shape["theta"])
    return max(shape["r"], math.hypot(half, shape["y_clip"]))


# ---------------------------------------------------------------- patterns

CIRCLES, HEXAGONS, TRIANGLES = "circles", "hexagons", "triangles"


def build_cell_set(kind, cell, rib, frame, max_bridge, style="auto"):
    """Lattice (t1, t2) and the holes placed per lattice point.

    Returns {"t1", "t2", "holes": [(offset, shape)], "label", "warnings"}.
    cell = inscribed diameter of each hole, rib = wall between holes.
    The tile (rib centre-lines) is the hole outline offset by rib/2, which is
    what guarantees a uniform wall: tiles tessellate exactly.
    """
    r = cell / 2.0
    R = r + rib / 2.0
    constrained = frame["constrained"]
    tmin = math.degrees(frame["theta_min"])
    bridges = frame["bridges"]
    warnings = []

    if kind == CIRCLES:
        cap = 2 * r * math.sin(frame["theta_min"])  # chord of the too-flat part of the top
        circle_ok = (not constrained) or (bridges and cap <= max_bridge + 1e-9)
        if style == "auto":
            if circle_ok:
                style = "circle"
            elif bridges and max_bridge > MIN_FEATURE:
                style = "flat_teardrop"
            else:
                style = "teardrop"
        th_td = tmin if constrained else 45.0
        if style == "circle":
            if not circle_ok:
                warnings.append("circle tops exceed the overhang/bridge limits")
            hole = circle_shape(r)
            th_tile = 30.0
            label = "circles"
        else:
            if style == "flat_teardrop" and constrained and not bridges:
                warnings.append("flat-topped teardrops need a horizontal hole axis to bridge")
            hole = teardrop_shape(r, th_td, max_bridge if style == "flat_teardrop" else None)
            th_tile = max(30.0, th_td)
            label = "%s (%.0f deg cap)" % (style.replace("_", " ") + "s", th_td)
        tile = circumscribed(hex_normals(False, th_tile), R)
        t1 = add(tile[5], tile[0])
        t2 = add(tile[0], tile[1])
        return {"t1": t1, "t2": t2, "holes": [((0.0, 0.0), hole)], "label": label, "warnings": warnings}

    if kind == HEXAGONS:
        top_edge = 2 * r * math.tan(math.radians(30))
        flat_ok = (not constrained) or (tmin <= 60 + 1e-9 and bridges and top_edge <= max_bridge + 1e-9)
        if style == "auto":
            style = "flat_top" if flat_ok else "pointy"
        if style == "flat_top":
            if not flat_ok:
                warnings.append("flat-top hexagon ceilings exceed the overhang/bridge limits")
            normals = hex_normals(True, 0)
            label = "hexagons (flat top)"
        else:
            th = max(30.0, tmin) if constrained else 30.0
            normals = hex_normals(False, th)
            label = "hexagons (pointy, %.0f deg roof)" % th
        tile = circumscribed(normals, R)
        hole = poly_shape(circumscribed(normals, r))
        t1 = add(tile[0], tile[1])
        t2 = add(tile[1], tile[2])
        return {"t1": t1, "t2": t2, "holes": [((0.0, 0.0), hole)], "label": label, "warnings": warnings}

    if kind == TRIANGLES:
        th = max(60.0, tmin) if constrained else 60.0
        n_up = tri_up_normals(th)
        tile = circumscribed(n_up, R)  # sorted normals: [90-th, 90+th, 270] -> [apex C, bottom-left A, bottom-right B]
        C, A, B = tile[0], tile[1], tile[2]
        up = circumscribed(n_up, r)
        down = [(-x, -y) for x, y in up]  # point reflection: flat top, apex at bottom
        t1 = sub(B, A)
        t2 = sub(C, A)
        down_off = add(B, C)
        holes = [((0.0, 0.0), poly_shape(up))]
        top_w = math.dist(down[1], down[2])  # reflected A,B = top edge
        down_ok = (not constrained) or (bridges and top_w <= max_bridge + 1e-9)
        if style == "auto":
            style = "full" if down_ok else "peaked"
        if style == "full":
            if not down_ok:
                warnings.append("down-pointing triangle ceilings exceed the overhang/bridge limits")
            holes.append((down_off, poly_shape(down)))
            label = "triangles"
        elif style == "peaked":
            roof = tmin if constrained else 45.0
            flat = max_bridge if (bridges and max_bridge > MIN_FEATURE) else None
            holes.append((down_off, poly_shape(roof_clip(down, roof, flat))))
            label = "triangles (down ones %.0f deg roof%s)" % (roof, "" if flat is None else ", %.1f mm flat" % flat)
        else:
            label = "triangles (up only)"
        if th > 60 + 1e-9:
            label += ", %.0f deg sides" % th
        return {"t1": t1, "t2": t2, "holes": holes, "label": label, "warnings": warnings}

    raise ValueError(kind)


def lattice_candidates(cellset, box, max_cells=20000):
    """Hole centres covering box = (xmin, ymin, xmax, ymax), lattice centred on
    the box centre. Returns [(center, shape)] with shape untranslated."""
    xmin, ymin, xmax, ymax = box
    c0 = ((xmin + xmax) / 2, (ymin + ymax) / 2)
    t1, t2 = cellset["t1"], cellset["t2"]
    rb = max(bound_radius(h) + math.hypot(*off) for off, h in cellset["holes"])
    det = t1[0] * t2[1] - t1[1] * t2[0]
    ilo = jlo = math.inf
    ihi = jhi = -math.inf
    for cx in (xmin - rb, xmax + rb):
        for cy in (ymin - rb, ymax + rb):
            dx, dy = cx - c0[0], cy - c0[1]
            i = (dx * t2[1] - dy * t2[0]) / det
            j = (t1[0] * dy - t1[1] * dx) / det
            ilo, ihi = min(ilo, i), max(ihi, i)
            jlo, jhi = min(jlo, j), max(jhi, j)
    ilo, ihi, jlo, jhi = math.floor(ilo), math.ceil(ihi), math.floor(jlo), math.ceil(jhi)
    if (ihi - ilo + 1) * (jhi - jlo + 1) * len(cellset["holes"]) > max_cells:
        raise ValueError("too many cells")
    out = []
    for j in range(jlo, jhi + 1):
        for i in range(ilo, ihi + 1):
            base = (c0[0] + i * t1[0] + j * t2[0], c0[1] + i * t1[1] + j * t2[1])
            for off, h in cellset["holes"]:
                c = (base[0] + off[0], base[1] + off[1])
                rh = bound_radius(h)
                if xmin - rh <= c[0] <= xmax + rh and ymin - rh <= c[1] <= ymax + rh:
                    out.append((c, h))
    return out


# ---------------------------------------------------------------- region tests (Python stand-in for evDistance)

def point_in_polygon(p, poly):
    x, y = p
    inside = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            xi = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if xi > x:
                inside = not inside
    return inside


def point_seg_dist(p, a, b):
    ab = sub(b, a)
    t = dot(sub(p, a), ab) / max(dot(ab, ab), 1e-300)
    t = max(0.0, min(1.0, t))
    q = add(a, scale(ab, t))
    return math.dist(p, q)


def dist_to_loops(p, loops):
    return min(point_seg_dist(p, lp[i], lp[(i + 1) % len(lp)]) for lp in loops for i in range(len(lp)))


def inside_region(p, loops):
    """loops[0] outer, the rest holes (even-odd)."""
    return sum(point_in_polygon(p, lp) for lp in loops) % 2 == 1


def place_holes(cellset, loops, margin):
    xs = [x for lp in loops for x, _ in lp]
    ys = [y for lp in loops for _, y in lp]
    box = (min(xs), min(ys), max(xs), max(ys))
    kept = []
    for c, h in lattice_candidates(cellset, box):
        # Mirrors the .fs test: the hole's own centroid lies in the region
        # and the whole hole outline stays `margin` clear of every region edge.
        poly = placed_outline(c, h, 72)
        if inside_region(centroid(poly), loops) and poly_loops_dist(poly, loops) >= margin - 1e-9:
            kept.append((c, h))
    return kept


def centroid(poly):
    a = cx = cy = 0.0
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        w = x1 * y2 - x2 * y1
        a += w
        cx += (x1 + x2) * w
        cy += (y1 + y2) * w
    return (cx / (3 * a), cy / (3 * a))


def segs_cross(a, b, c, d):
    def orient(p, q, r):
        return (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])
    return (orient(a, b, c) > 0) != (orient(a, b, d) > 0) and (orient(c, d, a) > 0) != (orient(c, d, b) > 0)


def poly_loops_dist(poly, loops):
    """Distance between a closed outline and the region's boundary loops (0 if they cross)."""
    segs = [(lp[i], lp[(i + 1) % len(lp)]) for lp in loops for i in range(len(lp))]
    n = len(poly)
    best = math.inf
    for i in range(n):
        a, b = poly[i], poly[(i + 1) % n]
        for c, d in segs:
            if segs_cross(a, b, c, d):
                return 0.0
            best = min(best, point_seg_dist(a, c, d), point_seg_dist(c, a, b))
    return best


# ---------------------------------------------------------------- independent checks

def placed_outline(c, h, n=96):
    return [(x + c[0], y + c[1]) for x, y in outline(h, n)]


def is_convex_ccw(poly):
    n = len(poly)
    for i in range(n):
        a, b, c = poly[i], poly[(i + 1) % n], poly[(i + 2) % n]
        cr = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
        if cr < -1e-9:
            return False
    return True


def check_printable(poly, frame, max_bridge):
    """Independent overhang check of one hole outline (CCW, y = up).
    Returns list of problems (empty = printable)."""
    if not frame["constrained"]:
        return []
    tmin = frame["theta_min"]
    problems = []
    if not is_convex_ccw(poly):
        problems.append("hole not convex (ceiling valley)")
    n = len(poly)
    flat_span = []  # x-extent of each run of too-flat ceiling edges
    run = None
    for i in range(n):
        a, b = poly[i], poly[(i + 1) % n]
        dx, dy = b[0] - a[0], b[1] - a[1]
        L = math.hypot(dx, dy)
        if L < 1e-12:
            continue
        ceiling = -dx / L > 1e-9  # outward normal (dy, -dx)/L has +y component
        slope = math.atan2(abs(dy), abs(dx))
        if ceiling and slope < tmin - 1e-6:
            if run is None:
                run = [min(a[0], b[0]), max(a[0], b[0])]
            else:
                run = [min(run[0], a[0], b[0]), max(run[1], a[0], b[0])]
        else:
            if run is not None:
                flat_span.append(run[1] - run[0])
                run = None
    if run is not None:
        flat_span.append(run[1] - run[0])
    for w in flat_span:
        if not frame["bridges"]:
            problems.append("ceiling flatter than %.1f deg (width %.2f) and axis not horizontal"
                            % (math.degrees(tmin), w))
        elif w > max_bridge + 1e-6:
            problems.append("bridge %.2f mm > max %.2f mm" % (w, max_bridge))
    return problems


def convex_dist(P, Q):
    """Min distance between two convex polygons (0 if they overlap)."""
    def sep_axis_overlap():
        for poly in (P, Q):
            n = len(poly)
            for i in range(n):
                a, b = poly[i], poly[(i + 1) % n]
                ax = (b[1] - a[1], a[0] - b[0])
                pa = [dot(p, ax) for p in P]
                qa = [dot(q, ax) for q in Q]
                if max(pa) < min(qa) or max(qa) < min(pa):
                    return False
        return True
    if sep_axis_overlap():
        return 0.0
    best = math.inf
    for A, B in ((P, Q), (Q, P)):
        m = len(B)
        for p in A:
            for i in range(m):
                best = min(best, point_seg_dist(p, B[i], B[(i + 1) % m]))
    return best


def min_rib(placed, n=96, cutoff=10.0, limit=None):
    """Smallest gap between any two placed holes. Pairs further apart than
    their bounding radii + cutoff are skipped; limit checks only the first
    `limit` holes against all others."""
    outs = [placed_outline(c, h, n) for c, h in placed]
    rads = [bound_radius(h) for _, h in placed]
    best = math.inf
    for i in range(len(placed) if limit is None else min(limit, len(placed))):
        for j in range(len(placed)):
            if j == i:
                continue
            ci, cj = placed[i][0], placed[j][0]
            if math.dist(ci, cj) > rads[i] + rads[j] + min(best, cutoff):
                continue
            best = min(best, convex_dist(outs[i], outs[j]))
    return best
