namespace AICutPro.Core;

/// <summary>
/// Source geometry and both preview-technology annotation formats embedded in
/// one editable CutProject. Input is ASCII DXF in millimetres only; never CAM.
/// </summary>
public sealed record CutProjectDxfWorkspace(
    string SourceAsciiDxf,
    string LineAnnotationsJson,
    string MixedAnnotationsJson);

public static class CutProjectDxfWorkspaceCodec
{
    public static CutProjectDxfWorkspace Create(
        string sourceAsciiDxf, Sheet sheet,
        SheetLineAssignmentRegister lineAssignments,
        SheetUShapeAssignmentRegister uAssignments,
        SheetChainAssignmentRegister straightChains,
        SheetMixedChainAssignmentRegister mixedChains)
    {
        ArgumentNullException.ThrowIfNull(sourceAsciiDxf);
        ArgumentNullException.ThrowIfNull(lineAssignments);
        ArgumentNullException.ThrowIfNull(uAssignments);
        ArgumentNullException.ThrowIfNull(straightChains);
        ArgumentNullException.ThrowIfNull(mixedChains);
        var document = ParseSource(sourceAsciiDxf);
        var sourceLines = document.Shapes.OfType<DxfLine>().ToArray();
        var conflicts = SheetTechnologyOverlapReview.Inspect(
            document.Shapes, sheet, lineAssignments, uAssignments,
            straightChains, mixedChains);
        if (conflicts.Count > 0)
            throw new FormatException("Conflicting DXF technology: " +
                string.Join("; ", conflicts.Take(4)));
        var legacy = SheetLineAssignmentSidecar.Export(
            sourceLines, sheet, lineAssignments, uAssignments, straightChains);
        var mixed = SheetMixedChainSidecar.Export(
            document.Shapes, sheet, mixedChains);
        return new(sourceAsciiDxf, legacy, mixed);
    }

    /// <summary>
    /// Restores independent registries, all validated before any project data
    /// is returned. Deserialization never authorizes a machine operation.
    /// </summary>
    public static (DxfImportResult Document,
        SheetLineAssignmentRegister Lines,
        SheetUShapeAssignmentRegister UGroups,
        SheetChainAssignmentRegister StraightChains,
        SheetMixedChainAssignmentRegister MixedChains)
        Load(CutProjectDxfWorkspace workspace, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(sheet);
        var document = ParseSource(workspace.SourceAsciiDxf);
        var lineAssignments = new SheetLineAssignmentRegister();
        var uAssignments = new SheetUShapeAssignmentRegister();
        var straightChains = new SheetChainAssignmentRegister();
        var mixedChains = new SheetMixedChainAssignmentRegister();
        SheetLineAssignmentSidecar.Restore(
            workspace.LineAnnotationsJson, document.Shapes.OfType<DxfLine>().ToArray(),
            sheet, lineAssignments, uAssignments, straightChains);
        SheetMixedChainSidecar.Restore(
            workspace.MixedAnnotationsJson, document.Shapes, sheet, mixedChains);
        var issues = SheetTechnologyOverlapReview.Inspect(
            document.Shapes, sheet, lineAssignments, uAssignments,
            straightChains, mixedChains);
        if (issues.Count != 0)
            throw new FormatException("Embedded DXF annotations conflict: " +
                string.Join("; ", issues.Take(4)));
        return (document, lineAssignments, uAssignments, straightChains, mixedChains);
    }

