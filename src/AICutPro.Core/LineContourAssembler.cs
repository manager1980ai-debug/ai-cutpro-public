namespace AICutPro.Core;

public sealed record LineContourAssembly(
    IReadOnlyList<DxfPolyline> ClosedContours,
    IReadOnlyList<string> Warnings,
    bool Complete);

/// <summary>
/// Conservative inspection-only topology builder for LINE entities.
/// Only accepts exact endpoints within a configurable tolerance, simple degree-two rings,
/// no duplicate/zero-length segments, no branching, no ambiguous endpoint clustering.
/// Does not approximate or join ARC entities.
/// </summary>
public static class LineContourAssembler
{
    private static double DistanceSquared(Point2 a, Point2 b)
        => (a.X-b.X)*(a.X-b.X) + (a.Y-b.Y)*(a.Y-b.Y);

    public static LineContourAssembly Assemble(IReadOnlyList<DxfLine> lines, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!double.IsFinite(toleranceMm) || toleranceMm <= 0 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), "Tolerance must be between 0 and 1 mm.");
        if (lines.Count > 10000) return new([], ["Too many LINE elements for bounded contour analysis."], false);
        if (lines.Count == 0) return new([], [], true);

        var warnings = new List<string>();
        var output = new List<DxfPolyline>();
        foreach (var group in lines.GroupBy(x => x.Layer, StringComparer.Ordinal))
        {
            var edges = group.ToArray();
            var nodes = new List<Point2>();
            var endpoints = new (int A, int B)[edges.Length];
            var tolSq = toleranceMm*toleranceMm;
            int FindNode(Point2 p)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                    throw new FormatException("Nonfinite LINE coordinate.");
                int found = -1;
                for (int i = 0; i < nodes.Count; i++)
                    if (DistanceSquared(p,nodes[i]) <= tolSq)
                    {
                        if (found != -1)
                            throw new FormatException("Ambiguous near-coincident endpoints; reduce tolerance.");
                        found = i;
                    }
                if (found >= 0) return found;
                nodes.Add(p);
                return nodes.Count-1;
            }

            try
            {
                for (int i=0;i<edges.Length;i++)
                {
                    if (DistanceSquared(edges[i].Start,edges[i].End) <= tolSq)
                        throw new FormatException("Zero-length or sub-tolerance LINE.");
                    var a = FindNode(edges[i].Start);
                    var b = FindNode(edges[i].End);
                    if (a == b) throw new FormatException("Collapsed LINE endpoints.");
                    endpoints[i] = (a,b);
                }
                // All graph vertices must be used by exactly two different edges.
                var incident = Enumerable.Range(0,nodes.Count).Select(_ => new List<int>()).ToArray();
                for (int i=0;i<endpoints.Length;i++)
                {
                    incident[endpoints[i].A].Add(i);
                    incident[endpoints[i].B].Add(i);
                }
                if (incident.Any(x=>x.Count != 2))
                    throw new FormatException("Open endpoint or branching LINE intersection.");
                var used = new bool[edges.Length];
                for(int start=0;start<edges.Length;start++)
                {
                    if(used[start])continue;
                    var currentNode=endpoints[start].A;
                    var firstNode=currentNode;
                    var currentEdge=start;
                    var ring=new List<Point2>();
                    while(true)
                    {
                        if(used[currentEdge])
                            throw new FormatException("Repeated edge or non-simple contour.");
                        used[currentEdge]=true;
                        ring.Add(nodes[currentNode]);
                        var e=endpoints[currentEdge];
                        var next=e.A==currentNode?e.B:e.A;
                        if(next==firstNode)break;
                        var choices=incident[next].Where(id=>!used[id]).ToArray();
                        if(choices.Length!=1)throw new FormatException("Broken or ambiguous ring.");
                        currentNode=next;
                        currentEdge=choices[0];
                        if(ring.Count>edges.Length)
                            throw new FormatException("Invalid graph cycle.");
                    }
                    if(ring.Count < 3)throw new FormatException("Contour has fewer than three edges.");
                    output.Add(new DxfPolyline(group.Key,ring,true));
                }
            }
            catch(FormatException ex)
            {
                warnings.Add($"Layer {group.Key}: {ex.Message}");
                return new([],warnings,false);
            }
        }
        return new(output,warnings,true);
    }
}
