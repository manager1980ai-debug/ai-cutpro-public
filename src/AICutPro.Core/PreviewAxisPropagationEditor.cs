namespace AICutPro.Core;

/// <summary>
/// Bounded preview-only propagation of one vertex edit through persistent
/// horizontal/vertical equality constraints on the same LINE-only ring.
/// Each axis is a separate undirected graph: horizontal edges link Y,
/// vertical edges link X. No arc distortion, cross-ring solver or CAM.
/// </summary>
public static class PreviewAxisPropagationEditor
{
    public static PreviewVertexEditResult MoveConnectedVertices(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int vertexIndex,
        PreviewPoint requested, IReadOnlyList<PreviewAxisRule> rules,
        double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(rules);
        if(!double.IsFinite(toleranceMm)||toleranceMm<0.0001||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if(!double.IsFinite(requested.X)||!double.IsFinite(requested.Y)||
           Math.Abs(requested.X)>1_000_000||Math.Abs(requested.Y)>1_000_000)
            throw new FormatException("Invalid requested vertex coordinates.");
        PreviewPersistentAxisConstraints.Verify(rings,rules,toleranceMm);
        var before=PreviewContourTopology.Analyze(rings,toleranceMm);
        if(!before.IsValid)
            throw new FormatException("Source contour topology is invalid.");
        if(ringIndex<0||ringIndex>=rings.Count)
            throw new FormatException("Invalid connected vertex ring.");
        var ring=rings[ringIndex];
        int count=ring.Segments.Count;
        if(!ring.Closed||count is <3 or >2000||
           ring.Segments.Any(x=>x is not PreviewLine)||
           vertexIndex<0||vertexIndex>=count)
            throw new FormatException("Propagation requires an indexed closed LINE-only ring.");
        var lines=ring.Segments.Cast<PreviewLine>().ToArray();
        var vertices=lines.Select(x=>x.Start).ToArray();
        for(int i=0;i<count;i++)
        {
            var end=lines[i].End;
            var next=vertices[(i+1)%count];
            if(Math.Sqrt(Math.Pow(end.X-next.X,2)+Math.Pow(end.Y-next.Y,2))>toleranceMm)
                throw new FormatException("Disconnected LINE before constraint propagation.");
        }

        // Separately find connected components of equal X and equal Y.
        // The requested vertex is the only driver; each reached component
        // receives its requested coordinate and no other axis is touched.
        bool[] Reach(bool horizontal)
        {
            var reached=new bool[count];
            var queue=new Queue<int>();
            queue.Enqueue(vertexIndex);
            reached[vertexIndex]=true;
            while(queue.Count>0)
            {
                var current=queue.Dequeue();
                foreach(var rule in rules)
                {
                    if(rule.RingIndex!=ringIndex||rule.Horizontal!=horizontal)continue;
                    var a=rule.SegmentIndex;
                    var b=(a+1)%count;
                    var neighbor=current==a?b:current==b?a:-1;
                    if(neighbor>=0&&!reached[neighbor])
                    {
                        reached[neighbor]=true;
                        queue.Enqueue(neighbor);
                    }
                }
            }
            return reached;
        }

        var xReach=Reach(false);
        var yReach=Reach(true);
        var updatedVertices=new PreviewPoint[count];
        for(int i=0;i<count;i++)
        {
            var x=xReach[i]?requested.X:vertices[i].X;
            var y=yReach[i]?requested.Y:vertices[i].Y;
            if(!double.IsFinite(x)||!double.IsFinite(y)||
               Math.Abs(x)>1_000_000||Math.Abs(y)>1_000_000)
                throw new FormatException("Propagated coordinate out of CAD bounds.");
            updatedVertices[i]=new PreviewPoint(x,y);
        }

        var segments=new List<PreviewSegment>(count);
        for(int i=0;i<count;i++)
        {
            var start=updatedVertices[i];
            var end=updatedVertices[(i+1)%count];
            var length=Math.Sqrt(Math.Pow(end.X-start.X,2)+Math.Pow(end.Y-start.Y,2));
            if(!double.IsFinite(length)||length<=toleranceMm)
                throw new FormatException("Propagation collapses an edge.");
            segments.Add(new PreviewLine(start,end));
        }
        var updated=rings.ToArray();
        updated[ringIndex]=new PreviewPath(segments,true);
        PreviewPersistentAxisConstraints.RequireCompatibleEdit(
            rings,updated,rules,toleranceMm);
        var after=PreviewContourTopology.Analyze(updated,toleranceMm);
        if(!after.IsValid||after.Rings.Count!=before.Rings.Count)
            throw new FormatException("Propagation invalidates contour topology: "+
                                      string.Join("; ",after.Issues));
        for(int i=0;i<before.Rings.Count;i++)
            if(before.Rings[i].ParentIndex!=after.Rings[i].ParentIndex||
               before.Rings[i].Depth!=after.Rings[i].Depth||
               before.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Propagation changes hole or island ownership.");
        PreviewDxfRoundTripVerifier.Verify(
            updated,PreviewDxfExporter.Export(updated,toleranceMm),toleranceMm);
        return new(updated,after.NetAreaMm2,true);
    }
}
