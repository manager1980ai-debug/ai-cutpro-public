using System.Globalization;

namespace AICutPro.Core;

/// <summary>
/// Narrow, deterministic text-to-2D sketch. One rectangular plate with optional
/// circular through-holes; the 3D counterpart is a parametric extrusion, not
/// a triangulated B-rep or manufacturing-certified solid.
/// </summary>
public sealed record SketchThroughHole(double Xmm,double Ymm,double RadiusMm);
public sealed record ExtrudedPlatePreview(
    double WidthMm, double HeightMm, double ThicknessMm,
    IReadOnlyList<SketchThroughHole> Holes,
    DxfImportResult Drawing2D, double NetAreaMm2, double VolumeMm3)
{
    public bool CanStartMachine => false;
    public bool IsManufacturingValidated => false;
}

public static class TextPlateSketch
{
    private static double Positive(string token,string name)
    {
        if(!double.TryParse(token,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||
           !double.IsFinite(n)||n<=0||n>100000)
            throw new FormatException($"Invalid {name}; use a positive millimetre value.");
        return n;
    }
    private static double Coordinate(string token,string name)
    {
        if(!double.TryParse(token,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||
           !double.IsFinite(n)||n<0||n>100000)
            throw new FormatException($"Invalid {name}; use a nonnegative millimetre value.");
        return n;
    }

    /// <example>PLATE 200 100 5; HOLE 50 50 10; HOLE 150 50 10</example>
    public static ExtrudedPlatePreview Parse(string description)
    {
        if(string.IsNullOrWhiteSpace(description)||description.Length>16000)
            throw new FormatException("Empty or overly large sketch description.");
        var commands=description.Replace("\r","").Split([';','\n'],
            StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(commands.Length is < 1 or > 201)
            throw new FormatException("Too many sketch commands.");
        string[] Tokens(string s)=>s.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        var header=Tokens(commands[0]);
        if(header.Length!=4 || !header[0].Equals("PLATE",StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Expected PLATE width height thickness in millimetres.");
        var width=Positive(header[1],"plate width");
        var height=Positive(header[2],"plate height");
        var thickness=Positive(header[3],"plate thickness");
        var holes=new List<SketchThroughHole>();
        for(int i=1;i<commands.Length;i++)
        {
            var args=Tokens(commands[i]);
            if(args.Length!=4||!args[0].Equals("HOLE",StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Command {i+1}: expected HOLE x y radius.");
            var x=Coordinate(args[1],"hole X");
            var y=Coordinate(args[2],"hole Y");
            var r=Positive(args[3],"hole radius");
            // Strict clearance is necessary even for a read-only material model.
            if(x<=r||x>=width-r||y<=r||y>=height-r)
                throw new FormatException($"Hole {i} touches or crosses the plate boundary.");
            foreach(var prior in holes)
            {
                var dx=x-prior.Xmm;var dy=y-prior.Ymm;
                var distance=Math.Sqrt(dx*dx+dy*dy);
                if(distance<=r+prior.RadiusMm)
                    throw new FormatException($"Hole {i} intersects or touches another hole.");
            }
            holes.Add(new(x,y,r));
        }
        double area=width*height-holes.Sum(h=>Math.PI*h.RadiusMm*h.RadiusMm);
        double volume=area*thickness;
        if(!double.IsFinite(area)||area<=0||!double.IsFinite(volume)||volume<=0)
            throw new FormatException("Nonpositive or overflowing solid volume.");
        var shapes=new List<DxfShape>
        {
            new DxfLine("CUT",new(0,0),new(width,0)),
            new DxfLine("CUT",new(width,0),new(width,height)),
            new DxfLine("CUT",new(width,height),new(0,height)),
            new DxfLine("CUT",new(0,height),new(0,0))
        };
        shapes.AddRange(holes.Select(h=>
            (DxfShape)new DxfCircle("CUT",new(h.Xmm,h.Ymm),h.RadiusMm)));
        var drawing=new DxfImportResult(shapes,[],4);
        return new(width,height,thickness,holes,drawing,area,volume);
    }
}
