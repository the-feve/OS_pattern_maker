FeatureScript 3083;
import(path : "onshape/std/geometry.fs", version : "3083.0");

// =====================================================================
// Printable Pattern — single custom FeatureScript feature
// Spec: docs/SPEC.md. Design and the overhang math: docs/DESIGN.md.
//
// Cuts a grid of holes (circles, hexagons or triangles) into faces or
// sketch regions, shaped so every hole prints without supports for the
// chosen print direction (max overhang angle + max bridge width). Curved
// faces are cut along a chosen direction.
//
// The pure-math functions below (patternFrame, circumscribed, buildCellSet,
// roofClip, latticeCandidates, ...) mirror tools/pattern_math.py one for
// one. That Python twin is where the math is tested
// (tools/test_pattern_math.py: overhang, rib width and edge margin over a
// 540-case matrix). Change both together.
//
// Pin the FeatureScript version above to whatever your Feature Studio
// header says if it differs.
//
// STATUS 2026-10-11: compiles and cuts planar faces in Onshape (fourth
// paste, Tip angle seen). Regular point-up hexagons as the default are new
// and not yet pasted. Curved faces: through-all and blind pockets not yet
// reported. Validation matrix: HANDOFF.md section 3.
// =====================================================================

// The first entry of each enum is the dialog default.
export enum HolePattern
{
    annotation { "Name" : "Circles" }
    CIRCLES,
    annotation { "Name" : "Hexagons" }
    HEXAGONS,
    annotation { "Name" : "Triangles" }
    TRIANGLES
}

export enum CircleStyle
{
    annotation { "Name" : "Auto (circle, teardrop if needed)" }
    AUTO,
    annotation { "Name" : "Circle" }
    CIRCLE,
    annotation { "Name" : "Teardrop (pointed)" }
    TEARDROP,
    annotation { "Name" : "Teardrop (flat top = max bridge)" }
    FLAT_TEARDROP
}

export enum HexStyle
{
    annotation { "Name" : "Regular, point up" }
    AUTO,
    annotation { "Name" : "Regular, flat top (bridged)" }
    FLAT_TOP,
    annotation { "Name" : "Point up, stretched to print" }
    POINTY
}

export enum TriangleStyle
{
    annotation { "Name" : "Auto" }
    AUTO,
    annotation { "Name" : "Full down triangles" }
    FULL,
    annotation { "Name" : "Peaked down triangles" }
    PEAKED,
    annotation { "Name" : "Up triangles only" }
    OMIT
}

export enum PatternEnd
{
    annotation { "Name" : "Through all" }
    THROUGH_ALL,
    annotation { "Name" : "Blind" }
    BLIND
}

// Hole axis within this of horizontal: flat ceilings are true bridges.
const BRIDGE_TOL = 1 * degree;
// Truncated/flat features narrower than this are not worth printing.
const MIN_FEATURE = 0.3 * millimeter;
// Guard against a tiny cell on a huge face locking up regeneration.
const MAX_CANDIDATES = 4000;
// The tool body starts this far outside the selected face, so its end
// cap is never coplanar with the face it cuts.
const START_CLEARANCE = 0.05 * millimeter;

annotation { "Feature Type Name" : "Printable Pattern",
        "Feature Type Description" : "Cuts a support-free hole pattern (circles, hexagons or triangles) into faces or sketch regions (curved faces along a chosen cut direction).<br>Holes are shaped from the print direction, max overhang angle and max bridge width so they print without supports." }
