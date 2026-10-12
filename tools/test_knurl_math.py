"""Checks knurl_math against independent 3D geometry.

Run:  python -m unittest tools.test_knurl_math   (from the repo root)

The feature sketches a V on a plane square to the cylinder's axis, extrudes
it along a straight line into a prism, and copies that prism around and
along the cylinder. These tests rebuild the prisms from exactly those
ingredients and check that together they make the groove asked for.
"""

import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import knurl_math as km  # noqa: E402
import pattern_math as pm  # noqa: E402  (vector helpers only)

R_HAT = (1.0, 0.0, 0.0)   # radial, at the middle of the seed segment
T_HAT = (0.0, 1.0, 0.0)   # around the cylinder
Z_HAT = (0.0, 0.0, 1.0)   # the axis

CASES = [
    # radius, length, pitch, depth
    (5.0, 100.0, 2.0, 0.5),
    (10.0, 20.0, 2.0, 0.5),
    (20.0, 30.0, 2.0, 0.5),
    (4.0, 12.0, 1.5, 0.4),
    (30.0, 15.0, 3.0, 1.0),
    (12.7, 250.0, 5.0, 1.0),   # the owner's rod, where short segments showed 0.3 mm steps
]


def seg_dir(layout):
    b = math.radians(layout["seg_helix"])
    return (0.0, math.sin(b), math.cos(b))


def wall_normal(layout):
    """Unit normal of one wall of a segment: the plane through the segment's
    direction and the V's edge as sketched (1 deep, `slope` wide, square to the axis)."""
    edge = pm.add(R_HAT, pm.scale(T_HAT, layout["slope"]))
    return pm.unit(pm.cross(seg_dir(layout), edge))


def in_groove(lay, z, r, theta):
    """Is the point (r, theta, z) inside any segment of the groove whose row-0
    segment has its middle at angle 0, z = 0? Rows continue both ways."""
    tan_seg = math.tan(math.radians(lay["seg_helix"]))
    step = math.radians(lay["step"])
    j0 = round(z / lay["row_height"])
    span = int(lay["reach"] / lay["row_height"]) + 2
    for j in range(j0 - span, j0 + span + 1):
        dz = z - j * lay["row_height"]
        if abs(dz) > lay["reach"] + 1e-12:
            continue
        x = r * math.cos(theta - j * step)   # the segment's own frame: x radial at its middle
        y = r * math.sin(theta - j * step)
        if x >= lay["apex_radius"] and abs(y - dz * tan_seg) <= lay["slope"] * (x - lay["apex_radius"]):
            return True
    return False


def groove_edges(lay, z, radius):
    """Where the groove's two edges are at this height, as distances around the
    cylinder from the true helix. Bisection outward from the helix."""
    centre = z * math.tan(math.radians(lay["helix"])) / radius
    out = []
    for sign in (-1, 1):
        inside, outside = 0.0, 4.0 * lay["pitch_around"] / radius
        for _ in range(40):
            mid = (inside + outside) / 2
            if in_groove(lay, z, radius, centre + sign * mid):
                inside = mid
            else:
                outside = mid
        out.append(inside * radius)
    return out


