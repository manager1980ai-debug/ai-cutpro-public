namespace AICutPro.Core;

/// <summary>
/// Explicit unit conversion for supported read-only DXF geometry. The caller must
/// confirm the source units; absent/unknown units are never guessed.
/// Conversion does not certify topology, toolpaths or machine operation.
/// </summary>
public sealed record DxfUnitConversionResult(DxfImportResult Document,
    int SourceUnitCode, double MillimetresPerUnit)
{
    public bool CanStartMachine => false;
}

public static class DxfUnitConverter
{
    public static DxfUnitConversionResult ToMillimetres(DxfImportResult source,
        int confirmedSourceUnits)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.InsUnits != confirmedSourceUnits)
            throw new FormatException("Confirmed units disagree with DXF $INSUNITS; do not guess scale.");
        var factor=confirmedSourceUnits switch
        {
            1 => 25.4,    // inch
            2 => 304.8,   // foot
            4 => 1.0,     // millimetre
            5 => 10.0,    // centimetre
            6 => 1000.0,  // metre
            _ => throw new FormatException("Unsupported or unspecified DXF unit code.")
        };

        double Scale(double value)
        {
            var converted=value*factor;
            if (!double.IsFinite(converted) || Math.Abs(converted)>1e9)
                throw new FormatException("DXF unit scaling exceeds finite preview coordinate bounds.");
            return converted;
        }
        Point2 Point(Point2 p) => new(Scale(p.X),Scale(p.Y));
        var converted=new List<DxfShape>(source.Shapes.Count);
        foreach(var shape in source.Shapes)
        {
            converted.Add(shape switch
            {
                DxfLine l => new DxfLine(l.Layer,Point(l.Start),Point(l.End)),
                DxfCircle c => new DxfCircle(c.Layer,Point(c.Center),Scale(c.Radius)),
                DxfArc a => new DxfArc(a.Layer,Point(a.Center),Scale(a.Radius),
                                      a.StartDeg,a.EndDeg),
                DxfPolyline p => new DxfPolyline(p.Layer,p.Vertices.Select(Point).ToArray(),p.Closed),
                DxfBulgedPolyline b => new DxfBulgedPolyline(b.Layer,
                    b.Vertices.Select(Point).ToArray(),b.Bulges.ToArray(),b.Closed),
                _ => throw new FormatException("Unsupported shape cannot be silently scaled.")
            });
        }
        // Only remove the exact unit-code warning. Preserve unsupported-entity,
        // codepage and other importer warnings, which continue to block preflight.
        var unitWarning=$"DXF $INSUNITS={confirmedSourceUnits}. Only millimetres (4) may be used in cutting jobs without conversion.";
        var warnings=source.Warnings.Where(w=>
            !(confirmedSourceUnits!=4 &&
              string.Equals(w,unitWarning,StringComparison.Ordinal))).ToArray();
        var doc=new DxfImportResult(converted,warnings,4);
        return new(doc,confirmedSourceUnits,factor);
    }
}
