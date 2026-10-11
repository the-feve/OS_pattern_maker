"""Checks the pattern math against independent geometric tests.

Run:  python3 -m unittest tools/test_pattern_math.py   (from the repo root)

What is checked, for every pattern x orientation x limits combination:
  1. Frame: u, v, N orthonormal; v is "up" (Z projected into the plane);
     the sketch plane's y axis cross(N, u) equals v.
  2. Printability: every placed hole's outline passes check_printable, and
     separately, every non-bridge wall's real 3D overhang angle (computed from
     the lifted 3D wall normal, not from the 2D reduction) is within the limit.
  3. Ribs: no two holes closer than the rib width.
  4. Margin: every hole lies inside the region, at least `margin` from its edge.
"""

import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import pattern_math as pm  # noqa: E402

RECT = [[(0, 0), (60, 0), (60, 40), (0, 40)]]
L_WITH_HOLE = [
    [(0, 0), (70, 0), (70, 25), (30, 25), (30, 55), (0, 55)],
    [(10, 8), (22, 8), (22, 18), (10, 18)][::-1],
]


def lift(frame, m2):
    return pm.add(pm.scale(frame["u"], m2[0]), pm.scale(frame["v"], m2[1]))


def z_at(beta_deg, N=(0.0, 1.0, 0.0), azimuth_deg=0.0):
    """Print axis at angle beta from the hole axis N=(0,1,0), tipped toward
    a direction in the region plane chosen by azimuth."""
    b, a = math.radians(beta_deg), math.radians(azimuth_deg)
    t = (math.cos(a), 0.0, math.sin(a))  # in-plane direction (plane is xz)
    return pm.add(pm.scale(N, math.cos(b)), pm.scale(t, math.sin(b)))


class FrameTests(unittest.TestCase):
    def test_orthonormal_and_up(self):
        N = (0.0, 1.0, 0.0)
        for beta in (0, 10, 45, 60, 89.5, 90):
            for az in (0, 37, 90, 200):
                Z = z_at(beta, N, az)
                f = pm.pattern_frame(N, Z, 45)
                u, v = f["u"], f["v"]
                self.assertAlmostEqual(pm.norm(u), 1, 9)
                self.assertAlmostEqual(pm.norm(v), 1, 9)
                self.assertAlmostEqual(pm.dot(u, v), 0, 9)
                self.assertAlmostEqual(pm.dot(v, N), 0, 9)
                y = pm.cross(N, u)
                for a, b in zip(y, v):
                    self.assertAlmostEqual(a, b, 9)
                if f["constrained"]:
                    self.assertGreater(pm.dot(v, Z), 0)  # v points up

    def test_limits(self):
        N = (0.0, 1.0, 0.0)
        f = pm.pattern_frame(N, z_at(90, N), 45)
        self.assertTrue(f["constrained"] and f["bridges"])
        self.assertAlmostEqual(math.degrees(f["theta_min"]), 45, 9)
        f = pm.pattern_frame(N, z_at(30, N), 45)
        self.assertFalse(f["constrained"])
        f = pm.pattern_frame(N, z_at(60, N), 45)
        self.assertTrue(f["constrained"] and not f["bridges"])
        # sin(45)/sin(60) -> effective 54.7 deg -> ceilings need >= 35.3 deg
        self.assertAlmostEqual(math.degrees(f["theta_min"]), 90 - math.degrees(math.asin(math.sin(math.radians(45)) / math.sin(math.radians(60)))), 9)


