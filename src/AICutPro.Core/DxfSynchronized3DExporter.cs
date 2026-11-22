using System.Globalization;
using System.Text;

namespace AICutPro.Core;

/// <summary>
/// Produces a 3D OpenSCAD extrusion from the CURRENT, verified DXF preview.
/// Supported subset: one straight-edged exterior and any number of DXF CIRCLE
/// through-holes. Unsupported arcs, material islands and multiple parts are
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
            throw new FormatException("3D extrusion supports one part with direct circular holes.");

        var outline = rings[exterior[0].Index];
        if (!outline.Closed || outline.Segments.Count is < 3 or > 2000 ||
            outline.Segments.Any(s => s is not PreviewLine))
            throw new FormatException("Outer 3D profile must consist of straight edges.");
        var vertices = outline.Segments.Cast<PreviewLine>()
            .Select(s => s.Start).ToArray();
        var epsilon = Math.Max(1e-8,toleranceMm*1e-4);
        for(int i=0;i<vertices.Length;i++)
        {
            var next = (PreviewLine)outline.Segments[i];
            var following = vertices[(i+1)%vertices.Length];
            if (Distance(next.End,following) > epsilon)
                throw new FormatException("Outer DXF vertices are not connected exactly.");
        }

        // CIRCLE is represented by four exact analytic quarter arcs. Editing
        // the exterior can reserialize those arcs as ARC entities, so recognize
        // them by strict geometry rather than depending on their DXF entity type.
        // Other curved holes are refused, never flattened or silently omitted.
        var holes = new List<(PreviewPoint Center,double Radius)>();
        foreach (var info in topology.Rings.Where(r=>r.Depth==1))
        {
            var circle = RecognizeCircle(rings[info.Index],epsilon);
            if(circle is null)
                throw new FormatException("3D extrusion requires truly circular through-holes.");
            holes.Add(circle.Value);
        }
        if(holes.Count > 200)
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
        foreach(var circle in holes)
        {
            // Extend the cutter past both faces to avoid coplanar CSG artifacts.
            var overhang = Math.Min(0.1, thicknessMm/4);
            scad.Append("  translate([").Append(N(circle.Center.X))
                .Append(", ").Append(N(circle.Center.Y))
                .Append(", -").Append(N(overhang)).Append("]) ")
                .Append("cylinder(h = ").Append(N(thicknessMm+2*overhang))
                .Append(", r = ").Append(N(circle.Radius))
                .AppendLine(", center = false);");
        }
        scad.AppendLine("}");
        return new(scad.ToString(),thicknessMm,topology.NetAreaMm2,
                   volume,holes.Count);
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
