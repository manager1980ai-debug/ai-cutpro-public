namespace AICutPro.Core;

/// <summary>
/// Rigid 2D drafting transforms of an entire, topology-verified LINE/ARC ring set.
/// Arcs remain exact, hole/island ownership is preserved. No CAM approval.
/// </summary>
public static class PreviewRigidTransformEditor
{
    public static PreviewVertexEditResult Translate(
        IReadOnlyList<PreviewPath> rings, double dxMm, double dyMm,
        double toleranceMm = 0.01)
    {
        if (!double.IsFinite(dxMm) || !double.IsFinite(dyMm))
            throw new FormatException("Translation must be finite.");
        return Apply(rings, p => new(p.X + dxMm, p.Y + dyMm),
            a => (a.StartDegrees, a.SweepDegrees),
            toleranceMm);
    }

    public static PreviewVertexEditResult Rotate(
        IReadOnlyList<PreviewPath> rings, PreviewPoint pivot, double degrees,
        double toleranceMm = 0.01)
    {
        CheckPoint(pivot);
        if (!double.IsFinite(degrees) || Math.Abs(degrees) > 360_000)
            throw new FormatException("Invalid rotation.");
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        PreviewPoint Transform(PreviewPoint p)
        {
            var x = p.X - pivot.X;
            var y = p.Y - pivot.Y;
            return new(pivot.X + x*cos - y*sin, pivot.Y + x*sin + y*cos);
        }
        return Apply(rings, Transform, a => (a.StartDegrees + degrees, a.SweepDegrees),
            toleranceMm);
    }

    /// <summary>Reflect around the infinite vertical line x = axisXmm.</summary>
    public static PreviewVertexEditResult MirrorVertical(
        IReadOnlyList<PreviewPath> rings, double axisXmm,
        double toleranceMm = 0.01)
    {
        if (!double.IsFinite(axisXmm) || Math.Abs(axisXmm)>1_000_000)
            throw new FormatException("Invalid mirror axis.");
        return Apply(rings, p => new(2*axisXmm - p.X,p.Y),
            a => (180-a.StartDegrees,-a.SweepDegrees), toleranceMm);
    }

    private static PreviewVertexEditResult Apply(
        IReadOnlyList<PreviewPath> rings,
        Func<PreviewPoint,PreviewPoint> point,
        Func<PreviewArc,(double Start,double Sweep)> arcAngles,
        double toleranceMm)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        var before = PreviewContourTopology.Analyze(rings,toleranceMm);
        if (!before.IsValid)
            throw new FormatException("Source geometry has invalid topology.");
        var transformed = new List<PreviewPath>(rings.Count);
        foreach(var ring in rings)
        {
            if (!ring.Closed) throw new FormatException("Open ring cannot be transformed.");
            var segments = new List<PreviewSegment>();
            foreach (var segment in ring.Segments)
            {
                if (segment is PreviewLine line)
                {
                    var start=point(line.Start);
                    var end=point(line.End);
                    CheckPoint(start); CheckPoint(end);
                    segments.Add(new PreviewLine(start,end));
                }
                else if (segment is PreviewArc arc)
                {
                    var center=point(arc.Center);
                    CheckPoint(center);
                    var angles=arcAngles(arc);
                    if(!double.IsFinite(angles.Start)||!double.IsFinite(angles.Sweep))
                        throw new FormatException("Transformed ARC angle is invalid.");
                    segments.Add(new PreviewArc(center,arc.RadiusMm,
                        angles.Start,angles.Sweep));
                }
                else throw new FormatException("Unsupported transformed segment.");
            }
            transformed.Add(new PreviewPath(segments,true));
        }
        var after = PreviewContourTopology.Analyze(transformed,toleranceMm);
        if (!after.IsValid || after.Rings.Count!=before.Rings.Count)
            throw new FormatException("Rigid transform invalidated geometry.");
        for(var i=0;i<before.Rings.Count;i++)
            if(before.Rings[i].ParentIndex!=after.Rings[i].ParentIndex ||
               before.Rings[i].Depth!=after.Rings[i].Depth ||
               before.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Rigid transform changed hole/island ownership.");
        var areaTolerance=Math.Max(1e-5,Math.Abs(before.NetAreaMm2)*1e-9);
        if(Math.Abs(after.NetAreaMm2-before.NetAreaMm2)>areaTolerance)
            throw new FormatException("Rigid transform changed net area.");
        var dxf = PreviewDxfExporter.Export(transformed,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(transformed,dxf,toleranceMm);
        return new(transformed,after.NetAreaMm2,true);
    }

    private static void CheckPoint(PreviewPoint p)
    {
        if (!double.IsFinite(p.X)||!double.IsFinite(p.Y)||
            Math.Abs(p.X)>1_000_000||Math.Abs(p.Y)>1_000_000)
            throw new FormatException("Transformed point outside supported CAD range.");
    }
}
