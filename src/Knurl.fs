FeatureScript 3083;
import(path : "onshape/std/geometry.fs", version : "3083.0");

// =====================================================================
// Knurl — custom FeatureScript feature, separate from Printable Pattern
// (src/PatternMaker.fs). Paste this file into its own Feature Studio.
// What it does and how: docs/KNURL.md.
//
// Cuts V grooves that wrap around a cylindrical face: crossed helices
// (diamond), one helix, or straight grooves along the axis. Works on
// shafts and in bores.
//
// Each helical groove is made of straight V prisms with flat faces, one per
// row along the axis, which keeps the cut fast. (The first draft swept
// every groove along a true helix; Onshape timed out on it.) On a shaft
// each prism runs on until it leaves the surface, so the groove is a union
// of overlapping prisms with no steps. (Prisms cut off at their rows left
// 0.3 mm steps: a V cut along a helix twists, a straight prism cannot.)
//
// knurlLayout and chordSag at the bottom mirror tools/knurl_math.py, where
// the numbers are tested (tools/test_knurl_math.py). Change both together.
//
// Pin the FeatureScript version above to whatever your Feature Studio
// header says if it differs.
//
// STATUS 2026-10-11: second draft (prisms cut off at their rows) ran in
// Onshape but left steps where segments met. This third draft (overlapping
// prisms on shafts) is not yet pasted.
// =====================================================================

// The first entry is the dialog default.
export enum KnurlStyle
{
    annotation { "Name" : "Diamond" }
    DIAMOND,
    annotation { "Name" : "Straight" }
    STRAIGHT,
    annotation { "Name" : "Helical (one direction)" }
    HELICAL
}

// Guards against a fine pitch on a big face locking up regeneration.
const MAX_SEGMENTS = 4000;
// The tools reach this far outside the face, so nothing is coplanar with it.
const SURFACE_CLEARANCE = 0.05 * millimeter;
// Segments that stop at their rows, and all segments at the two ends of
// the knurl, run this far past the row or the end, so nothing meets face
// to face. The two helix directions use different amounts so their end
// faces never share a plane.
const ROW_OVERLAP = [0.05 * millimeter, 0.08 * millimeter];
// A shaft segment runs until its tip is this far outside the surface.
const EXIT_CLEARANCE = 0.05 * millimeter;
// Cylinder axis within this of the print direction: overhangs are checked.
const ALIGNED_TOL = 5 * degree;
// Largest facet error allowed, as a fraction of the groove depth: normal,
// and with "Smoother grooves" on. MAX_FACETS caps the rows per pitch.
const FACET_TOL = 0.15;
const FACET_TOL_FINE = 0.05;
const MAX_FACETS = 6;

annotation { "Feature Type Name" : "Knurl",
        "Feature Type Description" : "Cuts a knurl (diamond, helical or straight V grooves) that wraps around cylindrical faces, on shafts or in bores.<br>Reports how far the groove walls overhang for the chosen print direction." }
