"""Pure-math twin of src/Knurl.fs (knurlLayout, chordSag).

Plain numbers with no Onshape context, so the knurl geometry can be checked
locally (see test_knurl_math.py). Change this and the .fs functions together.

Units: mm and degrees.

Conventions: the cylinder's axis is z. A groove runs along the surface at
`helix_deg` from the axis (0 = straight, along the axis). The V is
`groove_deg` wide (included angle) measured square to the groove.

A helical groove is built from straight V prisms with flat faces, one per
"row" (a slice of the cylinder square to its axis), each lying along the
helix at the middle of its row. Flat faces keep the cut fast: the first
draft swept each groove along a true helix and Onshape timed out.

On a shaft each prism is made long enough to run out of the surface at both
ends, so the groove is the union of overlapping prisms and has no steps.
(Prisms cut off at the row boundaries left 0.3 mm steps, because a V cut
along a helix twists and a straight prism cannot.) In a bore a straight
prism digs deeper the further it runs, so there the prisms stop at their
rows and small steps remain.
"""

import math

FACET_TOL = 0.15       # largest facet error allowed, as a fraction of the groove depth
FACET_TOL_FINE = 0.05  # the same with "smoother grooves" on
MAX_FACETS = 6         # most rows a groove may take to move one pitch around
EXIT_CLEARANCE = 0.05  # a shaft prism runs until its tip is this far outside the surface


def chord_sag(rho, half_span):
    """Spread in radius along a straight segment whose ends are half_span
    either side of its middle, when it is placed to straddle radius rho:
    sag/2 inside it at the middle, sag/2 outside it at the ends."""
    return half_span * half_span / (2 * rho)


def knurl_layout(radius, length, pitch, depth, helix_deg, groove_deg, bore=False, fine=False):
    """Numbers for a knurl on a cylinder of this radius and length.

    count         grooves around, per direction (pitch rounded to fit)
    pitch_around  the pitch actually used, measured around the cylinder
    rows          slices along the axis, one straight segment of each groove in each
    row_height    height of a row (rows * row_height = length)
    facets        rows a groove takes to move one pitch around
    helix         helix angle actually used, at the surface (nudged so that a
                  whole number of rows fits the length)
    step          how far around the axis a groove moves per row (degrees)
    seg_helix     angle between a segment and the axis: the helix angle at
                  mid-depth, where the segment is laid along the helix
    apex_radius   radius of a segment's tip at the middle of its row
    sag           how much further out the tip is at the row's ends
    reach         how far a segment runs along the axis either side of the
                  middle of its row: until it leaves the surface (shaft), or
                  to the row's ends (bore, straight)
    stepped       True when segments stop at their rows and so meet with a step
    facet_error   depth ripple along the groove (sag), or for stepped
                  segments the largest of that and the steps where they meet
    slope         half-width of the V around the cylinder per unit of depth,
                  on a section square to the axis
    land          flat left between neighbouring grooves, measured square to
                  them (negative: the grooves overlap)
    overhang      of the groove walls, degrees from vertical, when the
                  cylinder stands along the print direction
    """
    around = 2 * math.pi * radius
    count = max(3, round(around / pitch))
    pitch_around = around / count
    alpha = math.radians(groove_deg) / 2
    rho = radius + depth if bore else radius - depth          # groove bottom
    rho_mid = radius + depth / 2 if bore else radius - depth / 2

    if helix_deg <= 1e-6:
        return {
            "count": count, "pitch_around": pitch_around,
            "rows": 1, "row_height": length, "facets": 1,
            "helix": 0.0, "step": 0.0, "seg_helix": 0.0,
            "apex_radius": rho, "sag": 0.0, "reach": length / 2,
            "stepped": False, "facet_error": 0.0,
            "slope": math.tan(alpha),
            "land": pitch_around - 2 * depth * math.tan(alpha),
            "overhang": 0.0,
        }

    advance = pitch_around / math.tan(math.radians(helix_deg))  # along the axis per pitch around
    tol = (FACET_TOL_FINE if fine else FACET_TOL) * depth
    facets = 0
    while True:
        facets += 1
        rows = max(1, round(length * facets / advance))
        row_height = length / rows
        tan_helix = pitch_around / (facets * row_height)
        phi = row_height * tan_helix / (2 * radius)            # half the angle a groove turns per row
        half_span = phi * rho_mid                              # of a row's part of a segment, around
        sag = chord_sag(rho, half_span)
        apex = rho - sag / 2
        facet_error = sag
        if bore:
            # Segments that stop at their rows meet at an angle phi from each
            # one's middle on the true helix; a straight one gets there
            # slightly early at the tip and late at the surface, or the reverse.
            miss_tip = 2 * apex * abs(math.atan(half_span / apex) - phi)
            miss_surface = 2 * radius * abs(math.atan(half_span / radius) - phi)
            facet_error = max(sag, miss_tip, miss_surface)
        if facet_error <= tol or facets >= MAX_FACETS:
            break
    seg_helix = math.atan(tan_helix * rho_mid / radius)
    reach = row_height / 2
    if not bore:
        exit_radius = radius + EXIT_CLEARANCE
        reach = max(reach, math.sqrt(exit_radius * exit_radius - apex * apex) / math.tan(seg_helix))
    return {
        "count": count, "pitch_around": pitch_around,
        "rows": rows, "row_height": row_height, "facets": facets,
        "helix": math.degrees(math.atan(tan_helix)),
        "step": math.degrees(2 * phi),
        "seg_helix": math.degrees(seg_helix),
        "apex_radius": apex, "sag": sag, "reach": reach,
        "stepped": bore, "facet_error": facet_error,
        "slope": math.tan(alpha) / math.cos(seg_helix),
        "land": pitch_around * math.cos(math.atan(tan_helix)) - 2 * depth * math.tan(alpha),
        "overhang": math.degrees(math.asin(math.cos(alpha) * math.sin(seg_helix))),
    }
