using System.Text.Json;

namespace AICutPro.Core;

/// <summary>
/// Versioned, fail-closed preview-annotation sidecar. Every imported DXF LINE
/// is matched by ordered layer and exact coordinates, so assigning against a
/// different drawing never silently transfers an operation to another shape.
/// This is not a CAD file and is never a machine program.
/// </summary>
public static class SheetLineAssignmentSidecar
{
    private sealed record LineIdentity(string Layer, Point2 Start, Point2 End);
    private sealed record Assignment(int Index, SheetLineOperation Operation);
    private sealed record Payload(
        int Version, double SheetWidthMm, double SheetHeightMm,
        LineIdentity[] Lines, Assignment[] Assignments,
        SheetUShapeAnnotation[]? UShapes = null,
        SheetChainAnnotation[]? Chains = null);

    private const int CurrentVersion = 3;
    private const int MaximumLines = 100_000;
    private const int MaximumJsonChars = 8_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static string Export(
        IReadOnlyList<DxfLine> lines, Sheet sheet,
        SheetLineAssignmentRegister register,
        SheetUShapeAssignmentRegister? uShapes = null,
        SheetChainAssignmentRegister? chains = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(register);
        CheckDocument(lines, sheet);
        var assignments = register.Snapshot();
        if (assignments.Count > lines.Count)
            throw new FormatException("More assignments than imported lines.");
        foreach (var item in assignments)
        {
            if (item.Key < 0 || item.Key >= lines.Count ||
                !SheetOperationPlanner.InspectOpenLine(item.Value, sheet).IsValid ||
                item.Value.Start != lines[item.Key].Start ||
                item.Value.End != lines[item.Key].End)
                throw new FormatException("Cannot export invalid line assignment.");
        }
        var uAnnotations = uShapes?.Snapshot() ??
            new Dictionary<int, SheetUShapeAnnotation>();
        if (uAnnotations.Count > lines.Count)
            throw new FormatException("Too many U-shaped annotations.");
        var uValidator = new SheetUShapeAssignmentRegister();
        foreach (var annotation in uAnnotations.Values)
        {
            if (!uValidator.Assign(annotation, lines, sheet).IsValid ||
                annotation.BaseLineIndex < 0)
                throw new FormatException("Cannot export invalid U-shaped annotation.");
        }
        var chainItems = chains?.Snapshot() ??
            new Dictionary<int, SheetChainAnnotation>();
        if (chainItems.Count > lines.Count)
            throw new FormatException("Too many open-chain annotations.");
        var chainValidator = new SheetChainAssignmentRegister();
        foreach (var annotation in chainItems.Values)
            if (!chainValidator.Assign(annotation, lines, sheet).IsValid)
                throw new FormatException("Cannot export invalid open-chain annotation.");
        var payload = new Payload(CurrentVersion, sheet.WidthMm, sheet.HeightMm,
            lines.Select(x => new LineIdentity(x.Layer, x.Start, x.End)).ToArray(),
            assignments.OrderBy(x => x.Key)
                .Select(x => new Assignment(x.Key, x.Value)).ToArray(),
            uAnnotations.Values.OrderBy(x => x.BaseLineIndex).ToArray(),
            chainItems.Values.OrderBy(x => x.SelectedLineIndex).ToArray());
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (json.Length > MaximumJsonChars)
            throw new FormatException("Annotation sidecar is too large.");
        return json;
    }

    /// <summary>
    /// Restore is all-or-nothing. Failure leaves the register unchanged.
    /// </summary>
    public static void Restore(
        string json, IReadOnlyList<DxfLine> lines, Sheet sheet,
        SheetLineAssignmentRegister register,
        SheetUShapeAssignmentRegister? uShapes = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(register);
        CheckDocument(lines, sheet);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonChars)
            throw new FormatException("Invalid annotation sidecar size.");
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions); }
        catch (JsonException exc) { throw new FormatException("Malformed sidecar JSON.", exc); }
        if (payload is null || (payload.Version is not (1 or 2 or CurrentVersion)) ||
            payload.SheetWidthMm != sheet.WidthMm ||
            payload.SheetHeightMm != sheet.HeightMm ||
            payload.Lines is null || payload.Assignments is null ||
            payload.Lines.Length != lines.Count ||
            payload.Assignments.Length > lines.Count ||
            (payload.UShapes?.Length ?? 0) > lines.Count ||
            (payload.Chains?.Length ?? 0) > lines.Count ||
            (payload.Version < 3 && payload.Chains is { Length: > 0 }) ||
            (payload.Version == 1 && payload.UShapes is { Length: > 0 }) )
            throw new FormatException("Sidecar version, dimensions or geometry count differ.");
        for (var i = 0; i < lines.Count; i++)
        {
            var identity = payload.Lines[i];
            if (identity is null || identity.Layer != lines[i].Layer ||
                identity.Start != lines[i].Start || identity.End != lines[i].End)
                throw new FormatException("DXF LINE identities differ from saved annotations.");
        }
        var staged = new SheetLineAssignmentRegister();
        foreach (var item in payload.Assignments)
        {
            if (item is null || item.Index < 0 || item.Index >= lines.Count ||
                item.Operation is null || staged.TryGet(item.Index, out _))
                throw new FormatException("Invalid or duplicate line assignment.");
            var line = lines[item.Index];
            if (!staged.Assign(item.Index, line.Start, line.End,
                    item.Operation, sheet).IsValid)
                throw new FormatException("Saved line operation failed validation.");
        }
        if (uShapes is null && payload.UShapes is { Length: > 0 })
            throw new FormatException("This loader cannot restore U-shaped groups.");
        var stagedU = new SheetUShapeAssignmentRegister();
        foreach (var annotation in payload.UShapes ?? [])
        {
            if (annotation is null ||
                stagedU.TryGet(annotation.BaseLineIndex, out _) ||
                !stagedU.Assign(annotation, lines, sheet).IsValid)
                throw new FormatException("Saved U-shaped annotation failed validation.");
        }
        if (chains is null && payload.Chains is { Length: > 0 })
            throw new FormatException("This loader cannot restore open-chain groups.");
        var stagedChains = new SheetChainAssignmentRegister();
        foreach (var annotation in payload.Chains ?? [])
        {
            if (annotation is null ||
                stagedChains.TryGet(annotation.SelectedLineIndex, out _) ||
                !stagedChains.Assign(annotation, lines, sheet).IsValid)
                throw new FormatException("Saved open-chain annotation failed validation.");
        }
        // Commit only after ALL annotation categories pass validation.
        register.ReplaceValidated(staged.Snapshot());
        uShapes?.ReplaceValidated(stagedU.Snapshot());
        chains?.ReplaceValidated(stagedChains.Snapshot());
    }

    private static void CheckDocument(IReadOnlyList<DxfLine> lines, Sheet sheet)
    {
        if (lines.Count > MaximumLines || !double.IsFinite(sheet.WidthMm)
            || !double.IsFinite(sheet.HeightMm) ||
            sheet.WidthMm <= 0 || sheet.HeightMm <= 0)
            throw new FormatException("Invalid sheet or excessive DXF line count.");
        foreach (var line in lines)
        {
            if (line is null || line.Layer is null ||
                !double.IsFinite(line.Start.X) || !double.IsFinite(line.Start.Y) ||
                !double.IsFinite(line.End.X) || !double.IsFinite(line.End.Y))
                throw new FormatException("Invalid DXF LINE identity.");
        }
    }
}
