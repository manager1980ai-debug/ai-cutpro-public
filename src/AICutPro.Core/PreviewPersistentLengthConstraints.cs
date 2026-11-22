namespace AICutPro.Core;

/// <summary>
/// Bounded, preview-only persistent length constraints for indexed LINE edges.
/// A length relation is a scalar distance between the two endpoints. This
/// feature is not a general nonlinear CAD solver or a CAM authorization.
/// </summary>
public sealed record PreviewLengthRule(int RingIndex,int SegmentIndex,double LengthMm);

public static class PreviewPersistentLengthConstraints
{
    public const int MaximumRules=16;

    private static double Distance(PreviewPoint a,PreviewPoint b)=>
        Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));

    public static IReadOnlyList<PreviewLengthRule> Add(
        IReadOnlyList<PreviewPath> rings,IReadOnlyList<PreviewLengthRule> rules,
        int ringIndex,int segmentIndex,double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var edge=Edge(rings,ringIndex,segmentIndex,toleranceMm);
        var length=Distance(edge.Start,edge.End);
        if(!double.IsFinite(length)||length<=toleranceMm)
            throw new FormatException("Cannot pin a degenerate length.");
        if(rules.Any(r=>r.RingIndex==ringIndex&&r.SegmentIndex==segmentIndex))
            throw new FormatException("Length already pinned on this edge.");
        if(rules.Count>=MaximumRules)
            throw new FormatException("Too many pinned edge lengths.");
        var updated=rules.Append(new PreviewLengthRule(ringIndex,segmentIndex,length)).ToArray();
        Verify(rings,updated,toleranceMm);
        return updated;
    }

    public static IReadOnlyList<PreviewLengthRule> Remove(
        IReadOnlyList<PreviewLengthRule> rules,int ringIndex,int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if(!rules.Any(r=>r.RingIndex==ringIndex&&r.SegmentIndex==segmentIndex))
            throw new FormatException("No length rule on selected edge.");
        return rules.Where(r=>r.RingIndex!=ringIndex||
                              r.SegmentIndex!=segmentIndex).ToArray();
    }

    public static void Verify(
        IReadOnlyList<PreviewPath> rings,IReadOnlyList<PreviewLengthRule> rules,
        double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if(rules.Count>MaximumRules)
            throw new FormatException("Too many length rules.");
        var used=new HashSet<(int,int)>();
        foreach(var rule in rules)
        {
            if(!used.Add((rule.RingIndex,rule.SegmentIndex)))
                throw new FormatException("Duplicate length rule.");
            if(!double.IsFinite(rule.LengthMm)||rule.LengthMm<=toleranceMm||
               rule.LengthMm>1_000_000)
                throw new FormatException("Invalid pinned edge length.");
            var line=Edge(rings,rule.RingIndex,rule.SegmentIndex,toleranceMm);
            var length=Distance(line.Start,line.End);
            if(Math.Abs(length-rule.LengthMm)>Math.Max(1e-7,toleranceMm*0.001))
                throw new FormatException("Persistent edge length violated.");
        }
    }

    public static void RequireCompatibleEdit(
        IReadOnlyList<PreviewPath> before,IReadOnlyList<PreviewPath> after,
        IReadOnlyList<PreviewLengthRule> rules,double toleranceMm=0.01)
    {
        Verify(before,rules,toleranceMm);
        foreach(var rule in rules)
            if(rule.RingIndex>=after.Count||
               before[rule.RingIndex].Segments.Count!=
                   after[rule.RingIndex].Segments.Count)
                throw new FormatException("Edit changes pinned segment identifiers.");
        Verify(after,rules,toleranceMm);
    }

    /// <summary>
    /// Move a vertex around the fixed other end of its one pinned adjacent
    /// edge, choosing the unique closest point on the required circle. Other
    /// vertices are not moved. Two adjacent length pins are intentionally
    /// refused rather than resolved by an ambiguous circle intersection.
    /// </summary>
    public static PreviewVertexEditResult MoveVertexOnPinnedLength(
        IReadOnlyList<PreviewPath> rings,int ringIndex,int vertexIndex,
        PreviewPoint requested,IReadOnlyList<PreviewLengthRule> lengths,
        IReadOnlyList<PreviewAxisRule> axes,double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentNullException.ThrowIfNull(axes);
        if(!double.IsFinite(requested.X)||!double.IsFinite(requested.Y)||
           Math.Abs(requested.X)>1_000_000||Math.Abs(requested.Y)>1_000_000)
            throw new FormatException("Invalid target position.");
        PreviewPersistentAxisConstraints.Verify(rings,axes,toleranceMm);
        Verify(rings,lengths,toleranceMm);
        if(ringIndex<0||ringIndex>=rings.Count)
            throw new FormatException("Invalid ring index.");
        var ring=rings[ringIndex];
        var n=ring.Segments.Count;
        if(!ring.Closed||n is <3 or >2000||
           ring.Segments.Any(s=>s is not PreviewLine)||
           vertexIndex<0||vertexIndex>=n)
            throw new FormatException("Length-constrained vertex requires a LINE-only ring.");
        var previous=(vertexIndex+n-1)%n;
        var touching=lengths.Where(r=>r.RingIndex==ringIndex&&
            (r.SegmentIndex==previous||r.SegmentIndex==vertexIndex)).ToArray();
        if(touching.Length!=1)
            throw new FormatException("Exactly one pinned adjacent length is required.");
        var rule=touching[0];
        var edge=(PreviewLine)ring.Segments[rule.SegmentIndex];
        var anchor=rule.SegmentIndex==vertexIndex?edge.End:edge.Start;
        var dx=requested.X-anchor.X;
        var dy=requested.Y-anchor.Y;
        var distance=Math.Sqrt(dx*dx+dy*dy);
        if(!double.IsFinite(distance)||distance<=toleranceMm)
            throw new FormatException("Requested point coincides with pinned edge anchor.");
        var point=new PreviewPoint(
            anchor.X+rule.LengthMm*dx/distance,
            anchor.Y+rule.LengthMm*dy/distance);
        if(Math.Abs(point.X)>1_000_000||Math.Abs(point.Y)>1_000_000)
            throw new FormatException("Projected point exceeds supported CAD bounds.");
        var edited=PreviewVertexEditor.MoveVertex(
            rings,ringIndex,vertexIndex,point,toleranceMm);
        PreviewPersistentAxisConstraints.RequireCompatibleEdit(
            rings,edited.Rings,axes,toleranceMm);
        RequireCompatibleEdit(rings,edited.Rings,lengths,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(
            edited.Rings,PreviewDxfExporter.Export(edited.Rings,toleranceMm),
            toleranceMm);
        return edited;
    }

    private static PreviewLine Edge(
        IReadOnlyList<PreviewPath> rings,int ringIndex,int segmentIndex,double toleranceMm)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if(!double.IsFinite(toleranceMm)||toleranceMm<0.0001||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if(rings.Count is <1 or >1000||ringIndex<0||ringIndex>=rings.Count)
            throw new FormatException("Length rule ring does not exist.");
        var ring=rings[ringIndex];
        if(!ring.Closed||ring.Segments.Count is <3 or >2000||
           ring.Segments.Any(s=>s is not PreviewLine)||
           segmentIndex<0||segmentIndex>=ring.Segments.Count)
            throw new FormatException("Length rule requires an indexed LINE-only contour.");
        return (PreviewLine)ring.Segments[segmentIndex];
    }
}