export const printablePattern = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // Curved faces need a cut direction (Cut group).
        annotation { "Name" : "Faces or sketch regions",
                    "Filter" : (EntityType.FACE && BodyType.SOLID) || (EntityType.FACE && SketchObject.YES && ConstructionObject.NO) }
        definition.regions is Query;

        annotation { "Group Name" : "Pattern", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Pattern", "UIHint" : UIHint.SHOW_LABEL }
            definition.patternType is HolePattern;

            if (definition.patternType == HolePattern.CIRCLES)
            {
                annotation { "Name" : "Hole shape", "UIHint" : UIHint.SHOW_LABEL }
                definition.circleStyle is CircleStyle;
            }
            if (definition.patternType == HolePattern.HEXAGONS)
            {
                annotation { "Name" : "Hexagon style", "UIHint" : UIHint.SHOW_LABEL }
                definition.hexStyle is HexStyle;

                if (definition.hexStyle != HexStyle.FLAT_TOP)
                {
                    // Turns the upper corner away from "up". The regular
                    // hexagon just turns; the stretched style also steepens
                    // its roofs by the same angle so they still print.
                    annotation { "Name" : "Tip angle" }
                    isAngle(definition.hexTipAngle, { (degree) : [-30, 0, 30] } as AngleBoundSpec);
                }
            }
            if (definition.patternType == HolePattern.TRIANGLES)
            {
                annotation { "Name" : "Down-pointing triangles", "UIHint" : UIHint.SHOW_LABEL }
                definition.triangleStyle is TriangleStyle;
            }

            // Every shape is sized by the circle it is drawn around, so the
            // same number gives similar-looking cells for every pattern.
            annotation { "Name" : "Cell size (inscribed diameter)" }
            isLength(definition.cellSize, { (millimeter) : [0.5, 5, 500] } as LengthBoundSpec);

            annotation { "Name" : "Rib width" }
            isLength(definition.ribWidth, { (millimeter) : [0.2, 1.6, 100] } as LengthBoundSpec);

            // Only whole cells are cut; each keeps at least this much solid
            // to the edge of the face or region.
            annotation { "Name" : "Edge margin" }
            isLength(definition.edgeMargin, { (millimeter) : [0, 2, 100] } as LengthBoundSpec);
        }

        annotation { "Group Name" : "Printing", "Collapsed By Default" : false }
        {
            // The build plate's normal (printer +Z). A plane or planar face
            // uses its normal, an edge its direction, a mate connector its Z.
            // Empty = world +Z.
            annotation { "Name" : "Print direction",
                        "Filter" : (EntityType.FACE && GeometryType.PLANE) || (EntityType.EDGE && GeometryType.LINE) || BodyType.MATE_CONNECTOR,
                        "MaxNumberOfPicks" : 1 }
            definition.printDirection is Query;

            annotation { "Name" : "Flip print direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.flipPrint is boolean;

            // Measured from vertical, as slicers do.
            annotation { "Name" : "Max overhang angle" }
            isAngle(definition.maxOverhang, { (degree) : [5, 45, 85] } as AngleBoundSpec);

            annotation { "Name" : "Max bridge width" }
            isLength(definition.maxBridge, { (millimeter) : [0, 5, 100] } as LengthBoundSpec);

            // Only used when every orientation prints (holes close to the
            // print axis); otherwise the pattern is aligned to "up".
            annotation { "Name" : "Pattern angle (if orientation is free)" }
            isAngle(definition.patternAngle, { (degree) : [-180, 0, 180] } as AngleBoundSpec);
        }

        annotation { "Group Name" : "Cut", "Collapsed By Default" : false }
        {
            // The hole axis, read the same way as the print direction.
            // Needed for curved faces. Empty = each planar face's own normal.
            annotation { "Name" : "Cut direction",
                        "Filter" : (EntityType.FACE && GeometryType.PLANE) || (EntityType.EDGE && GeometryType.LINE) || BodyType.MATE_CONNECTOR,
                        "MaxNumberOfPicks" : 1 }
            definition.cutDirection is Query;

            annotation { "Name" : "End type", "UIHint" : UIHint.SHOW_LABEL }
            definition.endType is PatternEnd;

            if (definition.endType == PatternEnd.BLIND)
            {
                annotation { "Name" : "Depth" }
                isLength(definition.depth, { (millimeter) : [0.01, 5, 10000] } as LengthBoundSpec);
            }

            // Default cuts against the region's normal (into the part for a
            // face, or for a sketch drawn on a part's face).
            annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
            definition.oppositeDirection is boolean;

            annotation { "Name" : "Both sides" }
            definition.bothSides is boolean;

            // Empty: a face's own part; for sketch regions, every part.
            annotation { "Name" : "Parts to cut", "Filter" : EntityType.BODY && BodyType.SOLID }
            definition.targets is Query;

            // Shows the candidate cells (kept or not) as a sketch.
            annotation { "Name" : "Keep pattern sketch" }
            definition.keepSketch is boolean;
        }
    }
    {
        const regionFaces = evaluateQuery(context, definition.regions);
        if (size(regionFaces) == 0)
        {
            throw regenError("Select at least one face or sketch region.", ["regions"]);
        }
        const printUp = resolvePrintUp(context, definition.printDirection, definition.flipPrint);
        const cutAxis = readDirection(context, definition.cutDirection, "cutDirection");
        const targets = resolveTargets(context, definition.targets, regionFaces);
        if (isQueryEmpty(context, targets))
        {
            throw regenError("No parts to cut. Select them under Parts to cut.", ["targets"]);
        }

        var throughLength = 0 * meter;
        if (definition.endType == PatternEnd.THROUGH_ALL)
        {
            const allBounds = evBox3d(context, { "topology" : qUnion([targets, definition.regions]), "tight" : false });
            throughLength = norm(allBounds.maxCorner - allBounds.minCorner) + 1 * millimeter;
        }

        var tools = [];
        var total = 0;
        var notes = [];
        var warnings = [];
        for (var k = 0; k < size(regionFaces); k += 1)
        {
            const res = patternOneRegion(context, id + ("region" ~ k), regionFaces[k], printUp.direction,
                cutAxis == undefined ? undefined : cutAxis.direction, throughLength, definition);
            total += res.count;
            if (res.count > 0)
            {
                tools = append(tools, res.tools);
            }
            const prefix = size(regionFaces) > 1 ? "Region " ~ (k + 1) ~ ": " : "";
            notes = append(notes, prefix ~ res.count ~ " " ~ res.label ~ " (" ~ res.orientation ~ ")");
            for (var w in res.warnings)
            {
                warnings = append(warnings, prefix ~ w);
            }
        }
        if (total == 0)
        {
            throw regenError("No whole cell fits inside the selection. Reduce cell size, rib width or edge margin.", ["regions", "cellSize"]);
        }

        opBoolean(context, id + "cut", {
                    "tools" : qUnion(tools),
                    "targets" : targets,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });

        if (!definition.keepSketch)
        {
            opDeleteBodies(context, id + "deleteSketches", {
                        "entities" : qSketchFilter(qCreatedBy(id, EntityType.BODY), SketchObject.YES)
                    });
        }

        // One report call only: Onshape shows just the last one per feature
        // (OS_FeatureScript_Reference notes, Rocket_Builder 2026-10-07).
        var text = "Print up: " ~ printUp.source ~ ".";
        if (cutAxis != undefined)
        {
            text = text ~ "  Cut along: " ~ cutAxis.source ~ ".";
        }
        for (var n in notes)
        {
            text = text ~ "  " ~ n ~ ".";
        }
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

// ---------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------

// Direction from a mate connector (its Z), a plane or planar face (its
// normal) or a straight edge. Undefined when nothing is selected.
function readDirection(context is Context, q is Query, parameterId is string)
{
    if (isQueryEmpty(context, q))
    {
        return undefined;
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
        throw regenError("Could not read a direction from the selection.", [parameterId]);
    }
    return { "direction" : normalize(dir), "source" : source };
}

function resolvePrintUp(context is Context, q is Query, flip is boolean) returns map
{
    const picked = readDirection(context, q, "printDirection");
    var dir = vector(0, 0, 1);
    var source = "world +Z (nothing selected)";
    if (picked != undefined)
    {
        dir = picked.direction;
        source = picked.source;
    }
    if (flip)
    {
        dir = dir * -1;
        source = source ~ ", flipped";
    }
    return { "direction" : dir, "source" : source };
}

// Explicit selection wins. Otherwise a solid face cuts its own part and a
// sketch region cuts every modifiable part.
function resolveTargets(context is Context, selected is Query, regionFaces is array) returns Query
{
    if (!isQueryEmpty(context, selected))
    {
        return selected;
    }
    var qs = [];
    for (var f in regionFaces)
    {
        if (isQueryEmpty(context, qSketchFilter(f, SketchObject.YES)))
        {
            qs = append(qs, qOwnerBody(f));
        }
        else
        {
            qs = append(qs, qAllModifiableSolidBodies());
        }
    }
    return qBodyType(qUnion(qs), BodyType.SOLID);
}

// Hole axis and origin for one region. With no cut direction a planar
// region is cut along its own normal ("aligned": the cells are sketched on
// the face itself). A cut direction is turned to point out of the face, so
// the default cut still goes into the part; unless it matches a planar
// face's normal the region is handled by projection.
function regionSetup(context is Context, face is Query, axis) returns map
{
    var pl = undefined;
    try silent
    {
        pl = evPlane(context, { "face" : face });
    }
    if (pl == undefined && axis == undefined)
    {
        throw regenError("Curved faces need a cut direction. Select a plane, planar face or straight edge under Cut direction.", ["regions", "cutDirection"]);
    }
    if (pl != undefined)
    {
        const along = axis == undefined ? 1 : dot(axis, pl.normal);
        if (abs(along) > 1 - 1e-9)
        {
            return { "N" : pl.normal, "origin" : pl.origin, "planeX" : pl.x, "aligned" : true };
        }
        if (abs(along) < 1e-3)
        {
            throw regenError("The cut direction lies in a selected face. Pick a direction that crosses it.", ["regions", "cutDirection"]);
        }
        return { "N" : along < 0 ? axis * -1 : axis, "origin" : pl.origin, "planeX" : perpendicularTo(axis), "aligned" : false };
    }
    var N = axis;
    try silent
    {
        if (dot(axis, evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) }).normal) < 0)
        {
            N = axis * -1;
        }
    }
    return { "N" : N, "origin" : evApproximateCentroid(context, { "entities" : face }), "planeX" : perpendicularTo(axis), "aligned" : false };
}