class KnurlTests(unittest.TestCase):
    def test_whole_number_of_grooves(self):
        for radius in (3.0, 10.0, 25.4, 60.0):
            for pitch in (0.8, 1.5, 2.0, 3.3):
                lay = km.knurl_layout(radius, 30, pitch, 0.4, 30, 90)
                self.assertEqual(lay["count"], int(lay["count"]))
                self.assertAlmostEqual(lay["count"] * lay["pitch_around"], 2 * math.pi * radius, 9)
                if lay["count"] > 3:
                    self.assertLess(abs(lay["pitch_around"] - pitch) / pitch, 0.5 / (lay["count"] - 0.5) + 1e-9)
        self.assertEqual(km.knurl_layout(0.5, 10, 5, 0.1, 30, 90)["count"], 3)  # never fewer than 3

    def test_rows_fill_the_length_and_keep_the_lattice(self):
        for radius, length, pitch, depth in CASES:
            for helix in (10, 30, 45, 60):
                for fine in (False, True):
                    for bore in (False, True):
                        lay = km.knurl_layout(radius, length, pitch, depth, helix, 90, bore=bore, fine=fine)
                        self.assertAlmostEqual(lay["rows"] * lay["row_height"], length, 9)
                        # After `facets` rows a groove has moved exactly one pitch around,
                        # so every row is the one below it, turned by `step`.
                        self.assertAlmostEqual(lay["facets"] * lay["step"], 360.0 / lay["count"], 9)
                        # ...and that is what running at `helix` along the surface does.
                        around = lay["row_height"] * math.tan(math.radians(lay["helix"]))
                        self.assertAlmostEqual(math.degrees(around / radius), lay["step"], 9)
        # The helix angle is only nudged: on a long face it stays close to the request.
        self.assertAlmostEqual(km.knurl_layout(10.0, 200.0, 2.0, 0.5, 30, 90)["helix"], 30, 0)

    def test_depth_straddles_the_target(self):
        # The tip is sag/2 too deep at the middle of a row and sag/2 too shallow at its ends.
        for radius, length, pitch, depth in CASES:
            for helix in (10, 30, 45, 60):
                for bore in (False, True):
                    lay = km.knurl_layout(radius, length, pitch, depth, helix, 90, bore=bore)
                    bottom = radius + depth if bore else radius - depth
                    half_span = lay["row_height"] / 2 * math.tan(math.radians(lay["seg_helix"]))
                    self.assertAlmostEqual(lay["apex_radius"], bottom - lay["sag"] / 2, 9)
                    self.assertAlmostEqual(math.hypot(lay["apex_radius"], half_span), bottom + lay["sag"] / 2, 9)
                    if lay["facets"] < km.MAX_FACETS:
                        self.assertLessEqual(lay["sag"], km.FACET_TOL * depth + 1e-9)

    def test_shaft_grooves_have_no_steps(self):
        # On a shaft every segment runs until it has left the surface, so the
        # groove is a union of overlapping prisms. Walk along it in fine steps:
        # its edges at the surface must move smoothly and stay near the design
        # width, and its ends must be outside the surface.
        for radius, length, pitch, depth in CASES:
            for helix, groove in ((30, 90), (45, 90), (15, 60), (60, 120)):
                lay = km.knurl_layout(radius, length, pitch, depth, helix, groove)
                self.assertFalse(lay["stepped"])
                ctx = "R%s d%s helix%s groove%s facets%s" % (radius, depth, helix, groove, lay["facets"])
                tip_at_end = math.hypot(lay["apex_radius"], lay["reach"] * math.tan(math.radians(lay["seg_helix"])))
                self.assertGreaterEqual(tip_at_end, radius + km.EXIT_CLEARANCE - 1e-9, ctx)
                design = depth * math.tan(math.radians(groove / 2)) / math.cos(math.radians(lay["helix"]))
                n = 320
                dz = 2 * lay["row_height"] / n
                prev = None
                for i in range(n + 1):
                    edges = groove_edges(lay, i * dz, radius)
                    for e in edges:
                        self.assertGreater(e, 0.85 * design, ctx)
                        self.assertLess(e, 1.25 * design + 0.02, ctx)
                    if prev:
                        for a, b in zip(edges, prev):
                            self.assertLess(abs(a - b), 0.004 + dz, ctx)   # no jump: a step would be 0.1-0.5
                    prev = edges

    def test_bore_segments_stop_at_their_rows(self):
        # In a bore a straight prism digs deeper as it runs on, so it must stop
        # at its row; the steps where rows meet stay within facet_error.
        for radius, length, pitch, depth in CASES:
            for helix in (10, 30, 45, 60):
                for fine in (False, True):
                    lay = km.knurl_layout(radius, length, pitch, depth, helix, 90, bore=True, fine=fine)
                    self.assertTrue(lay["stepped"])
                    self.assertAlmostEqual(lay["reach"], lay["row_height"] / 2, 9)
                    half_span = lay["row_height"] / 2 * math.tan(math.radians(lay["seg_helix"]))
                    for start in (lay["apex_radius"], radius):
                        gap = math.radians(lay["step"]) - 2 * math.atan2(half_span, start)
                        self.assertLessEqual(abs(gap) * start, lay["facet_error"] + 1e-9)
                    tol = (km.FACET_TOL_FINE if fine else km.FACET_TOL) * depth
                    if lay["facets"] < km.MAX_FACETS:
                        self.assertLessEqual(lay["facet_error"], tol + 1e-9)

    def test_fine_is_smoother(self):
        coarse = km.knurl_layout(12.7, 250.0, 5.0, 1.0, 30, 90)
        fine = km.knurl_layout(12.7, 250.0, 5.0, 1.0, 30, 90, fine=True)
        self.assertGreater(fine["facets"], coarse["facets"])
        self.assertLess(fine["facet_error"], coarse["facet_error"] / 2)

    def test_v_is_the_groove_angle_square_to_the_segment(self):
        # Angle between a wall and the segment's own centre plane = half the groove angle.
        for helix in (0, 10, 30, 45, 60):
            for groove in (30, 60, 90, 120, 150):
                lay = km.knurl_layout(10.0, 25.0, 2.0, 0.5, helix, groove)
                centre_plane = pm.unit(pm.cross(seg_dir(lay), R_HAT))
                cos_between = abs(pm.dot(wall_normal(lay), centre_plane))
                self.assertAlmostEqual(math.degrees(math.acos(cos_between)), groove / 2, 9)

    def test_overhang_matches_the_wall(self):
        # Cylinder standing along print up: a wall's overhang from vertical is
        # asin of its normal's vertical component.
        for helix in (0, 10, 30, 45, 60):
            for groove in (30, 60, 90, 120, 150):
                lay = km.knurl_layout(10.0, 25.0, 2.0, 0.5, helix, groove)
                steepness = abs(pm.dot(wall_normal(lay), Z_HAT))
                self.assertAlmostEqual(lay["overhang"], math.degrees(math.asin(steepness)), 9)
        self.assertAlmostEqual(km.knurl_layout(10, 200, 2, 0.5, 30, 90)["overhang"], 20, 0)
        self.assertEqual(km.knurl_layout(10, 25, 2, 0.5, 0, 90)["overhang"], 0.0)  # straight: vertical walls

    def test_land(self):
        # Defaults leave a flat between grooves; too deep and they overlap.
        lay = km.knurl_layout(10.0, 25.0, 2.0, 0.5, 30, 90)
        width = 2 * 0.5 * math.tan(math.radians(45))                         # groove width at the surface
        spacing = lay["pitch_around"] * math.cos(math.radians(lay["helix"]))  # between grooves, square to them
        self.assertAlmostEqual(lay["land"], spacing - width, 9)
        self.assertGreater(lay["land"], 0.5)
        self.assertLess(km.knurl_layout(10.0, 25.0, 2.0, 1.2, 30, 90)["land"], 0)

    def test_straight(self):
        lay = km.knurl_layout(10.0, 25.0, 2.0, 0.5, 0, 90)
        self.assertEqual((lay["rows"], lay["row_height"], lay["sag"], lay["step"], lay["reach"]), (1, 25.0, 0.0, 0.0, 12.5))
        self.assertAlmostEqual(lay["apex_radius"], 9.5, 9)
        self.assertAlmostEqual(lay["slope"], 1.0, 9)


if __name__ == "__main__":
    unittest.main()
