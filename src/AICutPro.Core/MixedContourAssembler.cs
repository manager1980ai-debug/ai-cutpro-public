namespace AICutPro.Core;

/// <summary>A topological, display-only result. Not a validated CAD profile or CAM toolpath.</summary>
public sealed record MixedContourAssembly(
    IReadOnlyList<PreviewPath> ClosedPaths,
    IReadOnlyList<string> Warnings,
    bool Complete);

/// <summary>
/// Joins explicit DXF LINE and ARC segments by their analytical endpoints, without replacing
/// arcs by line chords. Strictly rejects open, branched, duplicate, or ambiguous endpoint graphs.
/// Does not certify intersections, offsets, nested holes, kerf or machine safety.
/// </summary>
public static class MixedContourAssembler
{
    private readonly record struct Edge(PreviewSegment Segment, PreviewPoint Start, PreviewPoint End);
    private static double Dist2(PreviewPoint a, PreviewPoint b) =>
        (a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y);
    private static bool Finite(double n) => double.IsFinite(n);
    private static PreviewPoint PointOn(DxfArc arc, double angle)
    {
        var r = angle*Math.PI/180d;
        return new(arc.Center.X + arc.Radius*Math.Cos(r),
                   arc.Center.Y + arc.Radius*Math.Sin(r));
    }
    private static double Normalize(double d) => ((d%360)+360)%360;