class PatternTests(unittest.TestCase):
    def run_case(self, kind, cell, rib, beta, max_oh, max_b, loops, az=0.0, style="auto", expect_ok=True):
        N = (0.0, 1.0, 0.0)
        frame = pm.pattern_frame(N, z_at(beta, N, az), max_oh, pattern_angle_deg=17.0)
        cs = pm.build_cell_set(kind, cell, rib, frame, max_b, style)
        margin = rib
        placed = pm.place_holes(cs, loops, margin)
        ctx = "%s cell=%s rib=%s beta=%s oh=%s bridge=%s -> %s" % (kind, cell, rib, beta, max_oh, max_b, cs["label"])
        self.assertGreater(len(placed), 0, ctx)
        if expect_ok:
            self.assertEqual(cs["warnings"], [], ctx)
        # 2. printability (2D rule) and 3D overhang
        sin_lim = math.sin(math.radians(max_oh)) + 1e-9
        Z = z_at(beta, N, az)
        for c, h in placed[:6]:
            poly = pm.outline(h, 180)
            probs = pm.check_printable(poly, frame, max_b)
            if expect_ok:
                self.assertEqual(probs, [], ctx)
            n = len(poly)
            for i in range(n):
                a, b = poly[i], poly[(i + 1) % n]
                dx, dy = b[0] - a[0], b[1] - a[1]
                L = math.hypot(dx, dy)
                if L < 1e-12:
                    continue
                slope = math.atan2(abs(dy), abs(dx))
                if slope < frame["theta_min"] - 1e-6:
                    continue  # bridge / flat-cap run, judged by width above
                m3 = lift(frame, (dy / L, -dx / L))  # outward (into material) wall normal
                if expect_ok:
                    self.assertLessEqual(pm.dot(m3, Z), sin_lim, ctx + " 3D overhang")
        # 3. ribs
        mr = pm.min_rib(placed, 48, limit=6)
        self.assertGreaterEqual(mr, rib - 0.01, ctx)
        # 4. margin
        for c, h in placed:
            for p in pm.placed_outline(c, h, 24):
                self.assertTrue(pm.inside_region(p, loops), ctx)
                self.assertGreaterEqual(pm.dist_to_loops(p, loops), margin - 1e-6, ctx)
        return cs, placed

    def test_matrix(self):
        n = 0
        for kind in (pm.CIRCLES, pm.HEXAGONS, pm.TRIANGLES):
            for beta in (0, 30, 50, 70, 90):
                for max_oh in (30, 45, 60):
                    for max_b in (0.0, 3.0, 6.0):
                        for cell, rib in ((4.0, 1.2), (7.0, 2.0)):
                            for loops in (RECT, L_WITH_HOLE):
                                self.run_case(kind, cell, rib, beta, max_oh, max_b, loops)
                                n += 1
        self.assertEqual(n, 540)

    def test_azimuth_independent(self):
        # Tilting the print axis toward any in-plane direction must still be printable.
        for az in (0, 45, 135, 250):
            for kind in (pm.CIRCLES, pm.HEXAGONS, pm.TRIANGLES):
                self.run_case(kind, 5.0, 1.5, 90, 45, 4.0, RECT, az=az)

    def test_expected_choices(self):
        N = (0.0, 1.0, 0.0)
        side = pm.pattern_frame(N, z_at(90, N), 45)  # horizontal holes
        flat = pm.pattern_frame(N, z_at(0, N), 45)   # vertical holes
        tilt = pm.pattern_frame(N, z_at(70, N), 45)  # tilted, no bridging
        lab = lambda k, f, cell, b: pm.build_cell_set(k, cell, 1.5, f, b)["label"]
        self.assertEqual(lab(pm.CIRCLES, side, 5, 5), "circles")  # cap 3.5 mm <= 5
        self.assertTrue(lab(pm.CIRCLES, side, 10, 5).startswith("flat teardrops"))
        self.assertTrue(lab(pm.CIRCLES, tilt, 5, 5).startswith("teardrops"))
        self.assertEqual(lab(pm.CIRCLES, flat, 10, 0), "circles")
        self.assertEqual(lab(pm.HEXAGONS, side, 5, 5), "hexagons (flat top)")
        self.assertEqual(lab(pm.HEXAGONS, side, 12, 5), "hexagons (pointy, 45 deg roof)")
        self.assertEqual(lab(pm.HEXAGONS, flat, 12, 0), "hexagons (flat top)")
        self.assertEqual(lab(pm.TRIANGLES, side, 5, 5), "triangles (down ones 45 deg roof, 5.0 mm flat)")
        self.assertTrue(lab(pm.TRIANGLES, tilt, 5, 5).startswith("triangles (down ones 41 deg roof)"))
        self.assertEqual(lab(pm.TRIANGLES, flat, 5, 0), "triangles")

    def test_forced_styles_warn(self):
        N = (0.0, 1.0, 0.0)
        side = pm.pattern_frame(N, z_at(90, N), 45)
        self.assertTrue(pm.build_cell_set(pm.CIRCLES, 12, 1.5, side, 3, "circle")["warnings"])
        self.assertTrue(pm.build_cell_set(pm.HEXAGONS, 12, 1.5, side, 3, "flat_top")["warnings"])
        self.assertTrue(pm.build_cell_set(pm.TRIANGLES, 12, 1.5, side, 3, "full")["warnings"])

    def test_checker_catches_bad_shapes(self):
        # The independent checker must reject the shapes the feature avoids.
        N = (0.0, 1.0, 0.0)
        side = pm.pattern_frame(N, z_at(90, N), 45)
        tilt = pm.pattern_frame(N, z_at(70, N), 45)
        flat_hex = pm.circumscribed(pm.hex_normals(True, 0), 6)
        regular_pointy = pm.circumscribed(pm.hex_normals(False, 30), 6)
        self.assertTrue(pm.check_printable(flat_hex, side, 3))      # 6.9 mm bridge
        self.assertFalse(pm.check_printable(flat_hex, side, 8))     # bridgeable
        self.assertTrue(pm.check_printable(flat_hex, tilt, 8))      # no bridging when tilted
        self.assertTrue(pm.check_printable(regular_pointy, side, 8))  # 30 deg roof < 45 needed
        self.assertTrue(pm.check_printable(pm.outline(pm.circle_shape(6), 180), side, 3))

    def test_hex_tiles_uniform_rib(self):
        # Regular flat-top honeycomb: neighbour holes exactly `rib` apart.
        N = (0.0, 1.0, 0.0)
        f = pm.pattern_frame(N, z_at(0, N), 45)
        cs, placed = self.run_case(pm.HEXAGONS, 6.0, 1.5, 0, 45, 0, RECT)
        self.assertAlmostEqual(pm.min_rib(placed), 1.5, 6)


if __name__ == "__main__":
    unittest.main()