export const knurl = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // The knurl covers the face's length, all the way around. Split a
        // face first to knurl only a band of it.
        annotation { "Name" : "Cylindrical faces",
                    "Filter" : EntityType.FACE && GeometryType.CYLINDER && BodyType.SOLID }
        definition.faces is Query;

        annotation { "Group Name" : "Knurl", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Style", "UIHint" : UIHint.SHOW_LABEL }
            definition.style is KnurlStyle;

            // Spacing of the grooves around the cylinder. Adjusted slightly
            // so a whole number of grooves fits.
            annotation { "Name" : "Groove pitch" }
            isLength(definition.pitch, { (millimeter) : [0.3, 2, 100] } as LengthBoundSpec);

            annotation { "Name" : "Groove depth" }
            isLength(definition.depth, { (millimeter) : [0.05, 0.5, 20] } as LengthBoundSpec);

            if (definition.style != KnurlStyle.STRAIGHT)
            {
                // Between the grooves and the cylinder's axis. Adjusted
                // slightly so a whole number of rows fits the length.
                annotation { "Name" : "Helix angle" }
                isAngle(definition.helixAngle, { (degree) : [5, 30, 60] } as AngleBoundSpec);

                // More, shorter rows: closer to a true helix, slower.
                annotation { "Name" : "Smoother grooves (slower)" }
                definition.fine is boolean;
            }
            if (definition.style == KnurlStyle.HELICAL)
            {
                annotation { "Name" : "Opposite hand" }
                definition.oppositeHand is boolean;
            }

            // Included angle of the V, measured square to the groove.
            annotation { "Name" : "Groove angle" }
            isAngle(definition.grooveAngle, { (degree) : [30, 90, 150] } as AngleBoundSpec);

            // Plain band left at each end of the face. 0 runs the grooves
            // off both ends.
            annotation { "Name" : "End margin" }
            isLength(definition.endMargin, { (millimeter) : [0, 0, 1000] } as LengthBoundSpec);
        }

        annotation { "Group Name" : "Printing check", "Collapsed By Default" : true }
        {
            // The build plate's normal (printer +Z). A plane or planar face
            // uses its normal, an edge its direction, a mate connector its Z.
            // Empty = world +Z. Only used for the overhang report.
            annotation { "Name" : "Print direction",
                        "Filter" : (EntityType.FACE && GeometryType.PLANE) || (EntityType.EDGE && GeometryType.LINE) || BodyType.MATE_CONNECTOR,
                        "MaxNumberOfPicks" : 1 }
            definition.printDirection is Query;

            // Measured from vertical, as slicers do.
            annotation { "Name" : "Max overhang angle" }
            isAngle(definition.maxOverhang, { (degree) : [5, 45, 85] } as AngleBoundSpec);
        }

        // Empty: the parts the faces belong to.
        annotation { "Name" : "Parts to cut", "Filter" : EntityType.BODY && BodyType.SOLID }
        definition.targets is Query;
    }
    {
        const faces = evaluateQuery(context, definition.faces);
        if (size(faces) == 0)
        {
            throw regenError("Select at least one cylindrical face.", ["faces"]);
        }
        var targets = definition.targets;
        if (isQueryEmpty(context, targets))
        {
            targets = qBodyType(qOwnerBody(definition.faces), BodyType.SOLID);
        }
        const printUp = resolvePrintUp(context, definition.printDirection);

        var tools = [];
        var notes = [];
        var warnings = [];
        for (var k = 0; k < size(faces); k += 1)
        {
            const res = knurlOneFace(context, id + ("face" ~ k), faces[k], printUp.direction, definition);
            tools = append(tools, res.tools);
            const prefix = size(faces) > 1 ? "Face " ~ (k + 1) ~ ": " : "";
            notes = append(notes, prefix ~ res.label);
            for (var w in res.warnings)
            {
                warnings = append(warnings, prefix ~ w);
            }
        }

        opBoolean(context, id + "cut", {
                    "tools" : qUnion(tools),
                    "targets" : targets,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });
        opDeleteBodies(context, id + "deleteSketches", {
                    "entities" : qSketchFilter(qCreatedBy(id, EntityType.BODY), SketchObject.YES)
                });

        // One report call only: Onshape shows just the last one per feature.
        var text = "";
        for (var n in notes)
        {
            text = text ~ n ~ ".  ";
        }
        text = text ~ "Print up: " ~ printUp.source ~ ".";
        if (size(warnings) > 0)
        {
            for (var w in warnings)
            {
                text = text ~ "  WARNING: " ~ w ~ ".";
            }
            reportFeatureWarning(context, id, text);
        }
        else
        {
            reportFeatureInfo(context, id, text);
        }
    });

// Print-up direction from a mate connector (its Z), a plane or planar face
// (its normal) or a straight edge; world +Z when nothing is selected.
function resolvePrintUp(context is Context, q is Query) returns map
{
    if (isQueryEmpty(context, q))
    {
        return { "direction" : vector(0, 0, 1), "source" : "world +Z (nothing selected)" };
    }
    var dir = undefined;
    var source = "";
    try silent
    {
        dir = evMateConnector(context, { "mateConnector" : q }).zAxis;
        source = "mate connector Z";
    }
    if (dir == undefined)
    {
        try silent
        {
            dir = evPlane(context, { "face" : q }).normal;
            source = "plane normal";
        }
    }
    if (dir == undefined)
    {
        try silent
        {
            dir = evAxis(context, { "axis" : q }).direction;
            source = "edge direction";
        }
    }
    if (dir == undefined)
    {
        throw regenError("Could not read a direction from the selection.", ["printDirection"]);
    }
    return { "direction" : normalize(dir), "source" : source };
}