    /// <summary>
    /// Updates the textual DXF while preserving every technology assignment
    /// ONLY when the complete ordered shape identities remain exactly equal.
    /// Both original sidecars independently validate all source shapes, not
    /// just the ones with assigned operations. No geometric tolerance, index
    /// guessing or reordering is permitted.
    /// </summary>
    public static CutProject ReplaceSourcePreservingExactGeometry(
        CutProject project, string replacementAsciiDxf)
    {
        ArgumentNullException.ThrowIfNull(project);
        CutProjectSerializer.Validate(project);
        ArgumentNullException.ThrowIfNull(replacementAsciiDxf);
        var previous = project.DxfWorkspace ??
            throw new FormatException("There is no embedded DXF to preserve.");
        if (string.Equals(previous.SourceAsciiDxf, replacementAsciiDxf,
                StringComparison.Ordinal))
            return project;

        // Parse and validate new source before considering any technology.
        var replacementDocument = ParseSource(replacementAsciiDxf);
        var original = Load(previous, project.Sheet);
        // The mixed sidecar pins identities for ALL source entities, including
        // unassigned lines, arcs, polylines and circles. Reject any difference.
        SheetMixedChainSidecar.Restore(
            previous.MixedAnnotationsJson, replacementDocument.Shapes,
            project.Sheet, new SheetMixedChainAssignmentRegister());
        // LINE identities and all original operations are additionally checked.
        SheetLineAssignmentSidecar.Restore(
            previous.LineAnnotationsJson,
            replacementDocument.Shapes.OfType<DxfLine>().ToArray(), project.Sheet,
            new SheetLineAssignmentRegister(), new SheetUShapeAssignmentRegister(),
            new SheetChainAssignmentRegister());

        var candidate = project with
        {
            DxfWorkspace = Create(replacementAsciiDxf, project.Sheet,
                original.Lines, original.UGroups, original.StraightChains,
                original.MixedChains)
        };
        CutProjectSerializer.Validate(candidate);
        return candidate;
    }

    /// <summary>
    /// Safely replaces the source DXF within a project. Annotation transfer across
    /// changed geometry is never guessed: the operator must explicitly request
    /// discarding every old annotation. Identical source may retain them.
    /// Returns a new immutable project only after complete validation.
    /// </summary>
    public static CutProject ReplaceSource(
        CutProject project, string replacementAsciiDxf,
        bool discardExistingAssignments)
    {
        ArgumentNullException.ThrowIfNull(project);
        CutProjectSerializer.Validate(project);
        ArgumentNullException.ThrowIfNull(replacementAsciiDxf);

        var previous = project.DxfWorkspace;
        if (previous is not null &&
            string.Equals(previous.SourceAsciiDxf, replacementAsciiDxf, StringComparison.Ordinal))
        {
            // No geometry change: the old valid registers stay bound to the same bytes.
            return project;
        }
        if (previous is not null && !discardExistingAssignments)
            throw new FormatException(
                "DXF geometry changed: explicit confirmation to discard all technology assignments is required.");

        // No register is copied or rebound to potentially different entity indices.
        // An empty export still validates units, source entity set and sheet size.
        var clean = Create(
            replacementAsciiDxf, project.Sheet,
            new SheetLineAssignmentRegister(),
            new SheetUShapeAssignmentRegister(),
            new SheetChainAssignmentRegister(),
            new SheetMixedChainAssignmentRegister());
        var candidate = project with { DxfWorkspace = clean };
        CutProjectSerializer.Validate(candidate);
        return candidate;
    }

