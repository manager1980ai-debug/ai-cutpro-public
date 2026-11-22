namespace AICutPro.Core;

/// <summary>
/// Limited persistent axis relations for indexed edges of closed LINE-only
/// rings. Editing an existing edge must preserve its axis by re-solving the
/// edited geometry, or fail without changing a snapshot. Not a general CAD
/// constraint graph; no machining approval.
/// </summary>
public sealed record PreviewAxisRule(int RingIndex, int SegmentIndex, bool Horizontal);

public static class PreviewPersistentAxisConstraints
{
    public const int MaximumRules=16;

    public static IReadOnlyList<PreviewAxisRule> Add(
        IReadOnlyList<PreviewPath> rings,
        IReadOnlyList<PreviewAxisRule> existing,
        PreviewAxisRule rule, double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ValidateRule(rings,rule,toleranceMm);
        if (existing.Any(x=>x.RingIndex==rule.RingIndex &&
                            x.SegmentIndex==rule.SegmentIndex &&
                            x.Horizontal!=rule.Horizontal))
            throw new FormatException("Conflicting axis constraints on the same edge.");
        if(existing.Count>=MaximumRules &&
           !existing.Contains(rule))
            throw new FormatException("Too many persistent axis rules.");
        var updated=existing.Append(rule).Distinct().ToArray();
        // Registering a relation cannot silently rotate the geometry.
        // User must first align it with the one-shot axis tool.
        Verify(rings,updated,toleranceMm);
        return updated;
    }

    public static IReadOnlyList<PreviewAxisRule> Remove(
        IReadOnlyList<PreviewAxisRule> existing, PreviewAxisRule rule)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if(!existing.Contains(rule))throw new FormatException("Axis rule does not exist.");
        return existing.Where(x=>x!=rule).ToArray();
    }

    public static void Verify(
        IReadOnlyList<PreviewPath> rings,
        IReadOnlyList<PreviewAxisRule> rules,double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(rules);
        if(!double.IsFinite(toleranceMm)||toleranceMm<0.0001||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if(rules.Count>MaximumRules)throw new FormatException("Too many axis rules.");
        if(rules.Distinct().Count()!=rules.Count)
            throw new FormatException("Duplicate persistent axis rules.");
        foreach(var rule in rules)
        {
            ValidateRule(rings,rule,toleranceMm);
            var line=(PreviewLine)rings[rule.RingIndex].Segments[rule.SegmentIndex];
            var discrepancy=rule.Horizontal?Math.Abs(line.End.Y-line.Start.Y):
                                            Math.Abs(line.End.X-line.Start.X);
            if(discrepancy>1e-7)
                throw new FormatException("Persistent axis relation violated.");
        }
    }

    /// <summary>
    /// Refuse an edit that violates any pinned relation. A generic edit is not
    /// allowed to guess how a constrained drawing should reshape: the user
    /// must apply a compatible geometric edit or explicitly remove the rule.
    /// </summary>
    public static void RequireCompatibleEdit(
        IReadOnlyList<PreviewPath> before,IReadOnlyList<PreviewPath> after,
        IReadOnlyList<PreviewAxisRule> rules,double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Verify(before,rules,toleranceMm);
        foreach(var rule in rules)
        {
            if(rule.RingIndex>=after.Count ||
               after[rule.RingIndex].Segments.Count!=
                   before[rule.RingIndex].Segments.Count)
                throw new FormatException("Edit changes segment identifiers used by axis constraints.");
        }
        Verify(after,rules,toleranceMm);
    }

    /// <summary>
    /// Project a requested vertex position onto every axis relation touching
    /// that vertex. Other vertices remain fixed; contradictory coordinates
    /// fail closed. Only the selected vertex can move, so no global solver
    /// or hidden modification of neighbouring dimensions is implied.
    /// </summary>
    public static PreviewVertexEditResult MoveVertexConstrained(
        IReadOnlyList<PreviewPath> rings,int ringIndex,int vertexIndex,
        PreviewPoint requested,IReadOnlyList<PreviewAxisRule> rules,
        double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(rules);
        if(!double.IsFinite(requested.X)||!double.IsFinite(requested.Y)||
           Math.Abs(requested.X)>1_000_000||Math.Abs(requested.Y)>1_000_000)
            throw new FormatException("Invalid requested vertex coordinates.");
        Verify(rings,rules,toleranceMm);
        if(ringIndex<0||ringIndex>=rings.Count)
            throw new FormatException("Invalid constrained vertex ring.");
        var path=rings[ringIndex];
        var count=path.Segments.Count;
        if(!path.Closed||count is <3 or >2000||
           path.Segments.Any(x=>x is not PreviewLine)||
           vertexIndex<0||vertexIndex>=count)
            throw new FormatException("Constrained vertex requires closed LINE-only ring.");

        double? pinnedX=null,pinnedY=null;
        var prev=(vertexIndex+count-1)%count;
        foreach(var rule in rules)
        {
            if(rule.RingIndex!=ringIndex ||
               (rule.SegmentIndex!=vertexIndex && rule.SegmentIndex!=prev))
                continue;
            var other=rule.SegmentIndex==vertexIndex
                ? (PreviewLine)path.Segments[(vertexIndex+1)%count]
                : (PreviewLine)path.Segments[prev];
            var otherPoint=rule.SegmentIndex==vertexIndex
                ? other.Start : other.Start;
            // The opposite vertex is the outgoing edge's end, or the
            // incoming edge's start. Both are fixed during this edit.
            if(rule.SegmentIndex==vertexIndex)
                otherPoint=((PreviewLine)path.Segments[vertexIndex]).End;
            var value=rule.Horizontal?otherPoint.Y:otherPoint.X;
            if(rule.Horizontal)
            {
                if(pinnedY is double current && Math.Abs(current-value)>toleranceMm*0.01)
                    throw new FormatException("Conflicting horizontal relations at vertex.");
                pinnedY=value;
            }
            else
            {
                if(pinnedX is double current && Math.Abs(current-value)>toleranceMm*0.01)
                    throw new FormatException("Conflicting vertical relations at vertex.");
                pinnedX=value;
            }
        }
        var projected=new PreviewPoint(pinnedX??requested.X,pinnedY??requested.Y);
        var edited=PreviewVertexEditor.MoveVertex(
            rings,ringIndex,vertexIndex,projected,toleranceMm);
        RequireCompatibleEdit(rings,edited.Rings,rules,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(
            edited.Rings,PreviewDxfExporter.Export(edited.Rings,toleranceMm),
            toleranceMm);
        return edited;
    }

    private static void ValidateRule(
        IReadOnlyList<PreviewPath> rings,PreviewAxisRule rule,double toleranceMm)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if(rings.Count is <1 or >1000 ||
           rule.RingIndex<0 || rule.RingIndex>=rings.Count)
            throw new FormatException("Axis constraint ring no longer exists.");
        var ring=rings[rule.RingIndex];
        if(!ring.Closed || ring.Segments.Count is <3 or >2000 ||
           ring.Segments.Any(s=>s is not PreviewLine) ||
           rule.SegmentIndex<0 || rule.SegmentIndex>=ring.Segments.Count)
            throw new FormatException("Persistent axis rule must target an indexed LINE-only contour.");
        if(!double.IsFinite(toleranceMm)||toleranceMm<0.0001||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
    }
}