function perpendicularTo(n is Vector) returns Vector
{
    return normalize(cross(n, abs(n[0]) < 0.9 ? vector(1, 0, 0) : vector(0, 1, 0)));
}

// ---------------------------------------------------------------------
// One region: frame -> cell set -> candidate cells -> keep whole cells
// -> tool bodies.
// ---------------------------------------------------------------------
function patternOneRegion(context is Context, rid is Id, face is Query, up is Vector, axis,
    throughLength is ValueWithUnits, definition is map) returns map
{
    const setup = regionSetup(context, face, axis);
    const frame = patternFrame(setup.N, up, definition.maxOverhang, definition.patternAngle, setup.planeX);
    const cellSet = buildCellSet(definition, frame);
    var warnings = cellSet.warnings;
    if (frame.angleIgnored)
    {
        warnings = append(warnings, "pattern angle ignored: the print direction fixes the orientation");
    }

    // Region bounds in the pattern frame (x = u, y = v = up, z = hole axis).
    const bounds = evBox3d(context, {
                "topology" : face,
                "cSys" : coordSystem(setup.origin, frame.u, frame.N),
                "tight" : true
            });
    const candidates = latticeCandidates(cellSet, bounds.minCorner[0], bounds.minCorner[1], bounds.maxCorner[0], bounds.maxCorner[1]);

    const cutDir = definition.oppositeDirection ? frame.N : frame.N * -1;
    warnings = concatenateArrays([warnings, pocketEndWarnings(definition, cutDir, up)]);

    var cut;
    if (setup.aligned)
    {
        cut = cutCellsOnFace(context, rid, face, setup.origin, frame, candidates, cutDir, throughLength, definition);
    }
    else
    {
        cut = cutCellsProjected(context, rid, face, setup.origin, frame, bounds.minCorner[2], bounds.maxCorner[2],
            candidates, cutDir, throughLength, definition);
    }
    return {
            "count" : cut.count,
            "tools" : cut.tools,
            "label" : cellSet.label,
            "orientation" : describeOrientation(frame),
            "warnings" : warnings
        };
}

// Planar region cut along its own normal: every candidate goes into one
// sketch on the face, and the whole cells are extruded.
function cutCellsOnFace(context is Context, rid is Id, face is Query, origin is Vector, frame is map,
    candidates is array, cutDir is Vector, throughLength is ValueWithUnits, definition is map) returns map
{
    // Sketch y axis = cross(N, u) = v, the same frame as the bounds.
    const sketchId = rid + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(origin, frame.N, frame.u) });
    for (var i = 0; i < size(candidates); i += 1)
    {
        sketchHole(sketch, "h" ~ i, candidates[i].center, candidates[i].shape);
    }
    skSolve(sketch);

    // Keep a cell only if it is whole: its centroid lies on the region and
    // its outline stays edgeMargin clear of every region edge (including
    // inner loops). A cell crossing an edge has distance 0.
    const edges = qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE);
    const need = max(definition.edgeMargin, 0.001 * millimeter) - 1e-6 * millimeter;
    var kept = [];
    for (var cell in evaluateQuery(context, qSketchRegion(sketchId)))
    {
        if (evDistance(context, { "side0" : cell, "side1" : edges }).distance < need)
        {
            continue;
        }
        const c = evApproximateCentroid(context, { "entities" : cell });
        if (evDistance(context, { "side0" : c, "side1" : face }).distance > 1e-5 * millimeter)
        {
            continue;
        }
        kept = append(kept, cell);
    }
    if (size(kept) == 0)
    {
        return { "count" : 0, "tools" : qNothing() };
    }

    var endDepth = throughLength;
    var startDepth = START_CLEARANCE;
    if (definition.endType == PatternEnd.BLIND)
    {
        endDepth = definition.depth;
    }
    if (definition.bothSides)
    {
        startDepth = endDepth;
    }
    opExtrude(context, rid + "extrude", {
                "entities" : qUnion(kept),
                "direction" : cutDir,
                "endBound" : BoundingType.BLIND,
                "endDepth" : endDepth,
                "startBound" : BoundingType.BLIND,
                "startDepth" : startDepth
            });
    return { "count" : size(kept), "tools" : qBodyType(qCreatedBy(rid + "extrude", EntityType.BODY), BodyType.SOLID) };
}