    public static MixedContourAssembly Assemble(IReadOnlyList<DxfShape> shapes,
        double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        if (!Finite(toleranceMm) || toleranceMm <= 0 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        var relevant=shapes.Where(s=>s is DxfLine or DxfArc or DxfBulgedPolyline or DxfCircle or DxfPolyline).ToArray();
        if(relevant.Length>10000)
            return new([],["Too many segments for bounded contour inspection."],false);
        if(relevant.Length==0) return new([],[],true);

        var paths = new List<PreviewPath>();
        var warnings = new List<string>();
        var tol2=toleranceMm*toleranceMm;
        foreach(var group in relevant.GroupBy(s=>s.Layer,StringComparer.Ordinal))
        {
            try
            {
                var edges = new List<Edge>();
                foreach(var shape in group)
                {
                    if(shape is DxfLine line)
                    {
                        var a=new PreviewPoint(line.Start.X,line.Start.Y);
                        var b=new PreviewPoint(line.End.X,line.End.Y);
                        if(!Finite(a.X)||!Finite(a.Y)||!Finite(b.X)||!Finite(b.Y)||
                           Dist2(a,b)<=tol2)
                            throw new FormatException("Invalid or degenerate LINE.");
                        edges.Add(new(new PreviewLine(a,b),a,b));
                    }
                    else if (shape is DxfPolyline straight)
                    {
                        if (!straight.Closed || straight.Vertices.Count < 3 ||
                            straight.Vertices.Count > 10000 ||
                            edges.Count + straight.Vertices.Count > 10000)
                            throw new FormatException("Open, degenerate, or oversized LWPOLYLINE.");
                        for (int k=0;k<straight.Vertices.Count;k++)
                        {
                            var p=straight.Vertices[k];
                            var q=straight.Vertices[(k+1)%straight.Vertices.Count];
                            var start=new PreviewPoint(p.X,p.Y);
                            var end=new PreviewPoint(q.X,q.Y);
                            if(!Finite(start.X)||!Finite(start.Y)||!Finite(end.X)||!Finite(end.Y)||
                               Dist2(start,end)<=tol2)
                                throw new FormatException("Invalid or degenerate LWPOLYLINE edge.");
                            edges.Add(new(new PreviewLine(start,end),start,end));
                        }
                    }
                    else if (shape is DxfBulgedPolyline curved)
                    {
                        // Preserve analytic arcs; this result is display-only and not certified CAD/CAM.
                        var curve = DxfBulgePreview.Build(curved);
                        if (edges.Count + curve.Segments.Count > 10000)
                            throw new FormatException("Too many polyline segments for preview.");
                        foreach (var segment in curve.Segments)
                        {
                            PreviewPoint start, end;
                            if (segment is PreviewLine linePart)
                            {
                                start = linePart.Start;
                                end = linePart.End;
                            }
                            else if (segment is PreviewArc arcPart)
                            {
                                if (!Finite(arcPart.SweepDegrees) ||
                                    Math.Abs(arcPart.SweepDegrees) >= 360)
                                    throw new FormatException("Invalid bulge sweep.");
                                var a = arcPart.StartDegrees*Math.PI/180;
                                var b = (arcPart.StartDegrees+arcPart.SweepDegrees)*Math.PI/180;
                                start = new PreviewPoint(
                                    arcPart.Center.X+arcPart.RadiusMm*Math.Cos(a),
                                    arcPart.Center.Y+arcPart.RadiusMm*Math.Sin(a));
                                end = new PreviewPoint(
                                    arcPart.Center.X+arcPart.RadiusMm*Math.Cos(b),
                                    arcPart.Center.Y+arcPart.RadiusMm*Math.Sin(b));
                            }
                            else throw new FormatException("Unsupported polyline segment.");
                            if (!Finite(start.X)||!Finite(start.Y)||!Finite(end.X)||!Finite(end.Y)||
                                Dist2(start,end)<=tol2)
                                throw new FormatException("Invalid or degenerate bulge segment.");
                            edges.Add(new Edge(segment,start,end));
                        }
                    }
                    else if(shape is DxfCircle circle)
                    {
                        if (!Finite(circle.Center.X) || !Finite(circle.Center.Y) ||
                            !Finite(circle.Radius) || circle.Radius <= toleranceMm)
                            throw new FormatException("Invalid CIRCLE.");
                        if (edges.Count + 4 > 10000)
                            throw new FormatException("Too many analytic circle segments for preview.");
                        // Four analytic quarter arcs yield an endpoint-connected ring;
                        // unlike flattened chords they retain the exact circular area.
                        var center=new PreviewPoint(circle.Center.X,circle.Center.Y);
                        for(int k=0;k<4;k++)
                        {
                            var arcPart=new PreviewArc(center,circle.Radius,k*90,90);
                            var t0=k*Math.PI/2;
                            var t1=(k+1)*Math.PI/2;
                            var a=new PreviewPoint(center.X+circle.Radius*Math.Cos(t0),
                                                   center.Y+circle.Radius*Math.Sin(t0));
                            var b=new PreviewPoint(center.X+circle.Radius*Math.Cos(t1),
                                                   center.Y+circle.Radius*Math.Sin(t1));
                            edges.Add(new(arcPart,a,b));
                        }
                    }
                    else if(shape is DxfArc arc)
                    {
                        if(!Finite(arc.Center.X)||!Finite(arc.Center.Y)||
                           !Finite(arc.Radius)||arc.Radius<=0||
                           !Finite(arc.StartDeg)||!Finite(arc.EndDeg)||
                           Math.Abs(arc.EndDeg-arc.StartDeg)>=360)
                            throw new FormatException("Invalid or ambiguous ARC sweep.");
                        double sweep=Normalize(arc.EndDeg-arc.StartDeg);
                        if(sweep<=1e-9)throw new FormatException("Zero or full-circle ARC.");
                        var a=PointOn(arc,arc.StartDeg);
                        var b=PointOn(arc,arc.StartDeg+sweep);
                        if(!Finite(a.X)||!Finite(a.Y)||!Finite(b.X)||!Finite(b.Y)||
                           Dist2(a,b)<=tol2)
                            throw new FormatException("Invalid or degenerate ARC endpoints.");
                        edges.Add(new(new PreviewArc(new(arc.Center.X,arc.Center.Y),
                            arc.Radius,Normalize(arc.StartDeg),sweep),a,b));
                    }
                }
                var nodes=new List<PreviewPoint>();
                var links=new (int A,int B)[edges.Count];
                int Node(PreviewPoint p)
                {
                    int match=-1;
                    for(int i=0;i<nodes.Count;i++)
                    {
                        if(Dist2(nodes[i],p)>tol2)continue;
                        if(match!=-1)throw new FormatException("Ambiguous endpoint clustering.");
                        match=i;
                    }
                    if(match>=0)return match;
                    nodes.Add(p);
                    return nodes.Count-1;
                }
                for(int i=0;i<edges.Count;i++)
                {
                    var a=Node(edges[i].Start);
                    var b=Node(edges[i].End);
                    if(a==b)throw new FormatException("Collapsed segment.");
                    links[i]=(a,b);
                }
                var incident=Enumerable.Range(0,nodes.Count).Select(_=>new List<int>()).ToArray();
                for(int i=0;i<edges.Count;i++)
                {
                    incident[links[i].A].Add(i);
                    incident[links[i].B].Add(i);
                }
                if(incident.Any(es=>es.Count!=2))
                    throw new FormatException("Open chain or branching segment graph.");
                var visited=new bool[edges.Count];
                for(int first=0;first<edges.Count;first++)
                {
                    if(visited[first])continue;
                    var startNode=links[first].A;
                    var currentNode=startNode;
                    var currentEdge=first;
                    var ordered=new List<PreviewSegment>();
                    do
                    {
                        if(visited[currentEdge])throw new FormatException("Repeated segment.");
                        visited[currentEdge]=true;
                        var e=edges[currentEdge];
                        var link=links[currentEdge];
                        var forward=link.A==currentNode;
                        ordered.Add(forward ? e.Segment : Reverse(e.Segment));
                        var next=forward?link.B:link.A;
                        currentNode=next;
                        if(currentNode==startNode)break;
                        var options=incident[currentNode].Where(x=>!visited[x]).ToArray();
                        if(options.Length!=1)
                            throw new FormatException("Ambiguous continuation.");
                        currentEdge=options[0];
                        if(ordered.Count>edges.Count)throw new FormatException("Unbounded ring.");
                    }while(true);
                    if(ordered.Count<2)throw new FormatException("Incomplete contour.");
                    var result=VirtualToolpath.Sample(new PreviewPath(ordered,true),
                        maxChordErrorMm:0.1,joinToleranceMm:toleranceMm);
                    if(!result.IsValidForPreview)
                        throw new FormatException("Continuity validation failed: "+
                            string.Join("; ",result.Warnings));
                    var previewPath = new PreviewPath(ordered,true);
                    var geometricIssues = PreviewContourValidator.Validate(previewPath,toleranceMm);
                    if (geometricIssues.Count > 0)
                        throw new FormatException("Contour geometric intersection: " +
                            string.Join("; ",geometricIssues));
                    paths.Add(previewPath);
                }
            }
            catch(Exception ex) when (ex is FormatException or ArgumentException)
            {
                warnings.Add($"Layer {group.Key}: {ex.Message}");
                return new([],warnings,false);
            }
        }
        // Separate rings can each be simple while crossing or touching one another.
        // Fail the entire display assembly rather than silently returning partial geometry.
        for (int i = 0; i < paths.Count; i++)
        for (int j = i + 1; j < paths.Count; j++)
        {
            var pairIssues = PreviewContourValidator.ValidatePair(paths[i], paths[j], toleranceMm);
            if (pairIssues.Count == 0) continue;
            warnings.Add($"Contours {i + 1} and {j + 1}: {string.Join("; ", pairIssues)}");
            return new([], warnings, false);
        }
        return new(paths,warnings,true);
    }

    private static PreviewSegment Reverse(PreviewSegment segment) => segment switch
    {
        PreviewLine l => new PreviewLine(l.End,l.Start),
        PreviewArc a => new PreviewArc(a.Center,a.RadiusMm,
            Normalize(a.StartDegrees+a.SweepDegrees),-a.SweepDegrees),
        _ => throw new ArgumentException("Unsupported segment")
    };
}
