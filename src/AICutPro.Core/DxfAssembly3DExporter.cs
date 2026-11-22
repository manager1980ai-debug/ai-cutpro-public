using System.Globalization;
using System.Text;

namespace AICutPro.Core;

/// <summary>
/// Generates a CSG assembly from the current, topology-verified DXF.
/// Multiple disjoint material regions, holes and nested material islands are
/// supported if EVERY ring is either line-only or a true analytic circle.
/// Each even-depth ring is an added material body; its direct odd-depth
/// children are subtracted through-holes. No B-rep or manufacturing approval.
/// </summary>
public sealed record DxfAssembly3DPreview(
    string OpenScad, double ThicknessMm, double NetAreaMm2,
    double VolumeMm3, int MaterialRegionCount, int HoleCount)
{
    public bool CanStartMachine => false;
    public bool IsManufacturingValidated => false;
}

public static class DxfAssembly3DExporter
{
    private sealed record Shape(PreviewPoint[]? Polygon,
        PreviewPoint Center, double Radius)
    {
        public bool IsCircle => Polygon is null;
    }

    public static DxfAssembly3DPreview Export(DxfImportResult document,
        double thicknessMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(thicknessMm) || thicknessMm <= 0 ||
            thicknessMm > 100_000)
            throw new FormatException("Invalid 3D extrusion thickness.");
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 ||
            toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));

        var paths = PreviewDxfExporter.ValidateDocument(document,toleranceMm);
        var topology = PreviewContourTopology.Analyze(paths,toleranceMm);
        if (!topology.IsValid || topology.Rings.Count!=paths.Count)
            throw new FormatException("Invalid 3D source topology.");
        if (paths.Count > 200)
            throw new FormatException("3D CSG assembly exceeds the ring limit.");

        var eps = Math.Max(1e-8,toleranceMm*1e-4);
        var shapes = paths.Select(p=>Describe(p,eps)).ToArray();
        var material = topology.Rings.Where(r=>r.IsMaterialBoundary).ToArray();
        var holes = topology.Rings.Where(r=>!r.IsMaterialBoundary).ToArray();
        if (material.Length==0)
            throw new FormatException("No material in DXF geometry.");
        var volume = topology.NetAreaMm2 * thicknessMm;
        if (!double.IsFinite(volume) || volume<=0)
            throw new FormatException("Invalid 3D assembly volume.");

        // The independent ASCII DXF round-trip compares exact analytic LINE
        // and ARC segments and the full containment hierarchy.
        PreviewDxfRoundTripVerifier.Verify(paths,
            PreviewDxfExporter.Export(paths,toleranceMm),toleranceMm);

        var sb = new StringBuilder();
        sb.AppendLine("// AI CutPro: current DXF analytical 3D CSG assembly.");
        sb.AppendLine("// Preview only, units mm. NOT a machine or CAM program.");
        sb.AppendLine("$fn = 192;");
        sb.AppendLine("union() {");
        foreach(var region in material)
        {
            var directVoids = holes.Where(h=>h.ParentIndex==region.Index).ToArray();
            sb.AppendLine("  difference() {");
            EmitShape(sb,shapes[region.Index],thicknessMm,0,"    ");
            foreach(var hole in directVoids)
            {
                var overlap = Math.Min(0.1,thicknessMm/4);
                EmitShape(sb,shapes[hole.Index],thicknessMm+2*overlap,
                    -overlap,"    ");
            }
            sb.AppendLine("  }");
        }
        sb.AppendLine("}");
        return new(sb.ToString(), thicknessMm, topology.NetAreaMm2,
            volume, material.Length, holes.Length);
    }

    private static Shape Describe(PreviewPath path, double eps)
    {
        if (!path.Closed || path.Segments.Count is < 3 or > 2000)
            throw new FormatException("3D CSG requires closed bounded rings.");
        if (path.Segments.All(s=>s is PreviewLine))
        {
            var edges=path.Segments.Cast<PreviewLine>().ToArray();
            var vertices=edges.Select(x=>x.Start).ToArray();
            for(var i=0;i<edges.Length;i++)
                if(Distance(edges[i].End,vertices[(i+1)%edges.Length])>eps)
                    throw new FormatException("3D straight edges are disconnected.");
            return new(vertices,default,0);
        }
        if(path.Segments.Count!=4 ||
           path.Segments.Any(s=>s is not PreviewArc))
            throw new FormatException("3D curved rings must be exact circles.");
        var arcs=path.Segments.Cast<PreviewArc>().ToArray();
        var first=arcs[0];
        if (!double.IsFinite(first.RadiusMm) || first.RadiusMm<=eps ||
            Math.Abs(Math.Abs(arcs.Sum(a=>a.SweepDegrees))-360)>1e-8)
            throw new FormatException("Unsupported circular 3D ring.");
        foreach(var arc in arcs)
            if (Math.Abs(arc.Center.X-first.Center.X)>eps ||
                Math.Abs(arc.Center.Y-first.Center.Y)>eps ||
                Math.Abs(arc.RadiusMm-first.RadiusMm)>eps ||
                Math.Abs(Math.Abs(arc.SweepDegrees)-90)>1e-8 ||
                Math.Sign(arc.SweepDegrees)!=Math.Sign(first.SweepDegrees))
                throw new FormatException("Unsupported noncircular 3D arc.");
        return new(null,first.Center,first.RadiusMm);
    }

    private static void EmitShape(StringBuilder sb, Shape shape,
        double height, double z, string indent)
    {
        if (shape.IsCircle)
        {
            sb.Append(indent).Append("translate([")
                .Append(N(shape.Center.X)).Append(", ")
                .Append(N(shape.Center.Y)).Append(", ")
                .Append(N(z)).Append("]) cylinder(h = ")
                .Append(N(height)).Append(", r = ")
                .Append(N(shape.Radius)).AppendLine(", center = false);");
        }
        else
        {
            sb.Append(indent).Append("translate([0, 0, ")
                .Append(N(z)).Append("]) linear_extrude(height = ")
                .Append(N(height)).AppendLine(", center = false, convexity = 10) {");
            sb.Append(indent).Append("  polygon(points = [");
            var polygon=shape.Polygon!;
            for(var i=0;i<polygon.Length;i++)
            {
                if(i>0)sb.Append(", ");
                sb.Append('[').Append(N(polygon[i].X)).Append(", ")
                    .Append(N(polygon[i].Y)).Append(']');
            }
            sb.AppendLine("]);");
            sb.Append(indent).AppendLine("}");
        }
    }

    private static double Distance(PreviewPoint a,PreviewPoint b) =>
        Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));

    private static string N(double n) =>
        n.ToString("G17",CultureInfo.InvariantCulture);
}