// ---------------------------------------------------------------------
// One cylindrical face. The face's length is divided into rows. One V is
// sketched square to the axis at the middle of the first row and extruded
// along a straight line that lies along the helix there: a prism with flat
// faces. Because a groove moves one whole pitch around every `facets`
// rows, every row is the row below it turned by a fixed step, so the whole
// knurl is that prism (per helix direction) copied around the axis and up
// the rows. Prisms near the two ends of the knurl are cut short there, so
// rows that need the same length share one seed.
// ---------------------------------------------------------------------
function knurlOneFace(context is Context, fid is Id, face is Query, up is Vector, definition is map) returns map
{
    const cyl = evSurfaceDefinition(context, { "face" : face });
    if (!(cyl is Cylinder))
    {
        throw regenError("Knurl needs cylindrical faces.", ["faces"]);
    }
    const cs = cyl.coordSystem;
    const R = cyl.radius;
    const depth = definition.depth;

    // Shaft (material inside the cylinder) or bore (material outside).
    var shaft = true;
    try silent
    {
        const tp = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) });
        const fromAxis = tp.origin - cs.origin;
        shaft = dot(tp.normal, fromAxis - cs.zAxis * dot(fromAxis, cs.zAxis)) > 0 * meter;
    }
    if (shaft && depth >= 0.8 * R)
    {
        throw regenError("The groove depth is too large for this diameter.", ["depth"]);
    }
    const out = shaft ? 1 : -1; // from the groove's tip towards the open side

    // The face's extent along its axis, less the end margins.
    const bounds = evBox3d(context, { "topology" : face, "cSys" : cs, "tight" : true });
    const z0 = bounds.minCorner[2] + definition.endMargin;
    const z1 = bounds.maxCorner[2] - definition.endMargin;
    if (z1 - z0 < 0.1 * millimeter)
    {
        throw regenError("The end margin leaves no room for the knurl.", ["endMargin"]);
    }

    const straight = definition.style == KnurlStyle.STRAIGHT;
    const layout = knurlLayout(R, z1 - z0, definition.pitch, depth,
        straight ? 0 * degree : definition.helixAngle, definition.grooveAngle, !shaft, definition.fine == true);

    // One entry per helix direction: +1 turns with the axis, -1 against it.
    var hands = [1, -1];
    if (straight)
    {
        hands = [1];
    }
    else if (definition.style == KnurlStyle.HELICAL)
    {
        hands = [definition.oppositeHand == true ? -1 : 1];
    }
    const segments = size(hands) * layout.count * layout.rows;
    if (segments > MAX_SEGMENTS)
    {
        throw regenError("That needs " ~ segments ~ " groove segments (limit " ~ MAX_SEGMENTS ~ "). Increase the groove pitch, or split the face and knurl a shorter band.", ["pitch"]);
    }

    // V profile on a plane square to the axis, at the middle of the first
    // row: sketch origin at the groove's tip, x radially outward, y around
    // the cylinder. The base sits a little outside the face.
    const zSeed = z0 + layout.rowHeight / 2;
    const tip = cs.origin + cs.xAxis * layout.apexRadius + cs.zAxis * zSeed;
    const around = cross(cs.zAxis, cs.xAxis);
    const height = depth + layout.sag + 0.3 * depth + 2 * SURFACE_CLEARANCE;
    const half = height * layout.slope;
    const sketchId = fid + "profile";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(tip, cs.zAxis, cs.xAxis) });
    sketchClosedPolygon(sketch, "v", [
                vector(0 * meter, 0 * meter),
                vector(out * height, -half),
                vector(out * height, half)
            ]);
    skSolve(sketch);

    const axis = line(cs.origin, cs.zAxis);
    var tools = [];
    for (var h = 0; h < size(hands); h += 1)
    {
        // How far each row's segment runs below and above the middle of its
        // row, along the axis: the full reach, or less where that would
        // pass an end of the knurl. Rows with the same pair share a seed.
        const reach = layout.reach + ROW_OVERLAP[h];
        var groups = [];
        for (var j = 0; j < layout.rows; j += 1)
        {
            const below = min(reach, (j + 0.5) * layout.rowHeight + ROW_OVERLAP[h]);
            const above = min(reach, (layout.rows - j - 0.5) * layout.rowHeight + ROW_OVERLAP[h]);
            var found = false;
            for (var g = 0; g < size(groups); g += 1)
            {
                if (abs(groups[g].below - below) < 1e-9 * meter && abs(groups[g].above - above) < 1e-9 * meter)
                {
                    groups[g].rows = append(groups[g].rows, j);
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                groups = append(groups, { "below" : below, "above" : above, "rows" : [j] });
            }
        }

        const direction = cs.zAxis * cos(layout.segHelix) + around * (hands[h] * sin(layout.segHelix));
        for (var g = 0; g < size(groups); g += 1)
        {
            // The seed, at the first row's position: extruded both ways
            // along the helix direction. It is only a template; every
            // segment, the first row's included, is a copy of it.
            const tag = "" ~ h ~ "_" ~ g;
            const seedId = fid + ("seed" ~ tag);
            opExtrude(context, seedId, {
                        "entities" : qSketchRegion(sketchId),
                        "direction" : direction,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : groups[g].above / cos(layout.segHelix),
                        "startBound" : BoundingType.BLIND,
                        "startDepth" : groups[g].below / cos(layout.segHelix)
                    });
            const seed = qBodyType(qCreatedBy(seedId, EntityType.BODY), BodyType.SOLID);

            // Copies: `count` around the axis in each row; each row is
            // lifted to its height and turned by `step` from the one below.
            var transforms = [];
            var names = [];
            for (var j in groups[g].rows)
            {
                const lift = transform(cs.zAxis * (j * layout.rowHeight));
                for (var k = 0; k < layout.count; k += 1)
                {
                    transforms = append(transforms, rotationAround(axis, k * (360 / layout.count) * degree + (hands[h] * j) * layout.step) * lift);
                    names = append(names, "r" ~ j ~ "_" ~ k);
                }
            }
            const copiesId = fid + ("copies" ~ tag);
            opPattern(context, copiesId, {
                        "entities" : seed,
                        "transforms" : transforms,
                        "instanceNames" : names
                    });
            tools = append(tools, qBodyType(qCreatedBy(copiesId, EntityType.BODY), BodyType.SOLID));
            opDeleteBodies(context, fid + ("deleteSeed" ~ tag), { "entities" : seed });
        }
    }

    // Report.
    var warnings = [];
    const pitchText = roundToPrecision(layout.pitchAround / millimeter, 2) ~ " mm pitch";
    const n = "" ~ layout.count;
    var label = n ~ " straight grooves, " ~ pitchText;
    if (!straight)
    {
        const angleText = " at " ~ roundToPrecision(layout.helix / degree, 1) ~ " deg, ";
        label = (size(hands) == 2 ? n ~ " + " ~ n ~ " crossed grooves" : n ~ " helical grooves") ~ angleText ~ pitchText;
    }
    if (layout.land < 0 * meter)
    {
        warnings = append(warnings, "the grooves are wider than the gaps between them, so the peaks are cut below the surface; reduce the depth or increase the pitch");
    }
    else
    {
        label = label ~ ", " ~ roundToPrecision(layout.land / millimeter, 2) ~ " mm lands";
    }
    if (!straight)
    {
        label = label ~ "; " ~ segments ~ " straight segments, "
            ~ (layout.stepped ? "steps up to " : "depth ripple ") ~ roundToPrecision(layout.facetError / millimeter, 2) ~ " mm";
    }
    const tilt = acos(min(1, abs(dot(cs.zAxis, up))));
    if (tilt > ALIGNED_TOL)
    {
        label = label ~ "; axis " ~ roundToPrecision(tilt / degree, 0) ~ " deg off print up, overhang not checked";
    }
    else if (!straight)
    {
        label = label ~ "; groove walls overhang " ~ roundToPrecision(layout.overhang / degree, 0) ~ " deg";
        if (layout.overhang > definition.maxOverhang + 0.5 * degree)
        {
            warnings = append(warnings, "groove walls overhang " ~ roundToPrecision(layout.overhang / degree, 0) ~ " deg, over the " ~ roundToPrecision(definition.maxOverhang / degree, 0) ~ " deg limit; use a wider groove angle or a smaller helix angle");
        }
    }
    return { "tools" : qUnion(tools), "label" : label, "warnings" : warnings };
}

