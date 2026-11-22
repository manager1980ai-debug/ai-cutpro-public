namespace AICutPro.Core;

/// <summary>
/// Bounded, in-memory undo/redo history for geometry-only DXF preview edits.
/// Snapshots are defensive copies: callers cannot mutate stored contours or
/// restore stale editing references by changing an input array.
/// No machine instructions, CAM approvals or file modifications are involved.
/// </summary>
public sealed class DxfPreviewHistory
{
    public const int MaximumStates = 31; // current + at most 30 undo operations

    private readonly List<DxfImportResult> _states = [];
    private readonly List<IReadOnlyList<PreviewAxisRule>> _axisRules = [];
    private int _index;

    public DxfPreviewHistory(DxfImportResult initial)
    {
        _states.Add(Snapshot(initial));
        _axisRules.Add([]);
    }

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _states.Count - 1;
    public int UndoCount => _index;
    public int RedoCount => _states.Count - 1 - _index;
    public DxfImportResult Current => Snapshot(_states[_index]);
    public IReadOnlyList<PreviewAxisRule> CurrentAxisRules =>
        _axisRules[_index].ToArray();

    /// <summary>Call only after an edit has passed geometry and DXF verification.</summary>
    public void Push(DxfImportResult verifiedEdit) =>
        Push(verifiedEdit,CurrentAxisRules);

    /// <summary>
    /// Save geometry and persistent drafting relations in one Undo/Redo step.
    /// Caller must verify both geometry and axis rules before committing.
    /// </summary>
    public void Push(DxfImportResult verifiedEdit,
                     IReadOnlyList<PreviewAxisRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var clone=Snapshot(verifiedEdit);
        if(rules.Count>PreviewPersistentAxisConstraints.MaximumRules)
            throw new FormatException("Too many persistent axis constraints.");
        if(_index < _states.Count-1)
        {
            _states.RemoveRange(_index+1,_states.Count-1-_index);
            _axisRules.RemoveRange(_index+1,_axisRules.Count-1-_index);
        }
        _states.Add(clone);
        _axisRules.Add(rules.ToArray());
        if(_states.Count>MaximumStates)
        {
            _states.RemoveAt(0);
            _axisRules.RemoveAt(0);
        }
        _index=_states.Count-1;
    }

    public DxfImportResult Undo()
    {
        if (!CanUndo) throw new InvalidOperationException("Nothing to undo.");
        _index--;
        return Current;
    }

    public DxfImportResult Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("Nothing to redo.");
        _index++;
        return Current;
    }

    private static DxfImportResult Snapshot(DxfImportResult source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Shapes);
        ArgumentNullException.ThrowIfNull(source.Warnings);
        var shapes = source.Shapes.Select<DxfShape, DxfShape>(shape => shape switch
        {
            DxfLine line => line with { },
            DxfCircle circle => circle with { },
            DxfArc arc => arc with { },
            DxfPolyline poly => poly with { Vertices = poly.Vertices.ToArray() },
            DxfBulgedPolyline bulge => bulge with
            {
                Vertices = bulge.Vertices.ToArray(),
                Bulges = bulge.Bulges.ToArray()
            },
            _ => throw new FormatException("Unsupported shape in DXF history.")
        }).ToArray();
        return new DxfImportResult(shapes, source.Warnings.ToArray(), source.InsUnits);
    }
}
