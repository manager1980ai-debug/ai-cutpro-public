using System.Globalization;
using System.Text;

namespace AICutPro.Core;

/// <summary>
/// Restricted, geometry-only ASCII DXF R2018 exporter for analytically validated
/// closed LINE/ARC rings. It refuses partial, unsupported, or ambiguous contours.
/// It never creates CAM instructions, kerf offsets, or machine commands.
/// </summary>
public static class PreviewDxfExporter
{
    /// <summary>
    /// Validate all geometry from one imported drawing before serializing it.
    /// The only acceptable import warnings are the two precise "preview-only"
    /// bulge notices; every unknown/skipped/ambiguous entity still blocks export.
    /// This authorizes an analytical drawing export ONLY, never CAM or cutting.
    /// </summary>
    public static IReadOnlyList<PreviewPath> ValidateDocument(
        DxfImportResult document, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(document);
        if(document.InsUnits!=4)
            throw new FormatException("DXF units must be confirmed as millimetres.");
        if(document.Warnings.Any(w=> w is not
            ("Bulged LWPOLYLINE is preview-only: CAD/CAM geometry is not yet certified." or
             "Legacy bulged POLYLINE is preview-only: CAD/CAM not certified.")))
            throw new FormatException("DXF contains skipped or unsupported geometry warnings.");
        if(document.Shapes.Count==0 ||
           document.Shapes.Any(s=>s is not
               (DxfLine or DxfArc or DxfCircle or DxfBulgedPolyline or DxfPolyline)))
            throw new FormatException("DXF contains unsupported geometry.");
        var assembly=MixedContourAssembler.Assemble(document.Shapes,toleranceMm);
        if(!assembly.Complete || assembly.ClosedPaths.Count==0)
            throw new FormatException("DXF contour assembly incomplete: " +
                string.Join("; ",assembly.Warnings));
        var topology=PreviewContourTopology.Analyze(assembly.ClosedPaths,toleranceMm);
        if(!topology.IsValid)
            throw new FormatException("DXF topology not verified: " +
                string.Join("; ",topology.Issues));
        return assembly.ClosedPaths;
    }

    public static string Export(IReadOnlyList<PreviewPath> rings, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        var topology = PreviewContourTopology.Analyze(rings, toleranceMm);
        if (!topology.IsValid)
            throw new FormatException("Unverified DXF geometry: " + string.Join("; ", topology.Issues));
        var entityCount = rings.Sum(r => (long)r.Segments.Count);
        if (entityCount > 10000)
            throw new FormatException("Too many DXF entities to export.");

        var result = new StringBuilder();
        void Pair(int code, string value)
        {
            result.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n');
            result.Append(value).Append('\n');
        }
        void Number(int code, double number)
        {
            if (!double.IsFinite(number) || Math.Abs(number) > 1e9)
                throw new FormatException("DXF coordinate is out of range.");
            Pair(code, number.ToString("G17", CultureInfo.InvariantCulture));
        }
        static double Normalize(double angle) => (angle % 360 + 360) % 360;

        Pair(0,"SECTION"); Pair(2,"HEADER");
        Pair(9,"$ACADVER"); Pair(1,"AC1032");
        Pair(9,"$DWGCODEPAGE"); Pair(3,"UTF-8");
        Pair(9,"$INSUNITS"); Pair(70,"4");
        Pair(0,"ENDSEC");
        Pair(0,"SECTION"); Pair(2,"ENTITIES");
        foreach (var ring in rings)
        foreach (var segment in ring.Segments)
        {
            switch (segment)
            {
                case PreviewLine line:
                    Pair(0,"LINE"); Pair(8,"CUT");
                    Number(10,line.Start.X); Number(20,line.Start.Y);
                    Number(11,line.End.X); Number(21,line.End.Y);
                    break;
                case PreviewArc arc:
                    // ARC entities always traverse CCW in DXF. For clockwise
                    // preview arcs, reverse start/end while preserving the same
                    // geometric curve. Traversal is recovered by the assembler.
                    if (arc.SweepDegrees == 0 || Math.Abs(arc.SweepDegrees) >= 360)
                        throw new FormatException("Full-circle or zero-sweep ARC is not exportable.");
                    Pair(0,"ARC"); Pair(8,"CUT");
                    Number(10,arc.Center.X); Number(20,arc.Center.Y);
                    Number(40,arc.RadiusMm);
                    Number(50,Normalize(arc.StartDegrees +
                        (arc.SweepDegrees < 0 ? arc.SweepDegrees : 0)));
                    Number(51,Normalize(arc.StartDegrees +
                        (arc.SweepDegrees > 0 ? arc.SweepDegrees : 0)));
                    break;
                default: throw new FormatException("Unsupported analytic DXF entity.");
            }
        }
        Pair(0,"ENDSEC"); Pair(0,"EOF");
        if (result.Length > 8_000_000)
            throw new FormatException("Export exceeds the supported DXF size limit.");
        return result.ToString();
    }
}
