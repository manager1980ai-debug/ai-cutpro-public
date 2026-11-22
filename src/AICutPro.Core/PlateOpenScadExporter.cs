using System.Globalization;
using System.Text;

namespace AICutPro.Core;

/// <summary>
/// Exports a narrowly defined 3D constructive-solid-geometry model:
/// one rectangular plate with round through-holes. OpenSCAD can render the
/// resulting .scad file as a true 3D solid and export STL separately.
/// This is CAD geometry only, NOT a toolpath or manufacturing approval.
/// </summary>
public static class PlateOpenScadExporter
{
    public static string Export(ExtrudedPlatePreview plate)
    {
        ArgumentNullException.ThrowIfNull(plate);
        static bool Valid(double value) =>
            double.IsFinite(value) && value > 0 && value <= 100_000;
        if (!Valid(plate.WidthMm) || !Valid(plate.HeightMm) ||
            !Valid(plate.ThicknessMm) || plate.Holes.Count > 200)
            throw new FormatException("Unsupported 3D plate dimensions.");
        if (!double.IsFinite(plate.NetAreaMm2) ||
            !double.IsFinite(plate.VolumeMm3) ||
            plate.NetAreaMm2 <= 0 || plate.VolumeMm3 <= 0)
            throw new FormatException("Nonfinite or nonpositive 3D area or volume.");
        var validated = TextPlateSketch.Parse(
            "PLATE " + N(plate.WidthMm) + " " + N(plate.HeightMm) +
            " " + N(plate.ThicknessMm) +
            string.Concat(plate.Holes.Select(h =>
                "; HOLE " + N(h.Xmm) + " " + N(h.Ymm) + " " + N(h.RadiusMm))));
        var epsilonArea = Math.Max(1e-6, validated.NetAreaMm2 * 1e-10);
        if (Math.Abs(validated.NetAreaMm2 - plate.NetAreaMm2) > epsilonArea ||
            Math.Abs(validated.VolumeMm3 - plate.VolumeMm3) >
                Math.Max(1e-5,validated.VolumeMm3 * 1e-10))
            throw new FormatException("3D dimensions do not match the verified 2D sketch.");
        var verifiedRings = PreviewDxfExporter.ValidateDocument(plate.Drawing2D);
        var topology = PreviewContourTopology.Analyze(verifiedRings);
        if (!topology.IsValid ||
            Math.Abs(topology.NetAreaMm2 - plate.NetAreaMm2) > epsilonArea ||
            verifiedRings.Count != plate.Holes.Count + 1)
            throw new FormatException("3D solid disagrees with source 2D contours.");
        PreviewDxfRoundTripVerifier.Verify(
            verifiedRings, PreviewDxfExporter.Export(verifiedRings));

        var result = new StringBuilder();
        result.AppendLine("// AI CutPro: parametric OpenSCAD solid, units: millimetres");
        result.AppendLine("// PREVIEW ONLY. NOT CAM, NOT a machine program.");
        result.AppendLine("// Open with OpenSCAD, F6 renders solid; export mesh there.");
        result.AppendLine("$fn = 128;");
        result.AppendLine("difference() {");
        result.Append("  cube([").Append(N(plate.WidthMm)).Append(", ")
              .Append(N(plate.HeightMm)).Append(", ")
              .Append(N(plate.ThicknessMm)).AppendLine("], center = false);");
        foreach (var hole in plate.Holes)
        {
            // Use exactly the thickness and centered placement to form a
            // genuine through-hole without extending outside the solid.
            result.Append("  translate([")
                  .Append(N(hole.Xmm)).Append(", ")
                  .Append(N(hole.Ymm)).Append(", 0]) ")
                  .Append("cylinder(h = ").Append(N(plate.ThicknessMm))
                  .Append(", r = ").Append(N(hole.RadiusMm))
                  .AppendLine(", center = false);");
        }
        result.AppendLine("}");
        return result.ToString();
    }

    private static string N(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);
}