// Curved face, or a cut direction that is not the face's normal. The cells
// are laid out on a plane square to the hole axis, touching the face on the
// side the cut starts from, and judged by their shadow on the face:
//   1. rays along the axis from the centre and from points edgeMargin
//      outside the outline must all hit the face (this also keeps cells off
//      the silhouette of a face that curves away, e.g. a cylinder);
//   2. the extruded cell must stay edgeMargin clear of the face's edges
//      (a prism along the axis is as far from an edge as its shadow is).
// Blind depth then cuts the prisms down to pockets (pocketTools).
// zLo..zHi is the face's extent along the axis, measured from origin.
function cutCellsProjected(context is Context, rid is Id, face is Query, origin is Vector, frame is map,
    zLo is ValueWithUnits, zHi is ValueWithUnits, candidates is array, cutDir is Vector,
    throughLength is ValueWithUnits, definition is map) returns map
{
    const blind = definition.endType == PatternEnd.BLIND;
    var endDepth = throughLength;
    var startDepth = definition.bothSides ? throughLength : START_CLEARANCE;
    if (blind)
    {
        // Long enough to pass right through the skin pocketTools makes.
        endDepth = zHi - zLo + definition.depth + 1 * millimeter;
        startDepth = (definition.bothSides ? definition.depth : 0 * meter) + 1 * millimeter;
    }
    const margin = max(definition.edgeMargin, 0.001 * millimeter);
    const sketchOrigin = origin + frame.N * (definition.oppositeDirection ? zLo : zHi);
    const rayOrigin = origin + frame.N * (zHi + 1 * millimeter);
    const rayDir = frame.N * -1;

    const sketchId = rid + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(sketchOrigin, frame.N, frame.u) });
    var drawn = 0;
    for (var i = 0; i < size(candidates); i += 1)
    {
        var whole = true;
        for (var p in outlineSamples(candidates[i].shape, margin))
        {
            const q = candidates[i].center + p;
            const hits = evRaycast(context, {
                        "entities" : face,
                        "ray" : line(rayOrigin + frame.u * q[0] + frame.v * q[1], rayDir),
                        "closest" : true
                    });
            if (size(hits) == 0)
            {
                whole = false;
                break;
            }
        }
        if (whole)
        {
            sketchHole(sketch, "h" ~ i, candidates[i].center, candidates[i].shape);
            drawn += 1;
        }
    }
    skSolve(sketch);
    if (drawn == 0)
    {
        return { "count" : 0, "tools" : qNothing() };
    }

    const extrudeId = rid + "extrude";
    opExtrude(context, extrudeId, {
                "entities" : qSketchRegion(sketchId),
                "direction" : cutDir,
                "endBound" : BoundingType.BLIND,
                "endDepth" : endDepth,
                "startBound" : BoundingType.BLIND,
                "startDepth" : startDepth
            });
    const bodies = qBodyType(qCreatedBy(extrudeId, EntityType.BODY), BodyType.SOLID);

    const edges = qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE);
    const hasEdges = !isQueryEmpty(context, edges);
    var rejects = [];
    var count = 0;
    for (var body in evaluateQuery(context, bodies))
    {
        if (hasEdges && evDistance(context, { "side0" : body, "side1" : edges }).distance < margin - 1e-6 * millimeter)
        {
            rejects = append(rejects, body);
        }
        else
        {
            count += 1;
        }
    }
    if (size(rejects) > 0)
    {
        opDeleteBodies(context, rid + "deleteRejects", { "entities" : qUnion(rejects) });
    }
    if (blind && count > 0)
    {
        return { "count" : count, "tools" : pocketTools(context, rid, face, bodies, definition) };
    }
    return { "count" : count, "tools" : bodies };
}

// Blind depth for projected cells: pockets whose floor follows the face.
// The face is thickened along its normals into a skin as thick as the depth
// (twice). Skin A minus the prisms is the skin with holes; skin B minus that
// leaves just the pockets, which become the tools. Unlike extruding up to an
// offset surface, this cannot fail for a cell near the edge of the face.
function pocketTools(context is Context, rid is Id, face is Query, prisms is Query, definition is map) returns Query
{
    var inward = definition.depth;
    var outward = START_CLEARANCE;
    if (definition.oppositeDirection)
    {
        inward = START_CLEARANCE;
        outward = definition.depth;
    }
    if (definition.bothSides)
    {
        inward = definition.depth;
        outward = definition.depth;
    }
    const skin = { "entities" : face, "thickness1" : outward, "thickness2" : inward };
    try
    {
        opThicken(context, rid + "skinA", skin);
        opThicken(context, rid + "skinB", skin);
    }
    catch (e)
    {
        throw regenError("Could not offset the face by the depth. Try a smaller depth: it has to be less than the face's tightest inside radius.", ["depth", "regions"]);
    }
    const skinA = qCreatedBy(rid + "skinA", EntityType.BODY);
    const skinB = qCreatedBy(rid + "skinB", EntityType.BODY);
    opBoolean(context, rid + "skinHoles", {
                "tools" : prisms,
                "targets" : skinA,
                "operationType" : BooleanOperationType.SUBTRACTION
            });
    opBoolean(context, rid + "pockets", {
                "tools" : skinA,
                "targets" : skinB,
                "operationType" : BooleanOperationType.SUBTRACTION
            });
    return qBodyType(qUnion([skinB, qCreatedBy(rid + "pockets", EntityType.BODY)]), BodyType.SOLID);
}

