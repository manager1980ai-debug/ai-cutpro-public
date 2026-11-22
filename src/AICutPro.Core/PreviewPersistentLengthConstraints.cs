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
    /// Move a single vertex while preserving one or two adjacent pinned
    /// lengths. A single pin projects onto a circle; two pins intersect two
    /// circles analytically, trying the nearest valid branch first.
    /// Other vertices stay fixed; topology and DXF validation are mandatory.
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
        if(touching.Length is <1 or >2)
            throw new FormatException("One or two pinned adjacent lengths are required.");

        var candidates=new List<PreviewPoint>();
        if(touching.Length==1)
        {
            var rule=touching[0];
            var edge=(PreviewLine)ring.Segments[rule.SegmentIndex];
            var anchor=rule.SegmentIndex==vertexIndex?edge.End:edge.Start;
            var dx=requested.X-anchor.X;
            var dy=requested.Y-anchor.Y;
            var distance=Math.Sqrt(dx*dx+dy*dy);
            if(!double.IsFinite(distance)||distance<=toleranceMm)
                throw new FormatException("Requested point coincides with pinned edge anchor.");
            candidates.Add(new PreviewPoint(
                anchor.X+rule.LengthMm*dx/distance,
                anchor.Y+rule.LengthMm*dy/distance));
        }
        else
        {
            // Consecutive edges share exactly the moved vertex. Intersect
            // circles centred at their fixed opposite endpoints. A tangent
            // intersection has one candidate, otherwise two.
            PreviewPoint Anchor(PreviewLengthRule rule)
            {
                var edge=(PreviewLine)ring.Segments[rule.SegmentIndex];
                return rule.SegmentIndex==vertexIndex?edge.End:edge.Start;
            }
            var a=Anchor(touching[0]);
            var b=Anchor(touching[1]);
            var r1=touching[0].LengthMm;
            var r2=touching[1].LengthMm;
            var dx=b.X-a.X;
            var dy=b.Y-a.Y;
            var d=Math.Sqrt(dx*dx+dy*dy);
            if(!double.IsFinite(d)||d<=toleranceMm)
                throw new FormatException("Two pinned lengths have coincident anchors.");
            if(d>r1+r2+toleranceMm*0.001||
               d<Math.Abs(r1-r2)-toleranceMm*0.001)
                throw new FormatException("Pinned length circles do not intersect.");
            var along=(r1*r1-r2*r2+d*d)/(2*d);
            var heightSq=r1*r1-along*along;
            if(heightSq<-(toleranceMm*toleranceMm))
                throw new FormatException("Pinned length circles have no real intersection.");
            var height=Math.Sqrt(Math.Max(0,heightSq));
            var ux=dx/d;
            var uy=dy/d;
            var baseX=a.X+along*ux;
            var baseY=a.Y+along*uy;
            candidates.Add(new PreviewPoint(baseX-height*uy,baseY+height*ux));
            if(height>toleranceMm*0.001)
                candidates.Add(new PreviewPoint(baseX+height*uy,baseY-height*ux));
        }
        var ordered=candidates.OrderBy(point=>
            Math.Pow(point.X-requested.X,2)+Math.Pow(point.Y-requested.Y,2));
        foreach(var point in ordered)
        {
            if(!double.IsFinite(point.X)||!double.IsFinite(point.Y)||
               Math.Abs(point.X)>1_000_000||Math.Abs(point.Y)>1_000_000)
                continue;
            try
            {
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
            catch(FormatException)
            {
                // Try the other mathematical branch only if it also passes
                // the entire topology, axis, length and DXF gate.
            }
        }
        throw new FormatException("No topology-valid intersection of pinned length circles.");
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
