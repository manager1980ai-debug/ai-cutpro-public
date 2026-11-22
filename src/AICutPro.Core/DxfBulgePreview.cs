namespace AICutPro.Core;

/// <summary>DXF LWPOLYLINE containing exact bulge arcs. Read-only preview, not manufacturing CAD.</summary>
public sealed record DxfBulgedPolyline(string Layer, IReadOnlyList<Point2> Vertices,
    IReadOnlyList<double> Bulges, bool Closed) : DxfShape(Layer);

public static class DxfBulgePreview
{
    /// <summary>Compute analytic line or signed circular arc segments from each bulge = tan(sweep/4).</summary>
    public static PreviewPath Build(DxfBulgedPolyline poly)
    {
        if (poly.Vertices.Count < 2 || poly.Bulges.Count != poly.Vertices.Count)
            throw new ArgumentException("Invalid bulged polyline lengths.");
        var segments = new List<PreviewSegment>();
        var count = poly.Closed ? poly.Vertices.Count : poly.Vertices.Count - 1;
        if (!poly.Closed && Math.Abs(poly.Bulges[^1]) > 1e-12)
            throw new ArgumentException("Open polyline cannot use a trailing bulge.");
        for (var i = 0; i < count; i++)
        {
            var start = poly.Vertices[i];
            var end = poly.Vertices[(i + 1) % poly.Vertices.Count];
            var bulge = poly.Bulges[i];
            if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
                !double.IsFinite(end.X) || !double.IsFinite(end.Y) || !double.IsFinite(bulge))
                throw new ArgumentException("Nonfinite polyline value.");
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(chord) || chord <= 1e-10 || Math.Abs(bulge) > 1e6)
                throw new ArgumentException("Invalid bulge or degenerate chord.");
            if (Math.Abs(bulge) <= 1e-12)
            {
                segments.Add(new PreviewLine(new(start.X, start.Y), new(end.X, end.Y)));
                continue;
            }
            var sweep = 4 * Math.Atan(bulge);
            // Offset from chord midpoint along the left normal; works for CW and major arcs.
            var offset = chord * (1 - bulge * bulge) / (4 * bulge);
            var cx = (start.X + end.X) / 2 - dy / chord * offset;
            var cy = (start.Y + end.Y) / 2 + dx / chord * offset;
            var radius = Math.Sqrt((start.X - cx) * (start.X - cx) + (start.Y - cy) * (start.Y - cy));
            var angle = Math.Atan2(start.Y - cy, start.X - cx) * 180 / Math.PI;
            if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(angle))
                throw new ArgumentException("Invalid calculated bulge arc.");
            segments.Add(new PreviewArc(new(cx, cy), radius, angle, sweep * 180 / Math.PI));
        }
        return new PreviewPath(segments, poly.Closed);
    }
}