// A blind pocket's floor faces back along the cut. If the cut runs upward,
// that floor is a ceiling: fine within the overhang limit, a bridge of about
// one cell width when horizontal, unprintable in between.
function pocketEndWarnings(definition is map, cutDir is Vector, up is Vector) returns array
{
    if (definition.endType != PatternEnd.BLIND)
    {
        return [];
    }
    var dirs = [cutDir];
    if (definition.bothSides)
    {
        dirs = append(dirs, cutDir * -1);
    }
    var out = [];
    for (var d in dirs)
    {
        const e = dot(d, up);
        if (e <= sin(definition.maxOverhang) + 1e-9)
        {
            continue;
        }
        if (e >= cos(BRIDGE_TOL))
        {
            if (definition.cellSize > definition.maxBridge + 1e-9 * millimeter)
            {
                out = append(out, "blind pocket roofs are flat bridges about " ~ roundToPrecision(definition.cellSize / millimeter, 1) ~ " mm wide (max bridge " ~ roundToPrecision(definition.maxBridge / millimeter, 1) ~ " mm)");
            }
        }
        else
        {
            out = append(out, "blind pocket roofs overhang " ~ roundToPrecision(asin(e) / degree, 0) ~ " deg (max " ~ roundToPrecision(definition.maxOverhang / degree, 0) ~ ")");
        }
    }
    return out;
}

function describeOrientation(frame is map) returns string
{
    const tilt = roundToPrecision(asin(min(1, frame.s)) / degree, 0);
    if (!frame.constrained)
    {
        return "holes " ~ tilt ~ " deg off the print axis, any shape prints";
    }
    return "holes " ~ tilt ~ " deg off the print axis, roofs need " ~ roundToPrecision(frame.thetaMin / degree, 0) ~ " deg" ~ (frame.bridges ? ", bridges allowed" : ", no bridging");
}

// =====================================================================
// Pure math — mirrors tools/pattern_math.py. No Context access below.
// Lengths carry units; angles in the normals lists are plain degrees.
// =====================================================================

// Local frame and limits for one region (pattern_math.pattern_frame).
// A wall whose in-plane outward normal makes it a ceiling overhangs
// asin(m.v * s) in 3D, so ceilings need a 2D slope of at least
// thetaMin = 90 deg - asin(sin(maxOverhang) / s).
function patternFrame(N is Vector, Z is Vector, maxOverhang is ValueWithUnits, patternAngle is ValueWithUnits, planeX is Vector) returns map
{
    const n = normalize(N);
    const zt = Z - n * dot(Z, n);
    const s = norm(zt);
    const sinOh = sin(maxOverhang);
    const constrained = s > sinOh + 1e-9;
    var v = s > 1e-9 ? normalize(zt) : normalize(cross(n, planeX));
    var angleIgnored = false;
    if (!constrained)
    {
        v = v * cos(patternAngle) + cross(n, v) * sin(patternAngle);
    }
    else if (abs(patternAngle / degree) > 1e-9)
    {
        angleIgnored = true;
    }
    const u = cross(v, n);
    var thetaMin = 0 * degree;
    if (constrained)
    {
        thetaMin = 90 * degree - asin(min(1, sinOh / s));
    }
    const bridges = constrained && s >= cos(BRIDGE_TOL) - 1e-9;
    return {
            "u" : u, "v" : v, "N" : n, "s" : s,
            "constrained" : constrained, "thetaMin" : thetaMin, "bridges" : bridges,
            "angleIgnored" : angleIgnored
        };
}

// CCW polygon whose edges have these outward-normal angles (degrees) and
// sit at distance rho from the origin; vertex k joins edges k and k+1.
function circumscribed(normalsDeg is array, rho is ValueWithUnits) returns array
{
    var ns = [];
    for (var a in normalsDeg)
    {
        var b = a;
        while (b < 0)
        {
            b += 360;
        }
        while (b >= 360)
        {
            b -= 360;
        }
        ns = append(ns, b);
    }
    ns = sort(ns, function(a, b)
        {
            return a - b;
        });
    const n = size(ns);
    var pts = [];
    for (var k = 0; k < n; k += 1)
    {
        const ai = ns[k] * degree;
        const aj = ns[(k + 1) % n] * degree;
        const d = sin(aj - ai);
        pts = append(pts, vector(rho * (sin(aj) - sin(ai)) / d, rho * (cos(ai) - cos(aj)) / d));
    }
    return pts;
}

function hexNormals(flatTop is boolean, thetaDeg is number) returns array
{
    if (flatTop)
    {
        return [30, 90, 150, 210, 270, 330];
    }
    return [0, 90 - thetaDeg, 90 + thetaDeg, 180, 270 - thetaDeg, 270 + thetaDeg];
}

// Point-up hexagon with six equal sides: vertical sides at x = +-r, roof
// and floor edges sloped thetaDeg from horizontal (30 is the regular
// hexagon). grow then moves every edge outward by that much, which gives
// the rib centre-line tile (no longer equal-sided). Vertex order matches
// circumscribed(hexNormals(false, thetaDeg), ...).
function pointyHex(r is ValueWithUnits, thetaDeg is number, grow is ValueWithUnits) returns array
{
    const th = thetaDeg * degree;
    const dSide = r + grow;
    const dRoof = r * sin(th) + r / 2 + grow;
    const y1 = (dRoof - dSide * sin(th)) / cos(th);
    const ya = dRoof / cos(th);
    return [vector(dSide, y1), vector(0 * meter, ya), vector(-dSide, y1), vector(-dSide, -y1), vector(0 * meter, -ya), vector(dSide, -y1)];
}

