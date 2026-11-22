namespace AICutPro.Core;

/// <summary>All coordinates are millimetres in a simulated machine. No physical motion APIs exist here.</summary>
public sealed record MachinePoint(double X, double Y, double Z);

public sealed record SheetCheckpoint(string Name, MachinePoint Position);

public sealed record VirtualMachine(WorkspaceGeometry Workspace, MachinePoint Head, MachinePoint WorkZero)
{
    public static VirtualMachine Create(WorkspaceGeometry workspace)
    {
        if (workspace.Validate().Count > 0) throw new ArgumentException("Invalid workspace.", nameof(workspace));
        var origin = new MachinePoint(0, 0, 0);
        return new VirtualMachine(workspace, origin, origin);
    }

    /// <summary>Simulated machine coordinates, measured from the table origin.</summary>
    public MachinePoint MachineCoordinates => Head;

    /// <summary>Simulated work coordinates after an explicitly chosen working zero.</summary>
    public MachinePoint WorkCoordinates =>
        new(Head.X - WorkZero.X, Head.Y - WorkZero.Y, Head.Z - WorkZero.Z);

    public IReadOnlyList<SheetCheckpoint> Checkpoints =>
    [
        new("Нижний левый", new MachinePoint(0, 0, 0)),
        new("Нижний правый", new MachinePoint(Workspace.MaterialSheet.WidthMm, 0, 0)),
        new("Верхний правый", new MachinePoint(Workspace.MaterialSheet.WidthMm, Workspace.MaterialSheet.HeightMm, 0)),
        new("Верхний левый", new MachinePoint(0, Workspace.MaterialSheet.HeightMm, 0))
    ];

    public VirtualMachine MoveVirtual(MachinePoint target)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.Z) ||
            target.X < 0 || target.Y < 0 || target.Z < 0 ||
            target.X > Workspace.Table.WidthMm || target.Y > Workspace.Table.HeightMm ||
            target.Z > 1000)
            throw new ArgumentOutOfRangeException(nameof(target), "Virtual head must stay within the configured table and simulated Z range 0..1000 mm.");
        return this with { Head = target };
    }

    public VirtualMachine SetWorkZeroAtHead() => this with { WorkZero = Head };
}