function sketchClosedPolygon(sketch is Sketch, prefix is string, pts is array)
{
    const n = size(pts);
    for (var i = 0; i < n; i += 1)
    {
        skLineSegment(sketch, prefix ~ "_e" ~ i, { "start" : pts[i], "end" : pts[(i + 1) % n] });
    }
}

// =====================================================================
// Pure math — mirrors tools/knurl_math.py. No Context access below.
// =====================================================================

// Spread in radius along a straight segment whose ends are halfSpan either
// side of its middle, when it is placed to straddle radius rho: sag/2
// inside it at the middle, sag/2 outside it at the ends.
function chordSag(rho is ValueWithUnits, halfSpan is ValueWithUnits) returns ValueWithUnits
{
    return halfSpan * halfSpan / (2 * rho);
}

// Numbers for a knurl on a cylinder of this radius and length
// (knurl_math.knurl_layout).
//   count        grooves around, per direction (pitch rounded to fit)
//   pitchAround  the pitch actually used, measured around the cylinder
//   rows         slices along the axis, one straight segment of each
//                groove in each
//   rowHeight    height of a row (rows * rowHeight = length)
//   facets       rows a groove takes to move one pitch around
//   helix        helix angle actually used, at the surface (nudged so a
//                whole number of rows fits the length)
//   step         how far around the axis a groove moves per row
//   segHelix     angle between a segment and the axis: the helix angle at
//                mid-depth, where the segment is laid along the helix
//   apexRadius   radius of a segment's tip at the middle of its row
//   sag          how much further out the tip is at the row's ends
//   reach        how far a segment runs along the axis either side of the
//                middle of its row: until it leaves the surface (shaft),
//                or to the row's ends (bore, straight)
//   stepped      true when segments stop at their rows and so meet with a
//                step
//   facetError   depth ripple along the groove (sag), or for stepped
//                segments the largest of that and the steps where they
//                meet
//   slope        half-width of the V around the cylinder per unit of
//                depth, on a section square to the axis
//   land         flat left between neighbouring grooves, measured square
//                to them (negative: the grooves overlap)
//   overhang     of the groove walls, from vertical, when the cylinder
//                stands along the print direction
function knurlLayout(radius is ValueWithUnits, length is ValueWithUnits, pitch is ValueWithUnits,
    depth is ValueWithUnits, helixAngle is ValueWithUnits, grooveAngle is ValueWithUnits,
    bore is boolean, fine is boolean) returns map
{
    const around = 2 * PI * radius;
    const count = max(3, round(around / pitch));
    const pitchAround = around / count;
    const alpha = grooveAngle / 2;
    const rho = bore ? radius + depth : radius - depth; // groove bottom
    const rhoMid = bore ? radius + depth / 2 : radius - depth / 2;

    if (helixAngle <= 1e-6 * degree)
    {
        return {
                "count" : count, "pitchAround" : pitchAround,
                "rows" : 1, "rowHeight" : length, "facets" : 1,
                "helix" : 0 * degree, "step" : 0 * degree, "segHelix" : 0 * degree,
                "apexRadius" : rho, "sag" : 0 * meter, "reach" : length / 2,
                "stepped" : false, "facetError" : 0 * meter,
                "slope" : tan(alpha),
                "land" : pitchAround - 2 * depth * tan(alpha),
                "overhang" : 0 * degree
            };
    }

    const advance = pitchAround / tan(helixAngle); // along the axis per pitch around
    const tol = (fine ? FACET_TOL_FINE : FACET_TOL) * depth;
    var facets = 0;
    var rows = 1;
    var rowHeight = length;
    var tanHelix = 0;
    var phi = 0; // half the angle a groove turns per row, in radians
    var sag = 0 * meter;
    var apex = rho;
    var facetError = 0 * meter;
    while (true)
    {
        facets += 1;
        rows = max(1, round(length * facets / advance));
        rowHeight = length / rows;
        tanHelix = pitchAround / (facets * rowHeight);
        phi = rowHeight * tanHelix / (2 * radius);
        const halfSpan = phi * rhoMid; // of a row's part of a segment, around
        sag = chordSag(rho, halfSpan);
        apex = rho - sag / 2;
        facetError = sag;
        if (bore)
        {
            // Segments that stop at their rows meet at an angle phi from
            // each one's middle on the true helix; a straight one gets
            // there slightly early at the tip and late at the surface, or
            // the reverse.
            const missTip = 2 * apex * abs(atan(halfSpan / apex) / radian - phi);
            const missSurface = 2 * radius * abs(atan(halfSpan / radius) / radian - phi);
            facetError = max(sag, max(missTip, missSurface));
        }
        if (facetError <= tol || facets >= MAX_FACETS)
        {
            break;
        }
    }
    const segHelix = atan(tanHelix * rhoMid / radius);
    var reach = rowHeight / 2;
    if (!bore)
    {
        const exitRadius = radius + EXIT_CLEARANCE;
        reach = max(reach, sqrt(exitRadius * exitRadius - apex * apex) / tan(segHelix));
    }
    return {
            "count" : count, "pitchAround" : pitchAround,
            "rows" : rows, "rowHeight" : rowHeight, "facets" : facets,
            "helix" : atan(tanHelix),
            "step" : 2 * phi * radian,
            "segHelix" : segHelix,
            "apexRadius" : apex, "sag" : sag, "reach" : reach,
            "stepped" : bore, "facetError" : facetError,
            "slope" : tan(alpha) / cos(segHelix),
            "land" : pitchAround * cos(atan(tanHelix)) - 2 * depth * tan(alpha),
            "overhang" : asin(cos(alpha) * sin(segHelix))
        };
}