// Points turned counter-clockwise about the origin.
function rotatePoints(pts is array, angle is ValueWithUnits) returns array
{
    const c = cos(angle);
    const s = sin(angle);
    var out = [];
    for (var p in pts)
    {
        out = append(out, vector(p[0] * c - p[1] * s, p[0] * s + p[1] * c));
    }
    return out;
}

// Part of convex polygon pts with nx*x + ny*y <= c.
function clipHalfplane(pts is array, nx is number, ny is number, c is ValueWithUnits) returns array
{
    var out = [];
    const n = size(pts);
    for (var i = 0; i < n; i += 1)
    {
        const a = pts[i];
        const b = pts[(i + 1) % n];
        const fa = nx * a[0] + ny * a[1] - c;
        const fb = nx * b[0] + ny * b[1] - c;
        if (fa <= 0 * meter)
        {
            out = append(out, a);
        }
        if ((fa < 0 * meter && fb > 0 * meter) || (fb < 0 * meter && fa > 0 * meter))
        {
            const t = fa / (fa - fb);
            out = append(out, a + (b - a) * t);
        }
    }
    return out;
}

// Cut the top corners off a convex polygon with two roof lines sloped
// roofDeg from horizontal. The polygon keeps its full height: flatWidth of
// its top stays flat (centred, a bridge) and the roof lines fall away from
// the two ends of that flat. With flatWidth undefined they meet in a peak
// at the top centre.
function roofClip(pts is array, roofDeg is number, flatWidth) returns array
{
    var xmin = pts[0][0];
    var xmax = pts[0][0];
    var py = pts[0][1];
    for (var p in pts)
    {
        xmin = min(xmin, p[0]);
        xmax = max(xmax, p[0]);
        py = max(py, p[1]);
    }
    const px = (xmin + xmax) / 2;
    const th = roofDeg * degree;
    const sn = sin(th);
    const cs = cos(th);
    const half = flatWidth == undefined ? 0 * meter : 0.5 * flatWidth;
    var out = clipHalfplane(pts, sn, cs, (px + half) * sn + py * cs);
    out = clipHalfplane(out, -sn, cs, -(px - half) * sn + py * cs);
    return dropDuplicates(out);
}

// Remove points that coincide with the one before them (cyclic). The two
// roof lines of a peak both cut the top edge at the same point.
function dropDuplicates(pts is array) returns array
{
    var out = [];
    const n = size(pts);
    for (var i = 0; i < n; i += 1)
    {
        if (norm(pts[i] - pts[(i + n - 1) % n]) > 1e-6 * millimeter)
        {
            out = append(out, pts[i]);
        }
    }
    return out;
}

function teardropShape(r is ValueWithUnits, thetaDeg is number, flatWidth) returns map
{
    const th = thetaDeg * degree;
    const apex = r / cos(th);
    const yTan = r * cos(th);
    var yClip = undefined;
    if (flatWidth != undefined)
    {
        yClip = max(apex - 0.5 * flatWidth * tan(th), yTan);
    }
    return { "kind" : "teardrop", "r" : r, "theta" : th, "apex" : apex, "yTan" : yTan, "yClip" : yClip };
}

// Points that must all land on a face for a hole to count as whole there:
// the centre, then the outline's corners pushed margin further out (for a
// round outline, points around it at r + margin). Each pushed point is
// exactly margin from the outline.
function outlineSamples(shape is map, margin is ValueWithUnits) returns array
{
    var out = [vector(0, 0) * meter];
    if (shape.kind == "poly")
    {
        for (var p in shape.pts)
        {
            out = append(out, p * (1 + margin / norm(p)));
        }
        return out;
    }
    const r = shape.r + margin;
    var angles = [];
    if (shape.kind == "circle")
    {
        for (var i = 0; i < 8; i += 1)
        {
            angles = append(angles, 45 * i * degree);
        }
    }
    else
    {
        angles = [90 * degree + shape.theta, 180 * degree, 225 * degree, 270 * degree, 315 * degree, 0 * degree, 90 * degree - shape.theta];
    }
    for (var a in angles)
    {
        out = append(out, vector(r * cos(a), r * sin(a)));
    }
    if (shape.kind == "teardrop")
    {
        if (shape.yClip == undefined)
        {
            out = append(out, vector(0 * meter, shape.apex + margin));
        }
        else if (shape.yClip - shape.yTan > 1e-6 * millimeter)
        {
            const half = (shape.apex - shape.yClip) / tan(shape.theta);
            const f = 1 + margin / sqrt(half * half + shape.yClip * shape.yClip);
            out = append(out, vector(half * f, shape.yClip * f));
            out = append(out, vector(-half * f, shape.yClip * f));
        }
    }
    return out;
}

function boundRadius(shape is map) returns ValueWithUnits
{
    if (shape.kind == "poly")
    {
        var m = 0 * meter;
        for (var p in shape.pts)
        {
            m = max(m, norm(p));
        }
        return m;
    }
    if (shape.kind == "circle")
    {
        return shape.r;
    }
    if (shape.yClip == undefined)
    {
        return max(shape.r, shape.apex);
    }
    const half = (shape.apex - shape.yClip) / tan(shape.theta);
    return max(shape.r, sqrt(half * half + shape.yClip * shape.yClip));
}

