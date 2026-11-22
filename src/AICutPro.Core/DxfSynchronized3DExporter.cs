using System.Globalization;
using System.Text;

namespace AICutPro.Core;

/// <summary>
/// Produces a 3D OpenSCAD extrusion from the CURRENT, verified DXF preview.
/// Supported subset: one straight-edged exterior with straight-edged polygonal
/// or truly circular through-holes. Unsupported arcs, material islands and multiple parts are
/// rejected rather than silently omitted. No CAM or machine authorization.
/// </summary>
public sealed record Synchronized3DPreview(
    string OpenScad, double ThicknessMm, double NetAreaMm2,
    double VolumeMm3, int HoleCount)
{
    public bool CanStartMachine => false;
    public bool IsManufacturingValidated => false;
}

public static class DxfSynchronized3DExporter
{
    public static Synchronized3DPreview Export(
        DxfImportResult currentDxf, double thicknessMm,
        double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(currentDxf);
        if (!double.IsFinite(thicknessMm) ||
            thicknessMm <= 0 || thicknessMm > 100000)
            throw new FormatException("3D thickness must be a finite positive millimetre value.");
        if (!double.IsFinite(toleranceMm) ||
            toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));

        var rings = PreviewDxfExporter.ValidateDocument(currentDxf,toleranceMm);
        var topology = PreviewContourTopology.Analyze(rings,toleranceMm);
        if (!topology.IsValid || topology.Rings.Count != rings.Count)
            throw new FormatException("Current DXF geometry has invalid topology.");

        // Only one solid exterior; holes must be its immediate children.
        var exterior = topology.Rings.Where(r => r.Depth == 0).ToArray();
        if (exterior.Length != 1 ||
            topology.Rings.Any(r => r.Depth > 1 || (r.Depth == 1 &&
                r.ParentIndex != exterior[0].Index)))
            throw new FormatException("3D extrusion supports one part with direct holes.");

        var outline = rings[exterior[0].Index];
        if (!outline.Closed || outline.Segments.Count is < 3 or > 2000 ||
            outline.Segments.Any(s => s is not PreviewLine))
            throw new FormatException("Outer 3D profile must consist of straight edges.");
        var epsilon = Math.Max(1e-8,toleranceMm*1e-4);
        var vertices = ExtractVertices(outline,epsilon);

        // The source contour must remain analytical. Polygonal holes are
        // exported as exact straight boundaries; circles retain exact centers
        // and radii. Unsupported mixed/partial arcs are rejected.
        var roundHoles = new List<(PreviewPoint Center,double Radius)>();
        var polygonHoles = new List<PreviewPoint[]>();
        foreach (var info in topology.Rings.Where(r=>r.Depth==1))
        {
            var hole = rings[info.Index];
            if (hole.Closed && hole.Segments.Count is >= 3 and <= 2000 &&
                hole.Segments.All(segment => segment is PreviewLine))
            {
                polygonHoles.Add(ExtractVertices(hole,epsilon));
                continue;
            }
            var circle = RecognizeCircle(hole,epsilon);
            if(circle is null)
                throw new FormatException("Unsupported curved or mixed 3D through-hole.");
            roundHoles.Add(circle.Value);
        }
        if(roundHoles.Count+polygonHoles.Count > 200)
            throw new FormatException("Too many 3D through-holes.");