    /// <summary>Serialize verified preview entities without converting circles or layers.</summary>
    public static string SerializeEditedDocument(DxfImportResult document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.InsUnits != 4 || document.Shapes.Count is < 1 or > 1000)
            throw new FormatException("Invalid embedded DXF edit.");
        var output = new System.Text.StringBuilder();
        void Pair(int code, string value)
        {
            output.Append(code.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n').Append(value).Append('\n');
        }
        void Number(int code, double value)
        {
            if (!double.IsFinite(value)) throw new FormatException("Nonfinite DXF value.");
            Pair(code, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        void Point(int xCode, int yCode, Point2 point)
        {
            Number(xCode, point.X); Number(yCode, point.Y);
        }
        void Entity(string type, string layer)
        {
            if (string.IsNullOrWhiteSpace(layer) || layer.Trim() != layer ||
                layer.Any(c => c < 32 || c > 126))
                throw new FormatException("Unsupported ASCII DXF layer.");
            Pair(0, type); Pair(8, layer);
        }
        void Polyline(string layer, IReadOnlyList<Point2> vertices,
            bool closed, IReadOnlyList<double>? bulges = null)
        {
            if (bulges is not null && bulges.Count != vertices.Count)
                throw new FormatException("Invalid bulge count.");
            Entity("LWPOLYLINE", layer);
            Number(90, vertices.Count); Number(70, closed ? 1 : 0);
            for (int i = 0; i < vertices.Count; i++)
            {
                Point(10, 20, vertices[i]);
                if (bulges is not null) Number(42, bulges[i]);
            }
        }
        Pair(0, "SECTION"); Pair(2, "HEADER");
        Pair(9, "$ACADVER"); Pair(1, "AC1032");
        Pair(9, "$INSUNITS"); Pair(70, "4");
        Pair(0, "ENDSEC"); Pair(0, "SECTION"); Pair(2, "ENTITIES");
        foreach (var shape in document.Shapes)
        {
            switch (shape)
            {
                case DxfLine line:
                    Entity("LINE", line.Layer);
                    Point(10, 20, line.Start); Point(11, 21, line.End); break;
                case DxfCircle circle:
                    Entity("CIRCLE", circle.Layer);
                    Point(10, 20, circle.Center); Number(40, circle.Radius); break;
                case DxfArc arc:
                    Entity("ARC", arc.Layer);
                    Point(10, 20, arc.Center); Number(40, arc.Radius);
                    Number(50, arc.StartDeg); Number(51, arc.EndDeg); break;
                case DxfPolyline poly:
                    Polyline(poly.Layer, poly.Vertices, poly.Closed); break;
                case DxfBulgedPolyline bulged:
                    Polyline(bulged.Layer, bulged.Vertices, bulged.Closed, bulged.Bulges); break;
                default: throw new FormatException("Unsupported DXF entity.");
            }
        }
        Pair(0, "ENDSEC"); Pair(0, "EOF");
        var source = output.ToString();
        var reopened = ParseSource(source);
        if (reopened.Shapes.Count != document.Shapes.Count ||
            !reopened.Shapes.Zip(document.Shapes).All(pair => SameShape(pair.First, pair.Second)))
            throw new FormatException("Edited DXF entity identity changed on serialization.");
        return source;

        static bool SameShape(DxfShape a, DxfShape b) => (a, b) switch
        {
            (DxfPolyline x, DxfPolyline y) => x.Layer == y.Layer &&
                x.Closed == y.Closed && x.Vertices.SequenceEqual(y.Vertices),
            (DxfBulgedPolyline x, DxfBulgedPolyline y) => x.Layer == y.Layer &&
                x.Closed == y.Closed && x.Vertices.SequenceEqual(y.Vertices) &&
                x.Bulges.SequenceEqual(y.Bulges),
            _ => a == b
        };
    }

    public static void Validate(CutProjectDxfWorkspace workspace, Sheet sheet)
        => _ = Load(workspace, sheet);

    private static DxfImportResult ParseSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 4_000_000)
            throw new FormatException("Embedded DXF exceeds the project source limit.");
        DxfImportResult parsed;
        try { parsed = DxfReader.Parse(source); }
        catch (ArgumentException ex)
        {
            throw new FormatException("Embedded DXF cannot be parsed.", ex);
        }
        // The importer uses these precise warnings for analytically
        // represented bulge arcs which remain display-only. They do not mean
        // that shapes were skipped. Any other warning still blocks embedding.
        var unknownWarnings = parsed.Warnings.Where(w => w is not
            ("Bulged LWPOLYLINE is preview-only: CAD/CAM geometry is not yet certified." or
             "Legacy bulged POLYLINE is preview-only: CAD/CAM not certified.")).ToArray();
        if (parsed.Shapes.Count is < 1 or > 1000 ||
            parsed.InsUnits != 4 || unknownWarnings.Length > 0)
            throw new FormatException(
                "Embedded DXF must contain supported entities, declare millimetres and have no unsupported import warnings.");
        // The project file is durable state, not an unrestricted diagnostic dump.
        // Very large finite coordinates/radii lead to overflow or catastrophic
        // precision loss in subsequent preview operations. Keep this bound in
        // sync with GeometryPreflight's default maximumCoordinateMm.
        const double maximumSourceCoordinateMm = 1_000_000;
        static bool Bounded(double value) =>
            double.IsFinite(value) && Math.Abs(value) <= maximumSourceCoordinateMm;
        static bool BoundedPoint(Point2 point) =>
            Bounded(point.X) && Bounded(point.Y);
        var outOfRange = parsed.Shapes.Any(shape => shape switch
        {
            DxfLine line => !BoundedPoint(line.Start) || !BoundedPoint(line.End),
            DxfCircle circle => !BoundedPoint(circle.Center) ||
                                !Bounded(circle.Radius),
            DxfArc arc => !BoundedPoint(arc.Center) || !Bounded(arc.Radius) ||
                          !double.IsFinite(arc.StartDeg) || !double.IsFinite(arc.EndDeg),
            DxfPolyline polyline => polyline.Vertices.Any(point => !BoundedPoint(point)),
            DxfBulgedPolyline bulged => bulged.Vertices.Any(point => !BoundedPoint(point)),
            _ => true
        });
        if (outOfRange || parsed.Shapes.Any(shape => !CurvedBoundsFit(shape)))
            throw new FormatException(
                "Embedded DXF has unsupported or out-of-range geometry (maximum 1000000 mm).");
        return parsed;

        // A center inside the allowed coordinates does not guarantee its
        // CIRCLE/ARC/bulge lies inside: inspect analytic extrema, not chords.
        static bool CurvedBoundsFit(DxfShape shape)
        {
            static bool Fits(PreviewContourBoundsResult bounds) =>
                bounds.IsValid &&
                Math.Abs(bounds.MinX) <= maximumSourceCoordinateMm &&
                Math.Abs(bounds.MaxX) <= maximumSourceCoordinateMm &&
                Math.Abs(bounds.MinY) <= maximumSourceCoordinateMm &&
                Math.Abs(bounds.MaxY) <= maximumSourceCoordinateMm;
            if (shape is DxfCircle circle)
                return FitsCircle(circle.Center, circle.Radius);
            if (shape is DxfArc arc)
            {
                var sweep = ((arc.EndDeg - arc.StartDeg) % 360 + 360) % 360;
                if (sweep < 1e-9)
                    return false;
                return Fits(PreviewContourBounds.Measure(
                    new PreviewPath([
                        new PreviewArc(
                            new PreviewPoint(arc.Center.X, arc.Center.Y),
                            arc.Radius, arc.StartDeg, sweep)
                    ], false)));
            }
            if (shape is DxfBulgedPolyline bulged)
                return Fits(PreviewContourBounds.Measure(DxfBulgePreview.Build(bulged)));
            return true;
        }

        static bool FitsCircle(Point2 center, double radius)
        {
            return radius > 0 &&
                double.IsFinite(center.X + radius) &&
                double.IsFinite(center.X - radius) &&
                double.IsFinite(center.Y + radius) &&
                double.IsFinite(center.Y - radius) &&
                Math.Abs(center.X + radius) <= maximumSourceCoordinateMm &&
                Math.Abs(center.X - radius) <= maximumSourceCoordinateMm &&
                Math.Abs(center.Y + radius) <= maximumSourceCoordinateMm &&
                Math.Abs(center.Y - radius) <= maximumSourceCoordinateMm;
        }
    }
}