// Lattice (t1, t2) plus the holes placed at each lattice point
// (pattern_math.build_cell_set). Holes are drawn around a circle of radius
// r = cellSize/2; the tiles (rib centre-lines) are the same shapes drawn
// around r + rib/2, and since the tiles tessellate exactly every wall is at
// least ribWidth thick.
function buildCellSet(definition is map, frame is map) returns map
{
    const r = definition.cellSize / 2;
    const R = r + definition.ribWidth / 2;
    const maxBridge = definition.maxBridge;
    const constrained = frame.constrained;
    const tmin = frame.thetaMin / degree;
    const bridges = frame.bridges;
    var warnings = [];
    const origin = vector(0, 0) * meter;

    if (definition.patternType == HolePattern.CIRCLES)
    {
        const cap = 2 * r * sin(frame.thetaMin); // chord of the too-flat part of the top
        const circleOk = !constrained || (bridges && cap <= maxBridge + 1e-9 * millimeter);
        var style = definition.circleStyle;
        if (style == CircleStyle.AUTO)
        {
            if (circleOk)
            {
                style = CircleStyle.CIRCLE;
            }
            else if (bridges && maxBridge > MIN_FEATURE)
            {
                style = CircleStyle.FLAT_TEARDROP;
            }
            else
            {
                style = CircleStyle.TEARDROP;
            }
        }
        const thTd = constrained ? tmin : 45;
        var hole;
        var thTile = 30;
        var label = "circles";
        if (style == CircleStyle.CIRCLE)
        {
            if (!circleOk)
            {
                warnings = append(warnings, "circle tops exceed the overhang/bridge limits");
            }
            hole = { "kind" : "circle", "r" : r };
        }
        else
        {
            if (style == CircleStyle.FLAT_TEARDROP && constrained && !bridges)
            {
                warnings = append(warnings, "flat-topped teardrops need a horizontal hole axis to bridge");
            }
            hole = teardropShape(r, thTd, style == CircleStyle.FLAT_TEARDROP ? maxBridge : undefined);
            thTile = max(30, thTd);
            label = (style == CircleStyle.FLAT_TEARDROP ? "flat teardrops" : "teardrops") ~ " (" ~ roundToPrecision(thTd, 0) ~ " deg cap)";
        }
        const tile = circumscribed(hexNormals(false, thTile), R);
        return {
                "t1" : tile[5] + tile[0], "t2" : tile[0] + tile[1],
                "holes" : [{ "offset" : origin, "shape" : hole }],
                "label" : label, "warnings" : warnings
            };
    }

    if (definition.patternType == HolePattern.HEXAGONS)
    {
        const topEdge = 2 * r * tan(30 * degree);
        const flatOk = !constrained || (tmin <= 60 + 1e-9 && bridges && topEdge <= maxBridge + 1e-9 * millimeter);
        var style = definition.hexStyle;
        var tile;
        var pts;
        var label;
        if (style == HexStyle.FLAT_TOP)
        {
            if (!flatOk)
            {
                warnings = append(warnings, "flat-top hexagon ceilings exceed the overhang/bridge limits");
            }
            const normals = hexNormals(true, 0);
            label = "hexagons (flat top)";
            tile = circumscribed(normals, R);
            pts = circumscribed(normals, r);
        }
        else
        {
            const tip = definition.hexTipAngle == undefined ? 0 * degree : definition.hexTipAngle;
            const lean = abs(tip / degree);
            const turned = lean < 1e-9 ? "" : ", tip turned " ~ roundToPrecision(tip / degree, 0) ~ " deg";
            var th = 30;
            if (style == HexStyle.POINTY)
            {
                // Stretched to print: six equal sides, roofs at the limit.
                // Turning the tip leans one roof flatter, so the roofs are
                // steepened by the same angle.
                th = constrained ? max(30, tmin + lean) : 30;
                if (th > 80)
                {
                    th = 80;
                    warnings = append(warnings, "tip angle too large for the overhang limit: hexagon roofs exceed it");
                }
                label = "hexagons (point up, stretched, " ~ roundToPrecision(th, 0) ~ " deg roof" ~ turned ~ ")";
            }
            else
            {
                // Auto: the regular hexagon with a corner up, whatever the
                // limits (owner's choice); the tip angle only turns it. The
                // label says when its flattest roof is over the limit.
                var note = "";
                const flattest = 30 - min(lean, 30);
                if (constrained && flattest < 0.5)
                {
                    if (!flatOk)
                    {
                        note = "; flat top exceeds the bridge/overhang limits";
                    }
                }
                else if (constrained && flattest < tmin - 1e-6)
                {
                    note = "; roofs overhang " ~ roundToPrecision(asin(min(1, frame.s * cos(flattest * degree))) / degree, 0)
                        ~ " deg, limit " ~ roundToPrecision(asin(min(1, frame.s * cos(frame.thetaMin))) / degree, 0);
                }
                label = "hexagons (point up" ~ turned ~ note ~ ")";
            }
            tile = rotatePoints(pointyHex(r, th, definition.ribWidth / 2), tip);
            pts = rotatePoints(pointyHex(r, th, 0 * meter), tip);
        }
        return {
                "t1" : tile[0] + tile[1], "t2" : tile[1] + tile[2],
                "holes" : [{ "offset" : origin, "shape" : { "kind" : "poly", "pts" : pts } }],
                "label" : label, "warnings" : warnings
            };
    }

    // Triangles: up triangle + its point reflection tile the plane.
    if (definition.patternType == HolePattern.TRIANGLES)
    {
        const th = constrained ? max(60, tmin) : 60;
        const nUp = [90 - th, 90 + th, 270];
        const tile = circumscribed(nUp, R); // [apex C, bottom-left A, bottom-right B]
        const C = tile[0];
        const A = tile[1];
        const B = tile[2];
        const up = circumscribed(nUp, r);
        var down = [];
        for (var p in up)
        {
            down = append(down, p * -1); // flat top, apex at the bottom
        }
        var holes = [{ "offset" : origin, "shape" : { "kind" : "poly", "pts" : up } }];
        const downOff = B + C;
        const topW = norm(down[1] - down[2]);
        const downOk = !constrained || (bridges && topW <= maxBridge + 1e-9 * millimeter);
        var style = definition.triangleStyle;
        if (style == TriangleStyle.AUTO)
        {
            style = downOk ? TriangleStyle.FULL : TriangleStyle.PEAKED;
        }
        var label;
        if (style == TriangleStyle.FULL)
        {
            if (!downOk)
            {
                warnings = append(warnings, "down-pointing triangle ceilings exceed the overhang/bridge limits");
            }
            holes = append(holes, { "offset" : downOff, "shape" : { "kind" : "poly", "pts" : down } });
            label = "triangles";
        }
        else if (style == TriangleStyle.PEAKED)
        {
            const roof = constrained ? tmin : 45;
            const flat = (bridges && maxBridge > MIN_FEATURE) ? min(maxBridge, topW) : undefined;
            holes = append(holes, { "offset" : downOff, "shape" : { "kind" : "poly", "pts" : roofClip(down, roof, flat) } });
            label = "triangles (down ones " ~ roundToPrecision(roof, 0) ~ " deg roof" ~ (flat == undefined ? "" : ", " ~ roundToPrecision(flat / millimeter, 1) ~ " mm flat") ~ ")";
        }
        else
        {
            label = "triangles (up only)";
        }
        if (th > 60 + 1e-9)
        {
            label = label ~ ", " ~ roundToPrecision(th, 0) ~ " deg sides";
        }
        return { "t1" : B - A, "t2" : C - A, "holes" : holes, "label" : label, "warnings" : warnings };
    }
    throw regenError("Unknown pattern type.", ["patternType"]);
}

