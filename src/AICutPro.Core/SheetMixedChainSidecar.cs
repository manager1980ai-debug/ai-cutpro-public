using System.Text.Json;

namespace AICutPro.Core;

/// <summary>
/// Deliberate preview annotation for a selected DXF ARC/LINE/open POLYLINE
/// component. It records intent only; no G-code, kerf, cutting path or motion.
/// </summary>
public sealed record SheetMixedChainAnnotation(
    int SelectedShapeIndex,
    SheetOperationKind Kind,
    SheetLineProcessingMode Processing);

public sealed class SheetMixedChainAssignmentRegister
{
    private readonly Dictionary<int, SheetMixedChainAnnotation> _entries = new();
    public int Count => _entries.Count;
    public bool CanGenerateMachineCommands => false;
    public IReadOnlyDictionary<int, SheetMixedChainAnnotation> Snapshot()
        => new Dictionary<int, SheetMixedChainAnnotation>(_entries);
    public bool TryGet(int index, out SheetMixedChainAnnotation? value)
        => _entries.TryGetValue(index, out value);
    public bool Remove(int index) => _entries.Remove(index);

    internal void ReplaceValidated(
        IReadOnlyDictionary<int, SheetMixedChainAnnotation> entries)
    {
        _entries.Clear();
        foreach (var entry in entries)
            _entries.Add(entry.Key, entry.Value);
    }

    public static MixedOpenChainPreview Validate(
        SheetMixedChainAnnotation annotation,
        IReadOnlyList<DxfShape> shapes, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(sheet);
        if (annotation.Kind is not
            (SheetOperationKind.OpenThroughCut or
             SheetOperationKind.PerforatedCut or
             SheetOperationKind.BendReference or SheetOperationKind.Marking) ||
            annotation.Processing is not
            (SheetLineProcessingMode.NoCut or
             SheetLineProcessingMode.SurfaceScore or
             SheetLineProcessingMode.ThroughCut or
             SheetLineProcessingMode.PerforatedThroughCut))
            return new(false, [], null, 0,
                ["Mixed open-chain preview needs an explicit valid operation and treatment."]);
        return MixedOpenChainAssembler.Assemble(
            shapes, annotation.SelectedShapeIndex, sheet);
    }

    public MixedOpenChainPreview Assign(
        SheetMixedChainAnnotation annotation,
        IReadOnlyList<DxfShape> shapes, Sheet sheet)
    {
        var candidate = Validate(annotation, shapes, sheet);
        if (!candidate.IsValid)
            return candidate;
        foreach (var existing in _entries)
        {
            if (existing.Key == annotation.SelectedShapeIndex)
                continue;
            var path = MixedOpenChainAssembler.Assemble(shapes, existing.Key, sheet);
            if (!path.IsValid ||
                path.SourceIndices.Any(candidate.SourceIndices.Contains))
                return new(false, [], null, 0,
                    ["Mixed chain overlaps a saved chain or its DXF geometry changed."]);
        }
        _entries[annotation.SelectedShapeIndex] = annotation;
        return candidate;
    }
}

/// <summary>
/// Separate, versioned mixed-geometry annotation file. Existing v1-v3 LINE
/// sidecars remain untouched. Restore validates all entries before mutation.
/// Every DXF shape is identified by its exact type, layer and geometry values,
/// preserving import ordering and making stale-file reassignment impossible.
/// </summary>
public static class SheetMixedChainSidecar
{
    private const int MaxJsonChars = 8_000_000;
    private sealed record Payload(
        int Version, double SheetWidthMm, double SheetHeightMm,
        string[] ShapeIdentities, SheetMixedChainAnnotation[] Entries);

    public static string Export(IReadOnlyList<DxfShape> shapes, Sheet sheet,
        SheetMixedChainAssignmentRegister register)
    {
        ArgumentNullException.ThrowIfNull(register);
        var identities = Identities(shapes, sheet);
        var entries = register.Snapshot().Values
            .OrderBy(x => x.SelectedShapeIndex).ToArray();
        var validated = new SheetMixedChainAssignmentRegister();
        foreach (var entry in entries)
            if (!validated.Assign(entry, shapes, sheet).IsValid)
                throw new FormatException("Cannot export invalid mixed-chain assignment.");
        var json = JsonSerializer.Serialize(new Payload(
            1, sheet.WidthMm, sheet.HeightMm, identities, entries));
        if (json.Length > MaxJsonChars)
            throw new FormatException("Mixed-chain sidecar is too large.");
        return json;
    }

    public static void Restore(string json, IReadOnlyList<DxfShape> shapes,
        Sheet sheet, SheetMixedChainAssignmentRegister destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var actual = Identities(shapes, sheet);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonChars)
            throw new FormatException("Invalid mixed-chain sidecar size.");
        SidecarJsonGuard.RejectDuplicateProperties(json);
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(json); }
        catch (JsonException ex)
        {
            throw new FormatException("Malformed mixed-chain sidecar JSON.", ex);
        }
        if (payload is null || payload.Version != 1 ||
            payload.SheetWidthMm != sheet.WidthMm ||
            payload.SheetHeightMm != sheet.HeightMm ||
            payload.ShapeIdentities is null || payload.Entries is null ||
            payload.ShapeIdentities.Length != actual.Length ||
            payload.Entries.Length > shapes.Count ||
            !payload.ShapeIdentities.SequenceEqual(actual))
            throw new FormatException(
                "Mixed-chain sidecar version, sheet or exact DXF geometry differs.");
        var staged = new SheetMixedChainAssignmentRegister();
        foreach (var entry in payload.Entries)
            if (entry is null ||
                staged.TryGet(entry.SelectedShapeIndex, out _) ||
                !staged.Assign(entry, shapes, sheet).IsValid)
                throw new FormatException("Invalid or duplicate saved mixed chain.");
        destination.ReplaceValidated(staged.Snapshot());
    }

    private static string[] Identities(IReadOnlyList<DxfShape> shapes, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(sheet);
        if (shapes.Count is < 1 or > 1000 ||
            !double.IsFinite(sheet.WidthMm) || sheet.WidthMm <= 0 ||
            !double.IsFinite(sheet.HeightMm) || sheet.HeightMm <= 0)
            throw new FormatException("Invalid drawing or sheet size.");
        var result = new string[shapes.Count];
        for (var i = 0; i < shapes.Count; i++)
        {
            result[i] = shapes[i] switch
            {
                DxfLine x => "LINE:" + JsonSerializer.Serialize(x),
                DxfArc x => "ARC:" + JsonSerializer.Serialize(x),
                DxfCircle x => "CIRCLE:" + JsonSerializer.Serialize(x),
                DxfPolyline x => "POLYLINE:" + JsonSerializer.Serialize(x),
                DxfBulgedPolyline x => "BULGED:" + JsonSerializer.Serialize(x),
                _ => throw new FormatException("Unsupported DXF shape identity.")
            };
            if (result[i].Length > 250_000)
                throw new FormatException("Excessive DXF shape identity size.");
        }
        return result;
    }
}
