namespace AICutPro.Core;

public sealed record PolygonProfile(
    string Layer,
    IReadOnlyList<Point2> Boundary,
    IReadOnlyList<IReadOnlyList<Point2>> InnerPolygons,
    IReadOnlyList<DxfCircle> CircularHoles,
    double OuterAreaMm2,
    double NetAreaMm2);
public sealed record ProfileAnalysis(IReadOnlyList<PolygonProfile> Profiles, IReadOnlyList<string> Warnings);

/// <summary>
/// Geometric inspection only. Does not produce toolpaths or certify safe cut geometry.
/// Requires closed, simple polygons on millimetre-scale coordinates. Open paths remain in preview.
/// </summary>
public static class ProfileAnalyzer
{
    private const double Eps = 1e-7;
    private static double Cross(Point2 a, Point2 b, Point2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static double DistSquared(Point2 a, Point2 b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    private static double OnSegment(Point2 a, Point2 b, Point2 p)
        => Math.Abs(Cross(a,b,p)) <= Eps &&
           p.X >= Math.Min(a.X,b.X) - Eps && p.X <= Math.Max(a.X,b.X) + Eps &&
           p.Y >= Math.Min(a.Y,b.Y) - Eps && p.Y <= Math.Max(a.Y,b.Y) + Eps ? 1 : 0;
    private static bool Intersects(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        var ab1 = Cross(a,b,c); var ab2 = Cross(a,b,d);
        var cd1 = Cross(c,d,a); var cd2 = Cross(c,d,b);
        if ((ab1 > Eps && ab2 < -Eps || ab1 < -Eps && ab2 > Eps) &&
            (cd1 > Eps && cd2 < -Eps || cd1 < -Eps && cd2 > Eps)) return true;
        return (Math.Abs(ab1) <= Eps && OnSegment(a,b,c) > 0) ||
               (Math.Abs(ab2) <= Eps && OnSegment(a,b,d) > 0) ||
               (Math.Abs(cd1) <= Eps && OnSegment(c,d,a) > 0) ||
               (Math.Abs(cd2) <= Eps && OnSegment(c,d,b) > 0);
    }
    private static bool RingsIntersect(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        for (int i = 0; i < a.Count; i++)
        for (int j = 0; j < b.Count; j++)
            if (Intersects(a[i],a[(i+1)%a.Count],b[j],b[(j+1)%b.Count])) return true;
        return false;
    }
    private static bool Simple(IReadOnlyList<Point2> p)
    {
        if (p.Count < 3) return false;
        for (int i = 0; i < p.Count; i++)
        {
            if (DistSquared(p[i],p[(i+1)%p.Count]) <= Eps*Eps) return false;
            for (int j = i+1; j < p.Count; j++)
            {
                if (j == i+1 || (i == 0 && j == p.Count-1)) continue;
                if (Intersects(p[i],p[(i+1)%p.Count],p[j],p[(j+1)%p.Count])) return false;
            }
        }
        return true;
    }
    private static double Area(IReadOnlyList<Point2> p)
    {
        double twice = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];var b = p[(i+1)%p.Count];
            twice += a.X*b.Y-b.X*a.Y;
        }
        return Math.Abs(twice)/2;
    }
    // Boundary is considered outside so a touching hole cannot be accepted.
    private static bool Inside(Point2 p, IReadOnlyList<Point2> ring)
    {
        bool result = false;
        for (int i=0,j=ring.Count-1;i<ring.Count;j=i++)
        {
            var a=ring[j];var b=ring[i];
            if (OnSegment(a,b,p)>0) return false;
            if ((a.Y>p.Y)!=(b.Y>p.Y) &&
                p.X < (b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X) result=!result;
        }
        return result;
    }
    private static double DistanceToSegmentSquared(Point2 p,Point2 a,Point2 b)
    {
        var dx=b.X-a.X; var dy=b.Y-a.Y;
        var t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/(dx*dx+dy*dy),0,1);
        return DistSquared(p,new Point2(a.X+t*dx,a.Y+t*dy));
    }
    private static double BoundaryDistanceSquared(Point2 p,IReadOnlyList<Point2> ring)
    {
        double best=double.PositiveInfinity;
        for(int i=0;i<ring.Count;i++)
            best=Math.Min(best,DistanceToSegmentSquared(p,ring[i],ring[(i+1)%ring.Count]));
        return best;
    }
    public static ProfileAnalysis Analyze(DxfImportResult document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var warnings = new List<string>();
        if (document.InsUnits != 4 || document.Warnings.Count != 0)
        {
            warnings.Add("Контуры не классифицированы: DXF содержит предупреждения или единицы не подтверждены как мм.");
            return new([],warnings);
        }
        // ARC chains are not joined here: sampled display arcs cannot be used as CAD boundaries.
        if (document.Shapes.OfType<DxfArc>().Any())
        {
            warnings.Add("ARC присутствуют в DXF. Построение деталей из дуговых цепей пока не поддерживается.");
            return new([], warnings);
        }
        var lines = document.Shapes.OfType<DxfLine>().ToArray();
        var assembled = LineContourAssembler.Assemble(lines);
        if (!assembled.Complete)
        {
            warnings.AddRange(assembled.Warnings);
            return new([], warnings);
        }
        var polygons = document.Shapes.OfType<DxfPolyline>().Concat(assembled.ClosedContours).ToArray();
        var rings = new List<(string Layer,IReadOnlyList<Point2> Points,double Area)>();
        foreach (var poly in polygons)
        {
            if (!poly.Closed) {warnings.Add($"Открытая полилиния в слое {poly.Layer} не является деталью.");continue;}
            var vertices=poly.Vertices.ToList();
            if (vertices.Count>3 && DistSquared(vertices[0],vertices[^1])<=Eps*Eps)
                vertices.RemoveAt(vertices.Count-1);
            if (!Simple(vertices)||Area(vertices)<=Eps)
            {
                warnings.Add($"Самопересечение, вырождение или неверный контур в слое {poly.Layer}.");
                continue;
            }
            rings.Add((poly.Layer,vertices,Area(vertices)));
        }
        if (rings.Count>1000){warnings.Add("Слишком много замкнутых контуров для анализа.");return new([],warnings);}
        // Refuse to classify anything when contour relationships are ambiguous.
        for(int i=0;i<rings.Count;i++)
        for(int j=i+1;j<rings.Count;j++)
            if(RingsIntersect(rings[i].Points,rings[j].Points))
            {
                warnings.Add("Замкнутые контуры пересекаются или касаются. Автоматическая классификация запрещена.");
                return new([],warnings);
            }
        var parent = new int[rings.Count];
        Array.Fill(parent,-1);
        for(int i=0;i<rings.Count;i++)
        {
            double best=double.PositiveInfinity;
            for(int j=0;j<rings.Count;j++)
            {
                if(i==j||rings[j].Area<=rings[i].Area) continue;
                if(Inside(rings[i].Points[0],rings[j].Points)&&rings[j].Area<best)
                { parent[i]=j;best=rings[j].Area; }
            }
        }
        int Depth(int index) {int depth=0;while(parent[index]>=0){depth++;index=parent[index];if(depth>rings.Count)throw new InvalidOperationException("Invalid containment");}return depth;}
        var depths=Enumerable.Range(0,rings.Count).Select(Depth).ToArray();
        if(depths.Any(d=>d>1))
        {
            warnings.Add("Вложенность более одного уровня требует ручной проверки; классификация остановлена.");
            return new([],warnings);
        }
        var holes=document.Shapes.OfType<DxfCircle>().ToList();
        foreach(var circle in holes)
        {
            if (!double.IsFinite(circle.Radius)||circle.Radius<=0 || !double.IsFinite(circle.Center.X)||!double.IsFinite(circle.Center.Y))
            {warnings.Add("Некорректная окружность.");return new([],warnings);}
            var owners=Enumerable.Range(0,rings.Count)
                .Where(i=>depths[i]==0 && Inside(circle.Center,rings[i].Points) &&
                    BoundaryDistanceSquared(circle.Center,rings[i].Points)>(circle.Radius+Eps)*(circle.Radius+Eps))
                .ToList();
            if(owners.Count!=1)
            {warnings.Add("Окружность не находится строго внутри одной детали.");return new([],warnings);}
            for(int i=0;i<rings.Count;i++)
                if(depths[i]==1 && parent[i]==owners[0] &&
                    (Inside(circle.Center,rings[i].Points) ||
                     BoundaryDistanceSquared(circle.Center,rings[i].Points)<=(circle.Radius+Eps)*(circle.Radius+Eps)))
                {warnings.Add("Окружность пересекается с внутренним контуром.");return new([],warnings);}
        }
        for(int i=0;i<holes.Count;i++)
        for(int j=i+1;j<holes.Count;j++)
        {
            var min=holes[i].Radius+holes[j].Radius+Eps;
            if(DistSquared(holes[i].Center,holes[j].Center)<=min*min)
            {warnings.Add("Круглые отверстия пересекаются или касаются.");return new([],warnings);}
        }
        var profiles=new List<PolygonProfile>();
        for(int i=0;i<rings.Count;i++)
        {
            if(depths[i]!=0) continue;
            var inner=Enumerable.Range(0,rings.Count).Where(j=>parent[j]==i).ToArray();
            var circles=holes.Where(c=>Inside(c.Center,rings[i].Points)).ToArray();
            var net=rings[i].Area-inner.Sum(j=>rings[j].Area)-circles.Sum(c=>Math.PI*c.Radius*c.Radius);
            if(net<=Eps){warnings.Add("Неположительная полезная площадь детали.");return new([],warnings);}
            profiles.Add(new(rings[i].Layer,rings[i].Points,inner.Select(j=>rings[j].Points).ToArray(),circles,rings[i].Area,net));
        }
        var ignored=document.Shapes.Count - document.Shapes.OfType<DxfPolyline>().Count() - holes.Count - lines.Length;
        if(ignored>0)warnings.Add($"Отдельные линии/другие элементы ({ignored}) не преобразованы в детали.");
        return new(profiles,warnings);
    }
}