// Hole centres covering the box, lattice centred on the box centre.
function latticeCandidates(cellSet is map, xmin is ValueWithUnits, ymin is ValueWithUnits,
    xmax is ValueWithUnits, ymax is ValueWithUnits) returns array
{
    const c0 = vector((xmin + xmax) / 2, (ymin + ymax) / 2);
    const t1 = cellSet.t1;
    const t2 = cellSet.t2;
    var rb = 0 * meter;
    for (var h in cellSet.holes)
    {
        rb = max(rb, boundRadius(h.shape) + norm(h.offset));
    }
    const det = t1[0] * t2[1] - t1[1] * t2[0];
    var ilo = 1e12;
    var ihi = -1e12;
    var jlo = 1e12;
    var jhi = -1e12;
    for (var cx in [xmin - rb, xmax + rb])
    {
        for (var cy in [ymin - rb, ymax + rb])
        {
            const dx = cx - c0[0];
            const dy = cy - c0[1];
            const i = (dx * t2[1] - dy * t2[0]) / det;
            const j = (t1[0] * dy - t1[1] * dx) / det;
            ilo = min(ilo, i);
            ihi = max(ihi, i);
            jlo = min(jlo, j);
            jhi = max(jhi, j);
        }
    }
    ilo = floor(ilo);
    ihi = ceil(ihi);
    jlo = floor(jlo);
    jhi = ceil(jhi);
    const estimate = (ihi - ilo + 1) * (jhi - jlo + 1) * size(cellSet.holes);
    if (estimate > MAX_CANDIDATES)
    {
        throw regenError("The pattern would need about " ~ estimate ~ " cells (limit " ~ MAX_CANDIDATES ~ "). Increase the cell size.", ["cellSize"]);
    }
    var out = [];
    for (var j = jlo; j <= jhi; j += 1)
    {
        for (var i = ilo; i <= ihi; i += 1)
        {
            const base = c0 + t1 * i + t2 * j;
            for (var h in cellSet.holes)
            {
                const c = base + h.offset;
                const rh = boundRadius(h.shape);
                if (c[0] >= xmin - rh && c[0] <= xmax + rh && c[1] >= ymin - rh && c[1] <= ymax + rh)
                {
                    out = append(out, { "center" : c, "shape" : h.shape });
                }
            }
        }
    }
    return out;
}

// ---------------------------------------------------------------------
// Sketching (pattern_math.teardrop_profile mirrors the teardrop branch)
// ---------------------------------------------------------------------
function sketchHole(sketch is Sketch, prefix is string, center is Vector, shape is map)
{
    if (shape.kind == "circle")
    {
        skCircle(sketch, prefix, { "center" : center, "radius" : shape.r });
        return;
    }
    if (shape.kind == "poly")
    {
        var pts = [];
        for (var p in shape.pts)
        {
            pts = append(pts, center + p);
        }
        sketchClosedPolygon(sketch, prefix, pts);
        return;
    }
    // Teardrop: arc under the circle, then a pointed or flat cap.
    const r = shape.r;
    const tr = center + vector(r * sin(shape.theta), shape.yTan);
    const tl = center + vector(-r * sin(shape.theta), shape.yTan);
    skArc(sketch, prefix ~ "_arc", { "start" : tr, "mid" : center + vector(0 * meter, -r), "end" : tl });
    if (shape.yClip == undefined)
    {
        const top = center + vector(0 * meter, shape.apex);
        skLineSegment(sketch, prefix ~ "_l", { "start" : tl, "end" : top });
        skLineSegment(sketch, prefix ~ "_r", { "start" : top, "end" : tr });
        return;
    }
    const half = (shape.apex - shape.yClip) / tan(shape.theta);
    if (shape.yClip - shape.yTan > 1e-6 * millimeter)
    {
        const cl = center + vector(-half, shape.yClip);
        const cr = center + vector(half, shape.yClip);
        skLineSegment(sketch, prefix ~ "_l", { "start" : tl, "end" : cl });
        skLineSegment(sketch, prefix ~ "_t", { "start" : cl, "end" : cr });
        skLineSegment(sketch, prefix ~ "_r", { "start" : cr, "end" : tr });
    }
    else
    {
        skLineSegment(sketch, prefix ~ "_t", { "start" : tl, "end" : tr });
    }
}

function sketchClosedPolygon(sketch is Sketch, prefix is string, pts is array)
{
    const n = size(pts);
    for (var i = 0; i < n; i += 1)
    {
        skLineSegment(sketch, prefix ~ "_e" ~ i, { "start" : pts[i], "end" : pts[(i + 1) % n] });
    }
}
