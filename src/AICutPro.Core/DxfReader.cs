using System.Globalization;

namespace AICutPro.Core;

public readonly record struct Point2(double X, double Y);
public abstract record DxfShape(string Layer);
public sealed record DxfLine(string Layer, Point2 Start, Point2 End) : DxfShape(Layer);
public sealed record DxfCircle(string Layer, Point2 Center, double Radius) : DxfShape(Layer);
public sealed record DxfArc(string Layer, Point2 Center, double Radius, double StartDeg, double EndDeg) : DxfShape(Layer);
public sealed record DxfPolyline(string Layer, IReadOnlyList<Point2> Vertices, bool Closed) : DxfShape(Layer);
public sealed record DxfImportResult(IReadOnlyList<DxfShape> Shapes, IReadOnlyList<string> Warnings, int? InsUnits);

/// <summary>
/// Strict, read-only ASCII DXF entity preview parser. No CAM toolpaths, nesting, or machine commands.
/// Accepts LINE, CIRCLE, ARC and straight LWPOLYLINE in ENTITIES only.
/// </summary>
public static class DxfReader
{
    private readonly record struct Pair(int Code, string Value);
    private static double Number(Pair pair)
    {
        if (!double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new FormatException($"Invalid DXF number for group {pair.Code}: {pair.Value}");
        return value;
    }
    private static double Required(IReadOnlyList<Pair> fields, int code)
    {
        // Scalar entity coordinates and radii must not silently choose the
        // first of two conflicting DXF groups. Repeated vertex codes in
        // LWPOLYLINE are parsed separately and are unaffected.
        var found = false;
        var value = 0d;
        foreach (var p in fields)
            if (p.Code == code)
            {
                if (found)
                    throw new FormatException($"Duplicate DXF scalar group {code}");
                value = Number(p);
                found = true;
            }
        if (!found) throw new FormatException($"Missing required DXF group {code}");
        return value;
    }
    private static int Integer(IReadOnlyList<Pair> fields, int code, int fallback = 0)
    {
        var found = false;
        var result = fallback;
        foreach (var p in fields)
            if (p.Code == code)
            {
                if (found)
                    throw new FormatException($"Duplicate DXF integer group {code}");
                if (!int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                    throw new FormatException($"Invalid DXF integer group {code}");
                found = true;
            }
        return result;
    }

    public static DxfImportResult Parse(string asciiDxf)
    {
        ArgumentNullException.ThrowIfNull(asciiDxf);
        if (asciiDxf.Length > 8_000_000) throw new ArgumentException("DXF input exceeds preview size limit.");
        using var reader = new StringReader(asciiDxf);
        var pairs = new List<Pair>();
        string? codeLine;
        while ((codeLine = reader.ReadLine()) != null)
        {
            var valueLine = reader.ReadLine() ?? throw new FormatException("Truncated DXF pair");
            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                throw new FormatException($"Invalid DXF group code: {codeLine}");
            pairs.Add(new Pair(code, valueLine.Trim()));
            if (pairs.Count > 250_000) throw new ArgumentException("Too many DXF groups.");
        }

        var shapes = new List<DxfShape>();
        var warnings = new List<string>();
        string section = "";
        string? pendingSection = null;
        int? units = null;
        for (int i = 0; i < pairs.Count;)
        {
            var p = pairs[i];
            if (p.Code == 0 && p.Value.Equals("SECTION", StringComparison.OrdinalIgnoreCase))
            {
                pendingSection = "yes";
                i++;
                continue;
            }
            if (pendingSection != null && p.Code == 2)
            {
                section = p.Value.ToUpperInvariant();
                pendingSection = null;
                i++;
                continue;
            }
            if (p.Code == 0 && p.Value.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase))
            {
                section = "";
                i++;
                continue;
            }
            if (section == "HEADER" && p.Code == 9 && p.Value.Equals("$INSUNITS", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < pairs.Count && pairs[i + 1].Code == 70)
                {
                    // A malformed HEADER variable must not smuggle a second
                    // unit value after the first one. Never silently choose.
                    if (i + 2 < pairs.Count && pairs[i + 2].Code == 70)
                        throw new FormatException("Duplicate DXF $INSUNITS value.");
                    if (!int.TryParse(pairs[i + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                        throw new FormatException("Invalid $INSUNITS");
                    if (units.HasValue && units.Value != n)
                        throw new FormatException("Conflicting DXF $INSUNITS declarations.");
                    units = n;
                }
                i++;
                continue;
            }
            if (section != "ENTITIES" || p.Code != 0) { i++; continue; }
            var type = p.Value.ToUpperInvariant();
            i++;
            var fields = new List<Pair>();
            while (i < pairs.Count && pairs[i].Code != 0) fields.Add(pairs[i++]);
            if (type is "ENDSEC" or "EOF") continue;
            // Legacy R12 POLYLINE consists of a header followed by VERTEX
            // entities and a mandatory SEQEND terminator. Fail closed on
            // 3D, fitted, mesh and polyface variants.
            if (type == "POLYLINE")
            {
                try
                {
                    var flags=Integer(fields,70);
                    if ((flags & ~1)!=0 ||
                        fields.Any(x=>x.Code is 38 or 39 or 30 &&
                                      Math.Abs(Number(x))>1e-12) ||
                        fields.Any(x=>x.Code is 210 or 220 or 230))
                        throw new FormatException("Unsupported legacy POLYLINE flags or 3D geometry.");
                    var legacyLayer=fields.FirstOrDefault(x=>x.Code==8).Value ?? "0";
                    var vertices=new List<Point2>();
                    var bulges=new List<double>();
                    var terminated=false;
                    while (i<pairs.Count && pairs[i].Code==0)
                    {
                        var itemType=pairs[i].Value.ToUpperInvariant();
                        if (itemType=="SEQEND")
                        {
                            i++;
                            while(i<pairs.Count && pairs[i].Code!=0)i++;
                            terminated=true;
                            break;
                        }
                        if (itemType!="VERTEX") break;
                        i++;
                        var vertexFields=new List<Pair>();
                        while(i<pairs.Count && pairs[i].Code!=0)
                            vertexFields.Add(pairs[i++]);
                        if (vertexFields.Any(x=>x.Code is 30 or 39 &&
                                                Math.Abs(Number(x))>1e-12) ||
                            (Integer(vertexFields,70)&~0)!=0)
                            throw new FormatException("Unsupported legacy 3D/fitted VERTEX.");
                        vertices.Add(new Point2(
                            Required(vertexFields,10),Required(vertexFields,20)));
                        bulges.Add(vertexFields.Where(x=>x.Code==42)
                            .Select(Number).DefaultIfEmpty(0).First());
                        if(vertices.Count>10000)
                            throw new FormatException("Too many legacy POLYLINE vertices.");
                    }
                    if(!terminated||vertices.Count<2)
                        throw new FormatException("Unterminated or degenerate legacy POLYLINE.");
                    var closed=(flags&1)!=0;
                    if(bulges.Any(b=>Math.Abs(b)>1e-12))
                    {
                        var curved=new DxfBulgedPolyline(legacyLayer,vertices,bulges,closed);
                        DxfBulgePreview.Build(curved);
                        shapes.Add(curved);
                        warnings.Add("Legacy bulged POLYLINE is preview-only: CAD/CAM not certified.");
                    }
                    else shapes.Add(new DxfPolyline(legacyLayer,vertices,closed));
                }
                catch(Exception ex) when (ex is FormatException or ArgumentException)
                {
                    warnings.Add($"Legacy POLYLINE skipped: {ex.Message}");
                }
                continue;
            }

            if (type is not ("LINE" or "CIRCLE" or "ARC" or "LWPOLYLINE"))
            {
                if (warnings.Count < 100) warnings.Add($"Unsupported entity {type} skipped");
                continue;
            }

            var layer = fields.FirstOrDefault(x => x.Code == 8).Value ?? "0";
            // Elevated/3D entities cannot be silently projected to a cutting plane.
            bool unsupported = fields.Any(x => x.Code is 38 or 39 or 30 or 31 &&
                                                       Math.Abs(Number(x)) > 1e-12) ||
                               fields.Any(x => x.Code is 210 or 220 or 230) ||
                               // Wide polyline geometry cannot be represented by a simple
                               // zero-width cutting centerline without an explicit choice.
                               (type == "LWPOLYLINE" && fields.Any(x =>
                                   x.Code is 40 or 41 or 43 && Math.Abs(Number(x)) > 1e-12));
            if (unsupported)
            {
                warnings.Add($"Entity {type} with elevation/extrusion is unsupported");
                continue;
            }
            try
            {
                switch (type)
                {
                    case "LINE":
                        shapes.Add(new DxfLine(layer,
                            new Point2(Required(fields, 10), Required(fields, 20)),
                            new Point2(Required(fields, 11), Required(fields, 21))));
                        break;
                    case "CIRCLE":
                        var r = Required(fields, 40);
                        if (r <= 0) throw new FormatException("Circle radius must be positive");
                        shapes.Add(new DxfCircle(layer, new Point2(Required(fields, 10), Required(fields, 20)), r));
                        break;
                    case "ARC":
                        var arcRadius = Required(fields, 40);
                        var startDeg = Required(fields, 50);
                        var endDeg = Required(fields, 51);
                        if (arcRadius <= 0 || Math.Abs(startDeg) > 1_000_000 || Math.Abs(endDeg) > 1_000_000)
                            throw new FormatException("ARC radius/angles out of range");
                        if (Math.Abs(startDeg - endDeg) < 1e-9)
                            throw new FormatException("Zero-sweep ARC must be verified");
                        shapes.Add(new DxfArc(layer, new Point2(Required(fields, 10), Required(fields, 20)), arcRadius, startDeg, endDeg));
                        break;
                    case "LWPOLYLINE":
                        var vertices = new List<Point2>();
                        var bulges = new List<double>();
                        double? x = null;
                        double? y = null;
                        double bulge = 0;
                        void FinishVertex()
                        {
                            if (x is null || y is null) throw new FormatException("Polyline vertex missing coordinate");
                            vertices.Add(new Point2(x.Value, y.Value));
                            bulges.Add(bulge);
                            x = null; y = null; bulge = 0;
                        }
                        foreach (var field in fields)
                        {
                            if (field.Code == 10)
                            {
                                if (x is not null) FinishVertex();
                                x = Number(field);
                            }
                            else if (field.Code == 20)
                            {
                                if (x is null || y is not null) throw new FormatException("Polyline Y out of sequence");
                                y = Number(field);
                            }
                            else if (field.Code == 42)
                            {
                                if (x is null || y is null) throw new FormatException("Polyline bulge before vertex");
                                bulge = Number(field);
                            }
                        }
                        if (x is not null) FinishVertex();
                        if (vertices.Count < 2) throw new FormatException("Invalid polyline vertices");
                        var count = Integer(fields, 90, vertices.Count);
                        if (count != vertices.Count) throw new FormatException("Polyline vertex count mismatch");
                        var closed = (Integer(fields, 70) & 1) != 0;
                        if (bulges.Any(b => Math.Abs(b) > 1e-12))
                        {
                            var curved = new DxfBulgedPolyline(layer, vertices, bulges, closed);
                            DxfBulgePreview.Build(curved); // reject malformed data before exposing preview
                            shapes.Add(curved);
                            warnings.Add("Bulged LWPOLYLINE is preview-only: CAD/CAM geometry is not yet certified.");
                        }
                        else shapes.Add(new DxfPolyline(layer, vertices, closed));
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                warnings.Add($"Entity {type} skipped: {ex.Message}");
            }
            if (shapes.Count > 10000) throw new ArgumentException("Too many shapes for preview.");
        }
        if (units != 4)
            warnings.Add(units == null || units == 0
                ? "DXF unit is unspecified. Confirm units before importing into millimetre jobs."
                : $"DXF $INSUNITS={units}. Only millimetres (4) may be used in cutting jobs without conversion.");
        return new(shapes, warnings, units);
    }
}