        var roundTrip = PreviewDxfExporter.Export(rings,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(rings,roundTrip,toleranceMm);
        var volume = topology.NetAreaMm2 * thicknessMm;
        if (!double.IsFinite(volume) || volume <= 0)
            throw new FormatException("Invalid synchronized 3D volume.");

        var scad = new StringBuilder();
        scad.AppendLine("// AI CutPro synchronized current DXF -> 3D preview");
        scad.AppendLine("// Units: mm. Not CAM; not certified for manufacturing.");
        scad.AppendLine("$fn = 192;");
        scad.AppendLine("difference() {");
        scad.Append("  linear_extrude(height = ").Append(N(thicknessMm))
            .AppendLine(", center = false, convexity = 10) {");
        scad.Append("    polygon(points = [");
        for (int i=0;i<vertices.Length;i++)
        {
            if(i>0)scad.Append(", ");
            scad.Append('[').Append(N(vertices[i].X)).Append(", ")
                .Append(N(vertices[i].Y)).Append(']');
        }
        scad.AppendLine("]);");
        scad.AppendLine("  }");
        // The cutters overlap both faces to prevent coplanar CSG artifacts.
        var overhang = Math.Min(0.1, thicknessMm/4);
        foreach(var circle in roundHoles)
        {
            scad.Append("  translate([").Append(N(circle.Center.X))
                .Append(", ").Append(N(circle.Center.Y))
                .Append(", -").Append(N(overhang)).Append("]) ")
                .Append("cylinder(h = ").Append(N(thicknessMm+2*overhang))
                .Append(", r = ").Append(N(circle.Radius))
                .AppendLine(", center = false);");
        }
        foreach(var verticesHole in polygonHoles)
        {
            scad.Append("  translate([0, 0, -").Append(N(overhang))
                .Append("]) linear_extrude(height = ")
                .Append(N(thicknessMm+2*overhang))
                .AppendLine(", center = false, convexity = 10) {");
            scad.Append("    polygon(points = [");
            for(int i=0;i<verticesHole.Length;i++)
            {
                if(i>0)scad.Append(", ");
                scad.Append('[').Append(N(verticesHole[i].X)).Append(", ")
                    .Append(N(verticesHole[i].Y)).Append(']');
            }
            scad.AppendLine("]);");
            scad.AppendLine("  }");
        }
        scad.AppendLine("}");
        return new(scad.ToString(),thicknessMm,topology.NetAreaMm2,
                   volume,roundHoles.Count+polygonHoles.Count);
    }

    private static PreviewPoint[] ExtractVertices(PreviewPath ring,double epsilon)
    {
        if (!ring.Closed || ring.Segments.Count is < 3 or > 2000 ||
            ring.Segments.Any(segment=>segment is not PreviewLine))
            throw new FormatException("Polygonal 3D profile must contain straight edges.");
        var segments = ring.Segments.Cast<PreviewLine>().ToArray();
        var vertices = segments.Select(segment=>segment.Start).ToArray();
        for(int i=0;i<segments.Length;i++)
            if(Distance(segments[i].End,vertices[(i+1)%vertices.Length])>epsilon)
                throw new FormatException("3D polygon edges are not joined exactly.");
        return vertices;
    }

    private static (PreviewPoint Center,double Radius)? RecognizeCircle(
        PreviewPath ring,double eps)
    {
        if (!ring.Closed || ring.Segments.Count!=4 ||
            ring.Segments.Any(s=>s is not PreviewArc)) return null;
        var arcs=ring.Segments.Cast<PreviewArc>().ToArray();
        var origin=arcs[0];
        if(!double.IsFinite(origin.RadiusMm) || origin.RadiusMm<=eps)
            return null;
        var sweep=arcs.Sum(a=>a.SweepDegrees);
        if(Math.Abs(Math.Abs(sweep)-360)>1e-8)
            return null;
        foreach(var a in arcs)
            if(Math.Abs(a.Center.X-origin.Center.X)>eps ||
               Math.Abs(a.Center.Y-origin.Center.Y)>eps ||
               Math.Abs(a.RadiusMm-origin.RadiusMm)>eps ||
               Math.Abs(Math.Abs(a.SweepDegrees)-90)>1e-8 ||
               Math.Sign(a.SweepDegrees)!=Math.Sign(origin.SweepDegrees))
                return null;
        return (origin.Center,origin.RadiusMm);
    }
    private static double Distance(PreviewPoint a,PreviewPoint b) =>
        Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static string N(double value) =>
        value.ToString("G17",CultureInfo.InvariantCulture);
}
