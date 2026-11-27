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
    private readonly List<IReadOnlyList<PreviewLengthRule>> _lengthRules = [];
    private readonly List<CutProjectDxfWorkspace?> _workspaces = [];
    private int _index;

    public DxfPreviewHistory(DxfImportResult initial, CutProjectDxfWorkspace? workspace = null)
    {
        _states.Add(Snapshot(initial));
        _axisRules.Add([]);
        _lengthRules.Add([]);
        _workspaces.Add(workspace);
    }

    public CutProjectDxfWorkspace? CurrentWorkspace => _workspaces[_index];

    public void UpdateCurrentWorkspace(CutProjectDxfWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        // Opening and closing the editor without changes must not discard redo.
        if (CurrentWorkspace == workspace)
            return;
        _workspaces[_index] = workspace;
        if (CanRedo)
        {
            _states.RemoveRange(_index+1,_states.Count-1-_index);
            _axisRules.RemoveRange(_index+1,_axisRules.Count-1-_index);
            _lengthRules.RemoveRange(_index+1,_lengthRules.Count-1-_index);
            _workspaces.RemoveRange(_index+1,_workspaces.Count-1-_index);
        }
    }

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _states.Count - 1;
    public int UndoCount => _index;
    public int RedoCount => _states.Count - 1 - _index;
    public DxfImportResult Current => Snapshot(_states[_index]);
    public IReadOnlyList<PreviewAxisRule> CurrentAxisRules =>
        _axisRules[_index].ToArray();
    public IReadOnlyList<PreviewLengthRule> CurrentLengthRules =>
        _lengthRules[_index].ToArray();

    /// <summary>Call only after an edit has passed geometry and DXF verification.</summary>
    public void Push(DxfImportResult verifiedEdit) =>
        Push(verifiedEdit,CurrentAxisRules,CurrentLengthRules);

    /// <summary>
    /// Save geometry and persistent drafting relations in one Undo/Redo step.
    /// Caller must verify both geometry and axis rules before committing.
    /// </summary>
    public void Push(DxfImportResult verifiedEdit,
                     IReadOnlyList<PreviewAxisRule> rules) =>
        Push(verifiedEdit,rules,CurrentLengthRules);

    public void Push(DxfImportResult verifiedEdit,
                     IReadOnlyList<PreviewAxisRule> rules,
                     IReadOnlyList<PreviewLengthRule> lengths) =>
        Push(verifiedEdit, rules, lengths, CurrentWorkspace);

    public void Push(DxfImportResult verifiedEdit,
                     IReadOnlyList<PreviewAxisRule> rules,
                     IReadOnlyList<PreviewLengthRule> lengths,
                     CutProjectDxfWorkspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(lengths);
        var clone=Snapshot(verifiedEdit);
        if(rules.Count>PreviewPersistentAxisConstraints.MaximumRules)
            throw new FormatException("Too many persistent axis constraints.");
        if(lengths.Count>PreviewPersistentLengthConstraints.MaximumRules)
            throw new FormatException("Too many persistent length constraints.");
        if(_index < _states.Count-1)
        {
            _states.RemoveRange(_index+1,_states.Count-1-_index);
            _axisRules.RemoveRange(_index+1,_axisRules.Count-1-_index);
            _lengthRules.RemoveRange(_index+1,_lengthRules.Count-1-_index);
            _workspaces.RemoveRange(_index+1,_workspaces.Count-1-_index);
        }
        _states.Add(clone);
        _axisRules.Add(rules.ToArray());
        _lengthRules.Add(lengths.ToArray());
        _workspaces.Add(workspace);
        if(_states.Count>MaximumStates)
        {
            _states.RemoveAt(0);
            _axisRules.RemoveAt(0);
            _lengthRules.RemoveAt(0);
            _workspaces.RemoveAt(0);
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
