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
        return parsed;
    }
}
