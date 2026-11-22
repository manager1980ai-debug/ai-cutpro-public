using System.Text.Json;
using AICutPro.Core;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("CHECK FAILED: " + message);
}

// B1.1: the offline diagnostic knowledge base is stable, localised and
// provides honest in-app guidance without claiming unavailable repair tools.
var diagnosticCodes = DxfDiagnosticCatalog.All.Select(e => e.Code).ToArray();
Assert(diagnosticCodes.Distinct(StringComparer.Ordinal).Count() == diagnosticCodes.Length &&
       diagnosticCodes.Length >= 5, "DXF diagnostic identifiers unique");
foreach (var entry in DxfDiagnosticCatalog.All)
    Assert(!string.IsNullOrWhiteSpace(entry.TitleRu) &&
           !string.IsNullOrWhiteSpace(entry.TitleEn) &&
           !string.IsNullOrWhiteSpace(entry.ExplanationRu) &&
           !string.IsNullOrWhiteSpace(entry.ExplanationEn) &&
           !string.IsNullOrWhiteSpace(entry.AdviceRu) &&
           !string.IsNullOrWhiteSpace(entry.AdviceEn) &&
           !entry.RepairAvailable,
           "Every DXF diagnostic has RU/EN explanations and honest repair status");
Assert(DxfDiagnosticCatalog.Classify(
    new FormatException("Truncated DXF group pair.")).Code == "DXF-1003",
    "Malformed DXF resolves to stable structure-error code");
Assert(DxfDiagnosticCatalog.Classify(
    new FormatException("Binary DXF is not supported; export as ASCII DXF.")).Code == "DXF-1002",
    "Binary DXF resolves to format error code");
Assert(DxfDiagnosticCatalog.Classify(
    new FormatException("Unsupported DXF $DWGCODEPAGE=ANSI_99999; choose an explicitly supported encoding.")).Code == "DXF-1004",
    "Unsupported code page resolves to encoding diagnostic");
Assert(DxfDiagnosticCatalog.Classify(
    new FormatException("DXF file must contain 1..8000000 bytes.")).Code == "DXF-1001",
    "Size errors receive explicit diagnostic code");
Assert(DxfDiagnosticCatalog.Classify(new IOException("read error")).Code == "DXF-1099",
    "Unknown exceptions are not mistaken for a repairable format problem");
Assert(DxfDiagnosticCatalog.ExplainRu(
    new FormatException("Truncated DXF pair")).Contains("Что делать:",StringComparison.Ordinal),
    "Import diagnostic includes actionable advice in Russian");

// B1.1 geometry guidance: classify only observed warning kinds, keep
// unknown warnings blocked, and never advertise an unavailable repair.
var geometryCases = new (string Issue, string Code)[]
{
    ("Layer CUT: Open chain or branching segment graph.", "GEO-3001"),
    ("Layer CUT: Repeated segment.", "GEO-3002"),
    ("Layer CUT: Contour geometric intersection: crossing edges.", "GEO-3003"),
    ("Круглые отверстия пересекаются или касаются.", "GEO-3004"),
    ("Окружность не находится строго внутри одной детали.", "GEO-3004"),
    ("Непроверенные данные из нового типа объекта.", "GEO-3099")
};
foreach (var (issue, code) in geometryCases)
{
    var found = DxfDiagnosticCatalog.ClassifyGeometry(issue);
    Assert(found.Code == code, "Geometry warning resolves to expected code: " + issue);
    var explanation = DxfDiagnosticCatalog.ExplainGeometryRu(issue);
    Assert(explanation.Contains(code, StringComparison.Ordinal) &&
           explanation.Contains("Что делать:", StringComparison.Ordinal) &&
           explanation.Contains(issue, StringComparison.Ordinal),
           "Geometry warning keeps a stable identifier, advice, and original detail");
}
Assert(DxfDiagnosticCatalog.All.Count(e => e.Code.StartsWith("GEO-", StringComparison.Ordinal)) == 5,
       "Geometry diagnostic catalogue contains exactly five new entries");

// Project format: round trip, unsupported versions, and malformed dimensions.
var project = CutProject.CreateDefault() with
{
    Name = "Проверка сохранения",
    Sheet = new Sheet(2500, 1250),
    Parts = [new Part("Фланец", 120, 95, 3)],
    Material = "S355",
    ThicknessMm = 5
};
var reloaded = CutProjectSerializer.Deserialize(CutProjectSerializer.Serialize(project));
Assert(reloaded.Name == project.Name && reloaded.Sheet.WidthMm == 2500 &&
       reloaded.Parts[0].Name == "Фланец" && reloaded.Parts[0].Quantity == 3 &&
       reloaded.Material == "S355" && reloaded.ThicknessMm == 5,
       "Project survives save/open round trip");
static bool RejectProject(string content)
{
    try { CutProjectSerializer.Deserialize(content); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectProject("{invalid"), "Invalid project JSON rejected");
Assert(RejectProject(JsonSerializer.Serialize(project with { FormatVersion = 99 })),
       "Unknown project version rejected");
Assert(RejectProject(JsonSerializer.Serialize(project with { Sheet = new Sheet(-1, 1250) })),
       "Invalid sheet dimension rejected");
Assert(RejectProject(JsonSerializer.Serialize(project with { Parts = [new Part("Фланец", 120, 95, 0)] })),
       "Invalid part quantity rejected");

var result = ShelfNester.Arrange(SampleJob.Sheet, SampleJob.Parts);
Assert(result.Placements.Count == 10, "All sample parts fit");
Assert(result.Warnings.Count == 0, "No warnings for sample");
Assert(result.UsedAreaMm2 == 10 * 180 * 220, "Used area");
Assert(result.UtilizationPercent > 0 && result.UtilizationPercent <= 100, "Utilization percentage");

foreach (var part in result.Placements)
{
    Assert(part.Xmm >= 0 && part.Ymm >= 0, "Nonnegative origin");
    Assert(part.Xmm + part.WidthMm <= result.Sheet.WidthMm, "Part fits sheet width");
    Assert(part.Ymm + part.HeightMm <= result.Sheet.HeightMm, "Part fits sheet height");
}
for (int i = 0; i < result.Placements.Count; i++)
for (int j = i + 1; j < result.Placements.Count; j++)
{
    var a = result.Placements[i];
    var b = result.Placements[j];
    Assert(a.Xmm + a.WidthMm <= b.Xmm || b.Xmm + b.WidthMm <= a.Xmm ||
           a.Ymm + a.HeightMm <= b.Ymm || b.Ymm + b.HeightMm <= a.Ymm,
           "Placements do not overlap");
}
var small = ShelfNester.Arrange(new Sheet(100, 100), [new Part("Big", 200, 200, 1)]);
Assert(small.Placements.Count == 0 && small.Warnings.Count == 1, "Oversized part is rejected");
var threw = false;
try { ShelfNester.Arrange(new Sheet(0, 10), SampleJob.Parts); }
catch (ArgumentOutOfRangeException) { threw = true; }
Assert(threw, "Invalid sheet is rejected");

var dxf = string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","LINE","8","CUT","10","0","20","0","11","180","21","0",
    "0","CIRCLE","8","HOLES","10","45","20","70","40","12",
    "0","LWPOLYLINE","8","CUT","90","3","70","1","10","0","20","0",
    "10","180","20","0","10","90","20","220",
    "0","ENDSEC","0","EOF"
});
var preview = DxfReader.Parse(dxf);
Assert(preview.InsUnits == 4, "DXF units read");
Assert(preview.Shapes.Count == 3, "Supported DXF entities parsed");
Assert(preview.Warnings.Count == 0, "Valid DXF has no warnings");
Assert(preview.Shapes[0] is DxfLine { Layer: "CUT" }, "Line layer");
Assert(preview.Shapes[1] is DxfCircle { Radius: 12 }, "Circle radius");
Assert(preview.Shapes[2] is DxfPolyline { Closed: true }, "Closed polyline");
// B1: ambiguity in scalar DXF coordinates, radius or unit declarations
// must never silently select one value and produce editable geometry.
static string SafeEntityDxf(params string[] entityPairs) =>
    string.Join("\n", new[]
    {
        "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
        "0","SECTION","2","ENTITIES"
    }.Concat(entityPairs).Concat(new[] { "0","ENDSEC","0","EOF" }));
var ambiguousLine = DxfReader.Parse(SafeEntityDxf(
    "0","LINE","10","0","10","100","20","0","11","10","21","0"));
Assert(ambiguousLine.Shapes.Count == 0 &&
       ambiguousLine.Warnings.Any(w => w.Contains("Duplicate DXF scalar group 10")),
       "Duplicate DXF X coordinate must skip the entire ambiguous LINE");
var ambiguousCircle = DxfReader.Parse(SafeEntityDxf(
    "0","CIRCLE","10","5","20","5","40","10","40","20"));
Assert(ambiguousCircle.Shapes.Count == 0 &&
       ambiguousCircle.Warnings.Any(w => w.Contains("Duplicate DXF scalar group 40")),
       "Duplicate DXF radius must skip the entire ambiguous CIRCLE");
var ambiguousPolyline = DxfReader.Parse(SafeEntityDxf(
    "0","LWPOLYLINE","90","3","90","4","70","1",
    "10","0","20","0","10","10","20","0","10","0","20","10"));
Assert(ambiguousPolyline.Shapes.Count == 0 &&
       ambiguousPolyline.Warnings.Any(w => w.Contains("Duplicate DXF integer group 90")),
       "Duplicate DXF vertex count must not create an editable polyline");
bool conflictingUnitsRejected = false;
try
{
    DxfReader.Parse(string.Join("\n", new[]
    {
        "0","SECTION","2","HEADER","9","$INSUNITS","70","4",
        "9","$INSUNITS","70","1","0","ENDSEC","0","SECTION",
        "2","ENTITIES","0","ENDSEC","0","EOF"
    }));
}
catch (FormatException) { conflictingUnitsRejected = true; }
Assert(conflictingUnitsRejected,
       "Conflicting repeated $INSUNITS must reject DXF instead of guessing scale");

bool duplicateUnitsValueRejected = false;
try
{
    DxfReader.Parse(string.Join("\n", new[]
    {
        "0","SECTION","2","HEADER","9","$INSUNITS","70","4",
        "70","1","0","ENDSEC","0","SECTION",
        "2","ENTITIES","0","ENDSEC","0","EOF"
    }));
}
catch (FormatException) { duplicateUnitsValueRejected = true; }
Assert(duplicateUnitsValueRejected,
       "Two unit values within one DXF header variable must fail closed");
var repeatedVertexCodes = DxfReader.Parse(SafeEntityDxf(
    "0","LWPOLYLINE","90","3","70","1",
    "10","0","20","0","10","10","20","0","10","0","20","10"));
Assert(repeatedVertexCodes.Shapes.Count == 1 &&
       repeatedVertexCodes.Shapes[0] is DxfPolyline { Closed: true } &&
       repeatedVertexCodes.Warnings.Count == 0,
       "Ordinary repeated DXF polyline vertex coordinate groups remain supported");


// B1: truncated but syntactically paired DXF can show diagnostic geometry,
// but must never be treated as complete editable or CAM-ready geometry.
// B1: EOF is mandatory even when all visible geometry and sections close.
var missingEof = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10",
    "0","ENDSEC"
}));
Assert(missingEof.Shapes.Count == 1 &&
       missingEof.Warnings.Any(w=>w.Contains("missing its mandatory EOF")),
       "DXF lacking EOF retains diagnostic geometry but warns about truncation");
var missingEofPreflight = GeometryPreflight.Inspect(missingEof,new Sheet(100,100));
Assert(missingEofPreflight.Status == GeometryPreflightStatus.Rejected &&
       missingEofPreflight.BlockingIssues.Any(x=>x.Contains("missing its mandatory EOF")),
       "Missing EOF must block geometric preflight");
bool missingEofExportRejected = false;
try { PreviewDxfExporter.ValidateDocument(missingEof); }
catch (FormatException) { missingEofExportRejected = true; }
Assert(missingEofExportRejected, "Missing EOF must block verified DXF export");
var completeEof = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10",
    "0","ENDSEC","0","EOF"
}));
Assert(completeEof.Shapes.Count == 1 && completeEof.Warnings.Count == 0,
       "Complete DXF with an explicit EOF remains accepted");

var unclosedSection = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10"
}));
Assert(unclosedSection.Shapes.Count == 1 &&
       unclosedSection.Warnings.Any(w=>w.Contains("unterminated or malformed SECTION")),
       "Truncated ENTITIES section remains diagnostic-only with a blocking warning");
var unclosedPreflight = GeometryPreflight.Inspect(unclosedSection,new Sheet(100,100));
Assert(unclosedPreflight.Status == GeometryPreflightStatus.Rejected &&
       unclosedPreflight.BlockingIssues.Any(issue => issue.Contains("unterminated")) &&
       !unclosedPreflight.CanGenerateMachineCommands,
       "Incomplete DXF must be rejected by preflight with an explicit reason");
bool unclosedExportBlocked=false;
try { PreviewDxfExporter.ValidateDocument(unclosedSection); }
catch (FormatException) {unclosedExportBlocked=true;}
Assert(unclosedExportBlocked,
       "Incomplete DXF cannot be saved as a verified edited drawing");

// B1 structural DXF guards: nested sections, unmatched terminators and
// geometry after EOF must never be accepted for edited drawing export.
var nestedSections = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4",
    "0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10",
    "0","ENDSEC","0","EOF"
}));
Assert(nestedSections.Warnings.Any(w=>w.Contains("nested or malformed SECTION")),
       "Nested DXF sections are flagged and cannot silently validate");
bool nestedExportBlocked = false;
try { PreviewDxfExporter.ValidateDocument(nestedSections); }
catch (FormatException) { nestedExportBlocked = true; }
Assert(nestedExportBlocked, "Nested DXF sections must block verified export");
var unmatchedEndsec = DxfReader.Parse(string.Join("\n", new[]
{
    "0","ENDSEC","0","SECTION","2","HEADER","9","$INSUNITS","70","4",
    "0","ENDSEC","0","SECTION","2","ENTITIES","0","ENDSEC","0","EOF"
}));
Assert(unmatchedEndsec.Warnings.Any(w=>w.Contains("ENDSEC has no matching SECTION")),
       "Unmatched DXF ENDSEC is diagnosed explicitly");
var prematureEof = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4",
    "0","ENDSEC","0","SECTION","2","ENTITIES",
    "0","EOF",
    "0","LINE","10","0","20","0","11","10","21","0"
}));
Assert(prematureEof.Shapes.Count == 0 &&
       prematureEof.Warnings.Any(w=>w.Contains("EOF occurs before ENDSEC")) &&
       prematureEof.Warnings.Any(w=>w.Contains("data after EOF")),
       "Premature EOF prevents trailing entities from entering the DXF model");
var trailingEofData = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10",
    "0","ENDSEC","0","EOF",
    "0","LINE","10","0","20","0","11","10","21","0"
}));
Assert(trailingEofData.Shapes.Count == 1 &&
       trailingEofData.Warnings.Any(w=>w.Contains("data after EOF")),
       "Data after EOF is not parsed as additional geometry");
bool trailingExportBlocked = false;
try { PreviewDxfExporter.ValidateDocument(trailingEofData); }
catch (FormatException) { trailingExportBlocked = true; }
Assert(trailingExportBlocked,
       "DXF with data after EOF cannot pass verified export");

// B1: unknown LWPOLYLINE flags must not be masked into a safe closed ring.
var unsupportedPolylineFlags = DxfReader.Parse(SafeEntityDxf(
    "0","LWPOLYLINE","90","4","70","129",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10"));
Assert(unsupportedPolylineFlags.Shapes.Count == 0 &&
       unsupportedPolylineFlags.Warnings.Any(w => w.Contains("Unsupported LWPOLYLINE flags")),
       "Unsupported LWPOLYLINE flags must skip the complete entity");
var supportedPolylineFlag = DxfReader.Parse(SafeEntityDxf(
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10"));
Assert(supportedPolylineFlag.Shapes.Count == 1 &&
       supportedPolylineFlag.Shapes[0] is DxfPolyline { Closed: true } &&
       supportedPolylineFlag.Warnings.Count == 0,
       "Supported closed LWPOLYLINE remains readable after stricter flag parsing");

// B1: a second ENTITIES section is ambiguous. Preserve geometry for
// diagnostic preview, but block topology-verified export and CAM preflight.
var duplicateEntitiesSections = DxfReader.Parse(string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","90","4","70","1",
    "10","0","20","0","10","10","20","0",
    "10","10","20","10","10","0","20","10",
    "0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","CIRCLE","10","5","20","5","40","1",
    "0","ENDSEC","0","EOF"
}));
Assert(duplicateEntitiesSections.Shapes.Count == 2 &&
       duplicateEntitiesSections.Warnings.Any(w => w.Contains("multiple ENTITIES sections")),
       "Multiple ENTITIES sections remain visible for diagnosis but are warned");
var duplicateEntitiesPreflight = GeometryPreflight.Inspect(
    duplicateEntitiesSections, new Sheet(100,100));
Assert(duplicateEntitiesPreflight.Status == GeometryPreflightStatus.Rejected &&
       duplicateEntitiesPreflight.BlockingIssues.Any(w => w.Contains("multiple ENTITIES sections")),
       "Multiple ENTITIES sections cannot pass geometry preflight");
bool duplicateEntitiesExportRejected = false;
try { PreviewDxfExporter.ValidateDocument(duplicateEntitiesSections); }
catch (FormatException) { duplicateEntitiesExportRejected = true; }
Assert(duplicateEntitiesExportRejected,
       "Multiple ENTITIES sections must fail verified geometry export");

// B1: a single LWPOLYLINE vertex must never contain two bulge values.
// Selecting the last value silently alters the curvature and material area.
var duplicatedVertexBulge = DxfReader.Parse(SafeEntityDxf(
    "0","LWPOLYLINE","90","3","70","1",
    "10","0","20","0","42","0.2","42","0.5",
    "10","100","20","0","10","50","20","100"));
Assert(duplicatedVertexBulge.Shapes.Count == 0 &&
       duplicatedVertexBulge.Warnings.Any(w=>w.Contains("Duplicate LWPOLYLINE vertex bulge")),
       "Two bulge values on one DXF vertex must reject the entire ambiguous polyline");
var repeatedVertexBulges = DxfReader.Parse(SafeEntityDxf(
    "0","LWPOLYLINE","90","3","70","1",
    "10","0","20","0","42","0.2",
    "10","100","20","0","42","0.3",
    "10","50","20","100","42","0"));
Assert(repeatedVertexBulges.Shapes.Count==1 &&
       repeatedVertexBulges.Shapes[0] is DxfBulgedPolyline &&
       repeatedVertexBulges.Warnings.Count==1 &&
       repeatedVertexBulges.Warnings[0].Contains("preview-only"),
       "Separate per-vertex bulges remain valid and preview-only");
var duplicatedBulgePreflight = GeometryPreflight.Inspect(
    duplicatedVertexBulge,new Sheet(200,200));
Assert(duplicatedBulgePreflight.Status==GeometryPreflightStatus.Rejected &&
       duplicatedBulgePreflight.BlockingIssues.Any(x=>x.Contains("Duplicate LWPOLYLINE vertex bulge")),
       "Ambiguous bulge geometry cannot pass manufacturing preflight");
bool duplicatedBulgeExportBlocked = false;
try { PreviewDxfExporter.ValidateDocument(duplicatedVertexBulge); }
catch (FormatException) { duplicatedBulgeExportBlocked = true; }
Assert(duplicatedBulgeExportBlocked,
       "Ambiguous per-vertex curvature must block verified DXF export");

var unknownUnit = DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF");
Assert(unknownUnit.Warnings.Any(w => w.Contains("unspecified")), "Unknown units warning");
var rejectedArc = DxfReader.Parse("0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nLWPOLYLINE\n90\n2\n10\n0\n20\n0\n42\n0.5\n10\n10\n20\n0\n0\nENDSEC");
Assert(rejectedArc.Shapes.Count == 1 && rejectedArc.Shapes[0] is DxfBulgedPolyline && rejectedArc.Warnings.Any(),
       "Bulge arc preserved for inspection, never silently admitted to CAM");
var curvedPreview = DxfBulgePreview.Build((DxfBulgedPolyline)rejectedArc.Shapes[0]);
Assert(curvedPreview.Segments[0] is PreviewArc, "Bulge produces analytic preview arc");
Assert(!GeometryPreflight.Inspect(rejectedArc, new Sheet(100, 100)).CanGenerateMachineCommands,
       "Bulged DXF cannot directly generate machine commands");


static DxfImportResult Document(params DxfShape[] shapes) => new(shapes, [], 4);
static DxfPolyline Closed(string layer, params Point2[] pts) => new(layer, pts, true);
var rectangle = Closed("CUT",new Point2(0,0),new Point2(200,0),new Point2(200,100),new Point2(0,100));
var innerHole = Closed("HOLE",new Point2(20,20),new Point2(40,20),new Point2(40,40),new Point2(20,40));
var circleHole = new DxfCircle("HOLE",new Point2(100,50),10);
var analyzed = ProfileAnalyzer.Analyze(Document(rectangle,innerHole,circleHole));
Assert(analyzed.Profiles.Count == 1, "One outer profile");
Assert(analyzed.Profiles[0].InnerPolygons.Count == 1, "One inner polygon");
Assert(analyzed.Profiles[0].CircularHoles.Count == 1, "One circular hole");
Assert(Math.Abs(analyzed.Profiles[0].OuterAreaMm2-20000)<1e-8,"Correct outer area");
Assert(Math.Abs(analyzed.Profiles[0].NetAreaMm2-(20000-400-Math.PI*100))<1e-8,"Correct net area");
var separate = ProfileAnalyzer.Analyze(Document(rectangle,
    Closed("CUT",new Point2(300,0),new Point2(350,0),new Point2(350,50),new Point2(300,50))));
Assert(separate.Profiles.Count==2,"Two separate parts");
var touching=ProfileAnalyzer.Analyze(Document(rectangle,
    Closed("CUT",new Point2(200,50),new Point2(250,50),new Point2(250,70),new Point2(200,70))));
Assert(touching.Profiles.Count==0 && touching.Warnings.Count>0,"Touching contours rejected");
var crossing=ProfileAnalyzer.Analyze(Document(
    Closed("CUT",new Point2(0,0),new Point2(100,100),new Point2(0,100),new Point2(100,0))));
Assert(crossing.Profiles.Count==0 && crossing.Warnings.Count>0,"Self intersecting ring rejected");
var outsideHole=ProfileAnalyzer.Analyze(Document(rectangle,new DxfCircle("HOLE",new Point2(190,50),20)));
Assert(outsideHole.Profiles.Count==0 && outsideHole.Warnings.Count>0,"Hole crossing outside boundary rejected");
var unverifiedUnits=ProfileAnalyzer.Analyze(new DxfImportResult([rectangle],[],null));
Assert(unverifiedUnits.Profiles.Count==0,"Units must be millimetres");


var arcsDxf = string.Join("\n", new[]
{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4","0","ENDSEC",
    "0","SECTION","2","ENTITIES",
    "0","ARC","8","CURVES","10","100","20","100","40","25","50","350","51","10",
    "0","ENDSEC","0","EOF"
});
var arcsResult = DxfReader.Parse(arcsDxf);
Assert(arcsResult.Warnings.Count == 0, "Valid ARC DXF has no warnings");
Assert(arcsResult.Shapes.Count == 1 && arcsResult.Shapes[0] is DxfArc, "ARC recognized");
var arc = (DxfArc)arcsResult.Shapes[0];
var arcPreview = DxfArcPreview.Sample(arc);
Assert(arcPreview.Count > 2, "ARC preview has multiple points");
Assert(Math.Abs(arcPreview[0].X - arcPreview[^1].X) < 1e-8, "Wrapped ARC endpoints symmetric");
Assert(arcPreview.All(p => Math.Abs(Math.Sqrt((p.X-100)*(p.X-100)+(p.Y-100)*(p.Y-100))-25)<1e-8), "ARC preview points on circle");
var arcOnly = ProfileAnalyzer.Analyze(arcsResult);
Assert(arcOnly.Profiles.Count == 0, "Preview-only ARC never converted to manufacturing profile");
var malformedArc = DxfReader.Parse(arcsDxf.Replace("\n50\n350\n51\n10", "\n50\n350\n51\n350"));
Assert(malformedArc.Shapes.Count == 0 && malformedArc.Warnings.Count > 0, "Zero sweep ARC rejected");


// Geometry-only path preview: arc length, segment joining and strict rejection.
var quarterCircle = new PreviewArc(new PreviewPoint(0, 0), 10, 0, 90);
var virtualArc = VirtualToolpath.Sample(new PreviewPath([quarterCircle], false), 0.01);
Assert(virtualArc.IsValidForPreview && virtualArc.Points.Count > 2, "Arc preview samples created");
Assert(Math.Abs(virtualArc.LengthMm - Math.PI * 5) < 1e-8, "Arc uses true circular length");
Assert(Math.Abs(virtualArc.Points[0].X - 10) < 1e-8, "Arc starting point");
Assert(Math.Abs(virtualArc.Points[^1].Y - 10) < 1e-8, "Arc endpoint");
Assert(virtualArc.Warnings.Any(w => w.Contains("PREVIEW ONLY")), "Preview is explicitly non-manufacturing");

var continuous = VirtualToolpath.Sample(new PreviewPath([
    new PreviewLine(new PreviewPoint(0,0), new PreviewPoint(10,0)),
    new PreviewArc(new PreviewPoint(10,10), 10, 270, 90)
], false));
Assert(continuous.IsValidForPreview, "Joined line-arc chain accepted");
Assert(Math.Abs(continuous.LengthMm - (10 + 5 * Math.PI)) < 1e-8, "Combined line and arc lengths");
var disconnected = VirtualToolpath.Sample(new PreviewPath([
    new PreviewLine(new PreviewPoint(0,0), new PreviewPoint(1,0)),
    new PreviewLine(new PreviewPoint(2,0), new PreviewPoint(3,0))
], false));
Assert(!disconnected.IsValidForPreview, "Disconnected chain rejected");
var unclosed = VirtualToolpath.Sample(new PreviewPath([
    new PreviewLine(new PreviewPoint(0,0), new PreviewPoint(1,0))
], true));
Assert(!unclosed.IsValidForPreview, "Unclosed contour rejected");
var badRadius = VirtualToolpath.Sample(new PreviewPath([
    new PreviewArc(new PreviewPoint(0,0), -1, 0, 90)
], false));
Assert(!badRadius.IsValidForPreview, "Negative radius rejected");
var badNumber = VirtualToolpath.Sample(new PreviewPath([
    new PreviewLine(new PreviewPoint(double.NaN,0), new PreviewPoint(1,0))
], false));
Assert(!badNumber.IsValidForPreview, "Nonfinite coordinates rejected");


static DxfLine Segment(Point2 a, Point2 b, string layer="CUT") => new(layer,a,b);
var a0=new Point2(0,0); var a1=new Point2(100,0);
var a2=new Point2(100,50);var a3=new Point2(0,50);
var shuffledLines=new DxfLine[] {
    Segment(a2,a3), Segment(a0,a3), Segment(a1,a0), Segment(a1,a2)
};
var assembledRectangle=LineContourAssembler.Assemble(shuffledLines);
Assert(assembledRectangle.Complete && assembledRectangle.ClosedContours.Count==1,"Unordered/reversed LINE segments form one ring");
var classifiedRectangle=ProfileAnalyzer.Analyze(Document(shuffledLines));
Assert(classifiedRectangle.Profiles.Count==1, "LINE ring integrated into DXF analysis");
Assert(Math.Abs(classifiedRectangle.Profiles[0].NetAreaMm2 - 5000)<1e-8,"LINE ring area correct");
var assembledNear=LineContourAssembler.Assemble([
    Segment(a0,new Point2(100.002,0)),Segment(a1,a2),Segment(a2,a3),Segment(a3,a0)], 0.005);
Assert(assembledNear.Complete && assembledNear.ClosedContours.Count==1,"Small gaps within tolerance are joined");
var tooWide=LineContourAssembler.Assemble([
    Segment(a0,new Point2(100.2,0)),Segment(a1,a2),Segment(a2,a3),Segment(a3,a0)], 0.01);
Assert(!tooWide.Complete && tooWide.Warnings.Count>0,"Large gap rejected");
var branch=LineContourAssembler.Assemble([
    ..shuffledLines, Segment(a0,new Point2(-20,-20))]);
Assert(!branch.Complete,"Branching endpoint rejected");
var duplicate=LineContourAssembler.Assemble([
    ..shuffledLines,Segment(a0,a1)]);
Assert(!duplicate.Complete,"Duplicate edge rejected");
var lineCross=ProfileAnalyzer.Analyze(Document(
    Segment(new Point2(0,0),new Point2(50,50)),
    Segment(new Point2(50,50),new Point2(0,50)),
    Segment(new Point2(0,50),new Point2(50,0)),
    Segment(new Point2(50,0),new Point2(0,0))));
Assert(lineCross.Profiles.Count==0 && lineCross.Warnings.Count>0,"Self-crossing assembled contour rejected");
var nonFinite=LineContourAssembler.Assemble([Segment(new Point2(double.NaN,0),a1)]);
Assert(!nonFinite.Complete,"Nonfinite input rejected");
var mixedLineArc=ProfileAnalyzer.Analyze(Document(shuffledLines[0],new DxfArc("CUT",new Point2(0,0),20,0,90)));
Assert(mixedLineArc.Profiles.Count==0 && mixedLineArc.Warnings.Count>0,"No premature line+arc CAD classification");


var halfArc=new DxfArc("CUT",new Point2(0,0),10,0,180);
var chord=new DxfLine("CUT",new Point2(-10,0),new Point2(10,0));
var mixed=MixedContourAssembler.Assemble([halfArc,chord]);
Assert(mixed.Complete && mixed.ClosedPaths.Count==1,"ARC + chord produce a closed topological path");
Assert(mixed.ClosedPaths[0].Segments.Count==2,"Arc remains analytic, not flattened to LINE");
var mixedSample=VirtualToolpath.Sample(mixed.ClosedPaths[0]);
Assert(mixedSample.IsValidForPreview,"Mixed arc-line path can be sampled for display");
Assert(Math.Abs(mixedSample.LengthMm-(10*Math.PI+20))<1e-6,
       "Mixed contour length uses exact arc length");
var reversed=MixedContourAssembler.Assemble([new DxfLine("CUT",new Point2(10,0),new Point2(-10,0)),halfArc]);
Assert(reversed.Complete && reversed.ClosedPaths.Count==1,"Reverse traversal joins ARC");
Assert(reversed.ClosedPaths[0].Segments.OfType<PreviewArc>().Any(a=>a.SweepDegrees<0),
       "Reverse traversal retains exact CW arc geometry");
Assert(VirtualToolpath.Sample(reversed.ClosedPaths[0]).IsValidForPreview,
       "Signed preview sweep remains valid");
// Composite DXF contour: an exact bulge arc joined with an independently stored LINE.
var bulgedHalf = new DxfBulgedPolyline("CUT",
    [new Point2(-10, 0), new Point2(10, 0)], [1, 0], false);
var combinedBulgeAndLine = MixedContourAssembler.Assemble([
    bulgedHalf, new DxfLine("CUT", new Point2(10, 0), new Point2(-10, 0))]);
Assert(combinedBulgeAndLine.Complete && combinedBulgeAndLine.ClosedPaths.Count == 1,
       "Bulge arc and independent DXF LINE assemble into one closed preview contour");
Assert(combinedBulgeAndLine.ClosedPaths[0].Segments.OfType<PreviewArc>().Count() == 1,
       "Bulge arc remains analytic after composite contour assembly");
var bulgedLength = VirtualToolpath.Sample(combinedBulgeAndLine.ClosedPaths[0]);
Assert(bulgedLength.IsValidForPreview &&
       Math.Abs(bulgedLength.LengthMm - (10*Math.PI+20)) < 1e-6,
       "Bulge and LINE closed preview uses true semicircular arc length");
var bulgedClosed = MixedContourAssembler.Assemble([
    new DxfBulgedPolyline("CUT",
        [new Point2(-10, 0), new Point2(10, 0)], [1, 0], true)]);
Assert(bulgedClosed.Complete && bulgedClosed.ClosedPaths.Count == 1,
       "Closed bulged polyline is assembled without independent chord");
var bulgeOpen = MixedContourAssembler.Assemble([bulgedHalf]);
Assert(!bulgeOpen.Complete && bulgeOpen.Warnings.Count > 0,
       "Open curved polyline cannot be mistaken for a closed cutting boundary");
var bulgeBranch = MixedContourAssembler.Assemble([
    bulgedHalf,
    new DxfLine("CUT", new Point2(10, 0), new Point2(-10, 0)),
    new DxfLine("CUT", new Point2(10, 0), new Point2(20, 0))]);
Assert(!bulgeBranch.Complete && bulgeBranch.ClosedPaths.Count == 0,
       "Branching composite polyline/LINE graph fails closed");
var bulgeCrossing = MixedContourAssembler.Assemble([
    bulgedHalf,
    new DxfLine("CUT", new Point2(10, 0), new Point2(0, -12)),
    new DxfLine("CUT", new Point2(0, -12), new Point2(-10, 0))]);
Assert(!bulgeCrossing.Complete && bulgeCrossing.Warnings.Count > 0,
       "Non-endpoint intersection in composite bulge/LINE ring is rejected");
var curvedPreflight = GeometryPreflight.Inspect(
    Document(bulgedHalf, new DxfLine("CUT",new Point2(10,0),new Point2(-10,0))),
    new Sheet(100,100));
Assert(curvedPreflight.Status == GeometryPreflightStatus.Rejected &&
       !curvedPreflight.CanGenerateMachineCommands,
       "Successful preview topology never authorizes cutting a bulge contour");

// Analytic Green-theorem metrics, without sampling curves into polygon chords.
var halfMetrics = PreviewContourMetrics.Measure(mixed.ClosedPaths[0]);
Assert(halfMetrics.IsValid &&
       Math.Abs(halfMetrics.AreaMm2 - Math.PI*50) < 1e-7 &&
       Math.Abs(halfMetrics.PerimeterMm - (Math.PI*10 + 20)) < 1e-7,
       "LINE/ARC half-disc has exact analytic area and circumference");
Assert(!halfMetrics.CanStartMachine,
       "Metrics are never a machine execution permit");
var bulgeMetrics = PreviewContourMetrics.Measure(combinedBulgeAndLine.ClosedPaths[0]);
Assert(bulgeMetrics.IsValid && Math.Abs(bulgeMetrics.AreaMm2 - Math.PI*50)<1e-7,
       "Composite bulge/LINE area equals the analytic half-circle");
var reversedSemidisc = new PreviewPath([
    new PreviewLine(new PreviewPoint(10,0), new PreviewPoint(-10,0)),
    new PreviewArc(new PreviewPoint(0,0),10,180,-180)
], true);
var reversedMetrics=PreviewContourMetrics.Measure(reversedSemidisc);
Assert(reversedMetrics.IsValid && Math.Abs(reversedMetrics.SignedAreaMm2 + halfMetrics.SignedAreaMm2)<1e-7,
       "Opposite traversal reverses signed area but preserves physical area");
var rectangleMetrics=PreviewContourMetrics.Measure(new PreviewPath([
    new PreviewLine(new PreviewPoint(0,0),new PreviewPoint(200,0)),
    new PreviewLine(new PreviewPoint(200,0),new PreviewPoint(200,100)),
    new PreviewLine(new PreviewPoint(200,100),new PreviewPoint(0,100)),
    new PreviewLine(new PreviewPoint(0,100),new PreviewPoint(0,0))
],true));
Assert(rectangleMetrics.IsValid && rectangleMetrics.SignedAreaMm2==20000 &&
       rectangleMetrics.PerimeterMm==600 && rectangleMetrics.Winding=="CCW",
       "Exact rectangular area, perimeter and orientation");
var displacedRing = new PreviewPath([
    new PreviewLine(new PreviewPoint(1e9,1e9),new PreviewPoint(1e9+200,1e9)),
    new PreviewLine(new PreviewPoint(1e9+200,1e9),new PreviewPoint(1e9+200,1e9+100)),
    new PreviewLine(new PreviewPoint(1e9+200,1e9+100),new PreviewPoint(1e9,1e9+100)),
    new PreviewLine(new PreviewPoint(1e9,1e9+100),new PreviewPoint(1e9,1e9))
],true);
Assert(PreviewContourMetrics.Measure(displacedRing).AreaMm2==20000,
       "Local origin prevents area cancellation for large drawing coordinates");
var openMetrics=PreviewContourMetrics.Measure(new PreviewPath([
    new PreviewLine(new PreviewPoint(0,0),new PreviewPoint(10,0))
],false));
Assert(!openMetrics.IsValid && openMetrics.Issues.Count>0,
       "Unclosed path cannot yield verified area");
var crossingMetrics=PreviewContourMetrics.Measure(new PreviewPath([
    new PreviewLine(new PreviewPoint(0,0),new PreviewPoint(10,10)),
    new PreviewLine(new PreviewPoint(10,10),new PreviewPoint(0,10)),
    new PreviewLine(new PreviewPoint(0,10),new PreviewPoint(10,0)),
    new PreviewLine(new PreviewPoint(10,0),new PreviewPoint(0,0))
],true));
Assert(!crossingMetrics.IsValid, "Self-intersection rejects analytic area result");

// B2 regression: independently simple rings can still cross or touch each other.
static DxfLine[] BoxLines(double x1, double y1, double x2, double y2) =>
[
    new DxfLine("CUT", new Point2(x1,y1), new Point2(x2,y1)),
    new DxfLine("CUT", new Point2(x2,y1), new Point2(x2,y2)),
    new DxfLine("CUT", new Point2(x2,y2), new Point2(x1,y2)),
    new DxfLine("CUT", new Point2(x1,y2), new Point2(x1,y1))
];
var independentBoxes = MixedContourAssembler.Assemble(
    [..BoxLines(0,0,10,10), ..BoxLines(20,0,30,10)]);
Assert(independentBoxes.Complete && independentBoxes.ClosedPaths.Count == 2,
       "Separate disjoint contour rings remain previewable");
var nestedBoxes = MixedContourAssembler.Assemble(
    [..BoxLines(0,0,30,30), ..BoxLines(10,10,20,20)]);
Assert(nestedBoxes.Complete && nestedBoxes.ClosedPaths.Count == 2,
       "Strictly nested non-touching rings remain previewable, without hole certification");
var crossingBoxes = MixedContourAssembler.Assemble(
    [..BoxLines(0,0,20,20), ..BoxLines(10,-5,30,10)]);
Assert(!crossingBoxes.Complete && crossingBoxes.ClosedPaths.Count == 0,
       "Crossing distinct polygon rings fail the entire assembly");
var tangentBoxes = MixedContourAssembler.Assemble(
    [..BoxLines(0,0,10,10), ..BoxLines(10,10,20,20)]);
Assert(!tangentBoxes.Complete,
       "Distinct polygons touching at a shared point are rejected");
var curvedOuterAndCrossingLine = MixedContourAssembler.Assemble([
    new DxfArc("CUT",new Point2(0,0),10,0,180),
    new DxfLine("CUT",new Point2(-10,0),new Point2(10,0)),
    ..BoxLines(-2,5,2,12)
]);
Assert(!curvedOuterAndCrossingLine.Complete,
       "Analytic ARC against a separate LINE contour detects crossing");
Assert(!nestedBoxes.ClosedPaths.Any(p => PreviewContourMetrics.Measure(p).CanStartMachine),
       "Pair inspection never grants equipment control");

// Analytic ring nesting: signed Green areas and even/odd material islands.
var topologyNested = PreviewContourTopology.Analyze(nestedBoxes.ClosedPaths);
Assert(topologyNested.IsValid && topologyNested.Rings.Count==2 &&
       Math.Abs(topologyNested.NetAreaMm2-800)<1e-7 &&
       topologyNested.Rings.Count(r=>!r.IsMaterialBoundary)==1,
       "Nested rectangles produce 900-100 = 800 square mm of material");
var topologyDisjoint = PreviewContourTopology.Analyze(independentBoxes.ClosedPaths);
Assert(topologyDisjoint.IsValid && Math.Abs(topologyDisjoint.NetAreaMm2-200)<1e-7,
       "Independent nonoverlapping rings add their exact areas");
var threeLevel = MixedContourAssembler.Assemble([
    ..BoxLines(0,0,40,40), ..BoxLines(5,5,35,35), ..BoxLines(10,10,20,20)
]);
Assert(threeLevel.Complete, "Three-level closed contour assembly is valid for preview");
var islandMetrics=PreviewContourTopology.Analyze(threeLevel.ClosedPaths);
Assert(islandMetrics.IsValid &&
       Math.Abs(islandMetrics.NetAreaMm2-(1600-900+100))<1e-7 &&
       islandMetrics.Rings.Count(r=>r.IsMaterialBoundary)==2,
       "Three-level inclusion alternates material, void and island");
var topologyCrossing = PreviewContourTopology.Analyze([
    new PreviewPath([new PreviewLine(new(0,0),new(20,0)),
                     new PreviewLine(new(20,0),new(20,20)),
                     new PreviewLine(new(20,20),new(0,20)),
                     new PreviewLine(new(0,20),new(0,0))],true),
    new PreviewPath([new PreviewLine(new(10,-5),new(30,-5)),
                     new PreviewLine(new(30,-5),new(30,10)),
                     new PreviewLine(new(30,10),new(10,10)),
                     new PreviewLine(new(10,10),new(10,-5))],true)
]);
Assert(!topologyCrossing.IsValid && !topologyCrossing.CanStartMachine,
       "Intersecting rings never yield net material area or equipment permission");
Assert(!topologyNested.CanStartMachine, "Analytic net area cannot enable machine commands");

// Exact CIRCLE support in read-only mixed topology, including nested round voids.
var circleRings=MixedContourAssembler.Assemble([
    new DxfCircle("CUT",new Point2(50,50),20),
    new DxfCircle("CUT",new Point2(50,50),5)]);
Assert(circleRings.Complete && circleRings.ClosedPaths.Count==2,
       "Analytic CIRCLE entities form complete contour rings");
var circleNesting=PreviewContourTopology.Analyze(circleRings.ClosedPaths);
Assert(circleNesting.IsValid &&
       Math.Abs(circleNesting.NetAreaMm2-Math.PI*(400-25))<1e-6,
       "Circular outer contour and circular hole retain exact pi*r² areas");
var touchingCircles=MixedContourAssembler.Assemble([
    new DxfCircle("CUT",new Point2(0,0),10),
    new DxfCircle("CUT",new Point2(20,0),10)]);
Assert(!touchingCircles.Complete,
       "Externally tangent circular boundaries rejected before CAD/CAM");

var openMixed=MixedContourAssembler.Assemble([halfArc]);
Assert(!openMixed.Complete,"ARC without chord is an open chain");
var branchedMixed=MixedContourAssembler.Assemble([halfArc,chord,
    new DxfLine("CUT",new Point2(10,0),new Point2(20,0))]);
Assert(!branchedMixed.Complete,"Branching LINE/ARC endpoint is rejected");
var brokenMixed=MixedContourAssembler.Assemble([halfArc,
    new DxfLine("CUT",new Point2(-10,0),new Point2(10.2,0))]);
Assert(!brokenMixed.Complete,"Too-large LINE/ARC gap rejected");
var withinTolerance=MixedContourAssembler.Assemble([halfArc,
    new DxfLine("CUT",new Point2(-10,0),new Point2(10.002,0))]);
Assert(withinTolerance.Complete,"LINE/ARC endpoint inside tolerance accepted for inspection");
var invalidSweep=MixedContourAssembler.Assemble([
    new DxfArc("CUT",new Point2(0,0),10,0,720),chord]);
Assert(!invalidSweep.Complete,"Multi-turn ARC rejected as ambiguous");


var safeSemicircle = new PreviewPath([
    new PreviewArc(new PreviewPoint(0,0),10,0,180),
    new PreviewLine(new PreviewPoint(-10,0),new PreviewPoint(10,0))],true);
Assert(PreviewContourValidator.Validate(safeSemicircle).Count==0,
       "Semicircle and chord meet only at their shared endpoints");
var crossingArcLine = new PreviewPath([
    new PreviewArc(new PreviewPoint(0,0),10,0,180),
    new PreviewLine(new PreviewPoint(-10,0),new PreviewPoint(10,0)),
    new PreviewLine(new PreviewPoint(10,0),new PreviewPoint(0,20)),
    new PreviewLine(new PreviewPoint(0,20),new PreviewPoint(10,0))],true);
Assert(PreviewContourValidator.Validate(crossingArcLine).Count>0,
       "Branching and repeated/overlapping line paths cannot be accepted");
var arcAndChordCross = new PreviewPath([
    new PreviewArc(new PreviewPoint(0,0),10,0,180),
    new PreviewLine(new PreviewPoint(-10,0),new PreviewPoint(10,0)),
    new PreviewLine(new PreviewPoint(10,0),new PreviewPoint(0,20)),
    new PreviewLine(new PreviewPoint(0,20),new PreviewPoint(10,0))],true);
Assert(PreviewContourValidator.Validate(arcAndChordCross).Count>0,
       "Nonadjacent LINE/ARC touching cannot be approved");
var twoArcCross = new PreviewPath([
    new PreviewArc(new PreviewPoint(0,0),10,0,180),
    new PreviewLine(new PreviewPoint(-10,0),new PreviewPoint(10,0))],true);
Assert(PreviewContourValidator.Validate(twoArcCross).Count==0,"Simple arc geometry still accepted");
var intersectingAnalytic=MixedContourAssembler.Assemble([
    new DxfArc("CUT",new Point2(0,0),10,0,180),
    new DxfLine("CUT",new Point2(-10,0),new Point2(10,0)),
    new DxfLine("CUT",new Point2(10,0),new Point2(0,20)),
    new DxfLine("CUT",new Point2(0,20),new Point2(10,0))
]);
Assert(!intersectingAnalytic.Complete,"Invalid mixed contour graph is rejected");


var genuineLineArcCrossing = MixedContourAssembler.Assemble([
    new DxfArc("CUT",new Point2(0,0),10,0,180),
    new DxfLine("CUT",new Point2(-10,0),new Point2(0,12)),
    new DxfLine("CUT",new Point2(0,12),new Point2(10,0))
]);
Assert(!genuineLineArcCrossing.Complete &&
       genuineLineArcCrossing.Warnings.Any(w=>w.Contains("intersection",StringComparison.OrdinalIgnoreCase)),
       "Non-endpoint LINE/ARC intersections rejected analytically");
var touchedArc = MixedContourAssembler.Assemble([
    new DxfArc("CUT",new Point2(0,0),10,0,180),
    new DxfLine("CUT",new Point2(-10,0),new Point2(0,10)),
    new DxfLine("CUT",new Point2(0,10),new Point2(10,0))
]);
Assert(!touchedArc.Complete,"Touching the interior of an ARC is rejected");


var preflightGood=GeometryPreflight.Inspect(
    Document(rectangle,innerHole,circleHole),new Sheet(300,200),0);
Assert(preflightGood.Status==GeometryPreflightStatus.ReadyForCamReview,
    "Verified polygon and holes pass only geometric preflight");
Assert(preflightGood.Profiles.Count==1 && preflightGood.BlockingIssues.Count==0,
    "Safe subset produces one profile");
Assert(!preflightGood.CanGenerateMachineCommands,
    "Geometric preflight NEVER grants machine execution");
var preflightUnknown=GeometryPreflight.Inspect(
    new DxfImportResult([rectangle],[],null),new Sheet(300,200));
Assert(preflightUnknown.Status==GeometryPreflightStatus.Rejected,
    "Unconfirmed units fail closed");
var preflightArc=GeometryPreflight.Inspect(
    Document(rectangle,new DxfArc("CUT",new Point2(30,30),10,0,90)),new Sheet(300,200));
Assert(preflightArc.Status==GeometryPreflightStatus.Rejected,
    "Unsupported arc cannot be silently omitted");
var preflightOpen=GeometryPreflight.Inspect(
    Document(rectangle,new DxfPolyline("CUT",[new Point2(0,0),new Point2(1,1)],false)),
    new Sheet(300,200));
Assert(preflightOpen.Status==GeometryPreflightStatus.Rejected,
    "Open DXF contour blocks preflight");
var preflightTooLarge=GeometryPreflight.Inspect(
    Document(rectangle),new Sheet(150,150));
Assert(preflightTooLarge.Status==GeometryPreflightStatus.Rejected,
    "Outside sheet boundary rejected");
var preflightMargin=GeometryPreflight.Inspect(
    Document(rectangle),new Sheet(300,200),10);
Assert(preflightMargin.Status==GeometryPreflightStatus.Rejected,
    "Sheet edge touching margin rejected");
var preflightIntersect=GeometryPreflight.Inspect(
    Document(rectangle,
        Closed("CUT",new Point2(100,50),new Point2(250,50),
            new Point2(250,150),new Point2(100,150))),
    new Sheet(400,300));
Assert(preflightIntersect.Status==GeometryPreflightStatus.Rejected,
    "Intersecting profiles block preflight");
var preflightWarnings=GeometryPreflight.Inspect(
    new DxfImportResult([rectangle],["Unsupported entity SPLINE skipped"],4),new Sheet(300,200));
Assert(preflightWarnings.Status==GeometryPreflightStatus.Rejected,
    "Importer warnings block preflight");
var preflightNoParts=GeometryPreflight.Inspect(
    Document(),new Sheet(300,200));
Assert(preflightNoParts.Status==GeometryPreflightStatus.Rejected,
    "Empty drawing cannot pass");
var preflightNaN=GeometryPreflight.Inspect(
    Document(Closed("CUT",new Point2(double.NaN,0),new Point2(100,0),new Point2(100,100))),
    new Sheet(300,200));
Assert(preflightNaN.Status==GeometryPreflightStatus.Rejected,
    "Non-finite coordinate cannot pass");


var plannedGeometry=GeometryPreflight.Inspect(
    Document(rectangle,innerHole,circleHole),new Sheet(300,200));
var plan=PreviewCutPlanner.Plan(plannedGeometry);
Assert(plan.BlockingIssues.Count==0 && plan.Operations.Count==3,"Cut preview has polygon hole, round hole and outer contour");
Assert(plan.Operations[0].Kind==PreviewCutKind.InnerPolygon &&
       plan.Operations[1].Kind==PreviewCutKind.CircularHole &&
       plan.Operations[2].Kind==PreviewCutKind.OuterBoundary,
       "All holes precede outside cut");
Assert(plan.Operations[1].SuggestedPiercePoint==new Point2(100,50),
       "Round-hole preview suggests waste-side center");
Assert(plan.Operations[1].SuggestedBoundaryEntry==new Point2(110,50),
       "Round-hole candidate boundary entry");
Assert(plan.Operations.All(o=>o.RequiresCamValidation)&&!plan.CanStartMachine,
       "Preview never authorizes physical cutting");
var blockedPlan=PreviewCutPlanner.Plan(preflightArc);
Assert(blockedPlan.Operations.Count==0 && blockedPlan.BlockingIssues.Count>0,
       "Unverified geometry never generates partial plan");
var tooSmallHole=PreviewCutPlanner.Plan(plannedGeometry,minimumHoleRadiusMm:12);
Assert(tooSmallHole.Operations.Count==0 && tooSmallHole.BlockingIssues.Count>0,
       "Too-small hole blocks complete preview plan");

var workspace = WorkspaceGeometry.Default;
Assert(workspace.Validate().Count == 0, "Default table and material sheet are compatible");
var customWorkspace = new WorkspaceGeometry(new Sheet(2500, 1300), new Sheet(2400, 1200));
Assert(customWorkspace.Validate().Count == 0, "Independent valid sheet and table dimensions accepted");
Assert(new WorkspaceGeometry(new Sheet(1000, 1000), new Sheet(1500, 900)).Validate().Count > 0,
       "Material sheet exceeding table rejected");
Assert(new WorkspaceGeometry(new Sheet(double.NaN, 1000), new Sheet(800, 800)).Validate().Count > 0,
       "Non-finite table dimensions rejected");

var vm = VirtualMachine.Create(customWorkspace);
Assert(vm.Checkpoints.Count == 4, "Four material sheet reference points exist");
Assert(vm.Checkpoints[2].Position.X == 2400 && vm.Checkpoints[2].Position.Y == 1200,
       "Sheet reference points match sheet rather than machine table");
vm = vm.MoveVirtual(new MachinePoint(50, 80, 10));
Assert(vm.MachineCoordinates.X == 50, "Virtual machine absolute X updates");
vm = vm.SetWorkZeroAtHead();
Assert(vm.WorkCoordinates == new MachinePoint(0, 0, 0), "Working zero established at current virtual head");
vm = vm.MoveVirtual(new MachinePoint(55, 70, 12));
Assert(vm.WorkCoordinates == new MachinePoint(5, -10, 2), "Relative work XYZ tracks offset");
var invalidHeadRejected = false;
try { vm.MoveVirtual(new MachinePoint(2600, 70, 12)); }
catch (ArgumentOutOfRangeException) { invalidHeadRejected = true; }
Assert(invalidHeadRejected, "Virtual head outside machine table rejected");

var semicircleBulge = new DxfBulgedPolyline("CUT",
    [new Point2(-10, 0), new Point2(10, 0)], [1, 0], false);
var semicircleSegments = DxfBulgePreview.Build(semicircleBulge);
Assert(semicircleSegments.Segments[0] is PreviewArc { RadiusMm: > 9.999 and < 10.001 },
       "Bulge=1 is an exact semicircular arc with radius 10");
var semicirclePreview = VirtualToolpath.Sample(semicircleSegments);
Assert(semicirclePreview.IsValidForPreview && Math.Abs(semicirclePreview.LengthMm - 10 * Math.PI) < 1e-7,
       "Bulged semicircle preview matches analytic arc length");
var clockwiseBulge = new DxfBulgedPolyline("CUT",
    [new Point2(-10, 0), new Point2(10, 0)], [-1, 0], false);
Assert(DxfBulgePreview.Build(clockwiseBulge).Segments[0] is PreviewArc { SweepDegrees: < 0 },
       "Negative bulge preserves clockwise arc direction");
var invalidBulgeRejected = false;
try { DxfBulgePreview.Build(new DxfBulgedPolyline("CUT",
    [new Point2(0, 0), new Point2(0, 0)], [1, 0], false)); }
catch (ArgumentException) { invalidBulgeRejected = true; }
Assert(invalidBulgeRejected, "Zero-length bulge chord rejected");


// Decoding different ASCII DXF revisions and explicitly declared legacy code pages.
var revisionFixture = string.Join("\n", new[]
{
    "0","SECTION","2","HEADER",
    "9","$ACADVER","1","AC1015",
    "9","$DWGCODEPAGE","3","ANSI_1251",
    "9","$INSUNITS","70","4",
    "0","ENDSEC","0","SECTION","2","ENTITIES",
    "0","LINE","8","Деталь","10","0","20","0","11","100","21","50",
    "0","ENDSEC","0","EOF"
});
DxfFileReader.Read(System.Text.Encoding.UTF8.GetBytes(dxf));
var windows1251Bytes = System.Text.Encoding.GetEncoding(1251).GetBytes(revisionFixture);
var decoded1251 = DxfFileReader.Read(windows1251Bytes);
Assert(decoded1251.AcadVersion == "AC1015" && decoded1251.EncodingName == "ANSI_1251",
       "R2000 revision and declared Windows-1251 code page identified");
Assert(decoded1251.Document.Shapes.Single() is DxfLine { Layer: "Деталь" },
       "Cyrillic layer survives Windows-1251 decoding");
Assert(decoded1251.Document.Warnings.Count == 0, "Declared and known revision/code page has no decoding warnings");

var utf8Version = revisionFixture.Replace("ANSI_1251", "UTF-8").Replace("AC1015", "AC1032");
var decodedUtf8 = DxfFileReader.Read(System.Text.Encoding.UTF8.GetBytes(utf8Version));
Assert(decodedUtf8.AcadVersion == "AC1032" && decodedUtf8.Document.Warnings.Count == 0,
       "R2018-style revision with UTF-8 data decoded without lossy replacement");

var bomBytes = new byte[] { 0xef, 0xbb, 0xbf }
    .Concat(System.Text.Encoding.UTF8.GetBytes(utf8Version)).ToArray();
Assert(DxfFileReader.Read(bomBytes).Document.Shapes.Count == 1, "UTF-8 BOM accepted");

var utf16Bytes = new byte[] { 0xff, 0xfe }
    .Concat(System.Text.Encoding.Unicode.GetBytes(utf8Version.Replace("UTF-8", "UTF-16"))).ToArray();
Assert(DxfFileReader.Read(utf16Bytes).Document.Shapes.Count == 1, "UTF-16 LE BOM decoded");

var unknownRevision = DxfFileReader.Read(System.Text.Encoding.UTF8.GetBytes(utf8Version.Replace("AC1032","AC9999")));
Assert(unknownRevision.Document.Warnings.Any(w => w.Contains("Unknown $ACADVER")),
       "Unknown revision flagged for geometric review");
Assert(GeometryPreflight.Inspect(unknownRevision.Document, new Sheet(500,500)).Status == GeometryPreflightStatus.Rejected,
       "Unverified DXF revision never passes preflight");

static bool RejectDxfBytes(byte[] file)
{
    try { DxfFileReader.Read(file); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectDxfBytes(System.Text.Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n")),
       "Binary DXF format rejected");
Assert(RejectDxfBytes(System.Text.Encoding.ASCII.GetBytes(
    revisionFixture.Replace("ANSI_1251", "ANSI_9999"))),
       "Unknown legacy code page rejected");
Assert(RejectDxfBytes(new byte[]{ 0xff, 0xfe, 0x00 }),
       "Malformed UTF-16 input rejected");
Assert(RejectDxfBytes(new byte[]{ 0xc3, 0x28 }),
       "Undeclared invalid UTF-8 rejected without replacement characters");

// DXF must not silently flatten genuinely 3D or wide entities into cutting contours.
var zLineDxf=revisionFixture.Replace("21\n50","21\n50\n31\n5");
var zLineRead=DxfReader.Parse(zLineDxf);
Assert(zLineRead.Shapes.Count==0 &&
       zLineRead.Warnings.Any(w=>w.Contains("elevation/extrusion")),
       "Nonzero LINE Z is rejected rather than silently projected to 2D");
var widePolylineDxf=string.Join("\n",new[]{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4",
    "0","ENDSEC","0","SECTION","2","ENTITIES",
    "0","LWPOLYLINE","8","CUT","90","3","70","1",
    "10","0","20","0","40","2",
    "10","10","20","0","10","10","20","10",
    "0","ENDSEC","0","EOF"
});
var wideRead=DxfReader.Parse(widePolylineDxf);
Assert(wideRead.Shapes.Count==0 && wideRead.Warnings.Count>0,
       "Nonzero LWPOLYLINE width rejected until explicit CAD width support exists");

// R12 uses POLYLINE/VERTEX/SEQEND instead of LWPOLYLINE.
var r12Polyline=string.Join("\n",new[]{
    "0","SECTION","2","HEADER","9","$INSUNITS","70","4",
    "0","ENDSEC","0","SECTION","2","ENTITIES",
    "0","POLYLINE","8","CUT","70","1",
    "0","VERTEX","10","0","20","0",
    "0","VERTEX","10","100","20","0",
    "0","VERTEX","10","100","20","50",
    "0","VERTEX","10","0","20","50",
    "0","SEQEND","0","ENDSEC","0","EOF"
});
var r12Shape=DxfReader.Parse(r12Polyline);
Assert(r12Shape.Shapes.Single() is DxfPolyline { Closed:true } &&
       r12Shape.Warnings.Count==0,
       "Legacy R12 2D POLYLINE/VERTEX imported without losing vertices");
var r12Bulged=DxfReader.Parse(r12Polyline.Replace(
    "10\n0\n20\n0", "10\n0\n20\n0\n42\n0.25"));
Assert(r12Bulged.Shapes.Single() is DxfBulgedPolyline &&
       r12Bulged.Warnings.Any(w=>w.Contains("preview-only")),
       "Legacy R12 bulge preserved analytically but excluded from CAM");
var r12MissingEnd=DxfReader.Parse(r12Polyline.Replace(
    "0\nSEQEND\n", ""));
Assert(r12MissingEnd.Warnings.Any(w=>w.Contains("Unterminated")),
       "Legacy R12 POLYLINE without SEQEND fails closed");
var r12ThreeD=DxfReader.Parse(r12Polyline.Replace(
    "0\nVERTEX\n10\n100\n20\n0",
    "0\nVERTEX\n10\n100\n20\n0\n30\n5"));
Assert(r12ThreeD.Warnings.Count>0,
       "3D legacy VERTEX cannot be projected as a valid 2D contour");

// B1: legacy R12 scalar bulges and polyline width/extrusion fields must
// never be silently discarded or interpreted as a cutting centerline.
var r12DuplicateBulge=DxfReader.Parse(r12Polyline.Replace(
    "10\n0\n20\n0", "10\n0\n20\n0\n42\n0.1\n42\n0.5"));
Assert(r12DuplicateBulge.Shapes.Count==0 &&
       r12DuplicateBulge.Warnings.Any(w=>w.Contains("Duplicate legacy VERTEX bulge")),
       "R12 vertex with two competing bulges must reject whole legacy polyline");
var r12HeaderWidth=DxfReader.Parse(r12Polyline.Replace(
    "0\nPOLYLINE\n8\nCUT\n70\n1",
    "0\nPOLYLINE\n8\nCUT\n70\n1\n40\n2"));
Assert(r12HeaderWidth.Shapes.Count==0 &&
       r12HeaderWidth.Warnings.Any(w=>w.Contains("width")),
       "R12 nonzero polyline header width must not silently become thin contour");
var r12VertexWidth=DxfReader.Parse(r12Polyline.Replace(
    "0\nVERTEX\n10\n0\n20\n0",
    "0\nVERTEX\n10\n0\n20\n0\n40\n2"));
Assert(r12VertexWidth.Shapes.Count==0 &&
       r12VertexWidth.Warnings.Any(w=>w.Contains("width")),
       "R12 per-vertex width must reject unsupported wide geometry");
var r12VertexExtrusion=DxfReader.Parse(r12Polyline.Replace(
    "0\nVERTEX\n10\n0\n20\n0",
    "0\nVERTEX\n10\n0\n20\n0\n210\n1"));
Assert(r12VertexExtrusion.Shapes.Count==0 &&
       r12VertexExtrusion.Warnings.Any(w=>w.Contains("3D")),
       "R12 vertex with unsupported extrusion cannot project into verified XY");
foreach(var badR12 in new[]{
    r12DuplicateBulge,r12HeaderWidth,r12VertexWidth,r12VertexExtrusion
})
{
    var gate=GeometryPreflight.Inspect(badR12,new Sheet(200,200));
    Assert(gate.Status==GeometryPreflightStatus.Rejected &&
           !gate.CanGenerateMachineCommands,
           "Unsupported legacy R12 geometry cannot pass preview preflight");
    bool exporterBlocked=false;
    try { PreviewDxfExporter.ValidateDocument(badR12); }
    catch(FormatException) { exporterBlocked=true; }
    Assert(exporterBlocked,
           "Unsupported legacy R12 geometry cannot be verified for DXF export");
}

// Deterministic numerical regression across translations and nesting scales.
// These are synthetic oracle checks, not a substitute for external industrial DXF.
for (int caseNo=1;caseNo<=128;caseNo++)
{
    double originX=(caseNo%2==0?1:-1)*(caseNo*1000.0);
    double originY=caseNo*12.5;
    double side=20+caseNo*0.25;
    double margin=side*0.2;
    var translated=MixedContourAssembler.Assemble([
        ..BoxLines(originX,originY,originX+side,originY+side),
        ..BoxLines(originX+margin,originY+margin,
                   originX+side-margin,originY+side-margin)
    ]);
    Assert(translated.Complete && translated.ClosedPaths.Count==2,
           $"Synthetic ring assembly case {caseNo}");
    var measured=PreviewContourTopology.Analyze(translated.ClosedPaths);
    double expected=side*side-(side-2*margin)*(side-2*margin);
    Assert(measured.IsValid &&
           Math.Abs(measured.NetAreaMm2-expected)<=Math.Max(1e-7,expected*1e-9),
           $"Synthetic translated net area case {caseNo}");
}
// Licensed external DXF files from the CADCraft Corpus (not production blueprints).
foreach(var name in new[]{"cadcraft-sample-bracket.dxf","cadcraft-sample-floorplan.dxf"})
{
    var fixturePath=Path.Combine("tests","fixtures",name);
    var bytes=File.ReadAllBytes(fixturePath);
    var importedRealFile=DxfFileReader.Read(bytes);
    Assert(importedRealFile.AcadVersion=="AC1032" &&
           importedRealFile.Document.InsUnits==1,
           $"External R2018 DXF fixture {name} preserves inch units");
    Assert(importedRealFile.Document.Shapes.Count>0 &&
           importedRealFile.Document.Warnings.Count>0,
           $"External DXF {name} exposes supported entities and warnings");
    var fixtureGate=GeometryPreflight.Inspect(importedRealFile.Document,
        new Sheet(1000,1000));
    Assert(fixtureGate.Status==GeometryPreflightStatus.Rejected &&
           !fixtureGate.CanGenerateMachineCommands,
           $"External DXF {name} refuses unconfirmed millimetre CAM geometry");
}
// Expanded external-fixture regression: conversion must NEVER launder an
// unsupported-entity warning into a manufacturing-ready DXF. The two source
// files are independently authored CADCraft samples, not production drawings.
foreach(var filename in new[]{"cadcraft-sample-bracket.dxf",
                              "cadcraft-sample-floorplan.dxf"})
{
    var bytes=File.ReadAllBytes(Path.Combine("tests","fixtures",filename));
    var original=DxfFileReader.Read(bytes);
    Assert(original.Document.InsUnits==1 && original.Document.Warnings.Count>1,
        "External DXF includes imperial units and separate unsupported-entity warnings");
    var scaled=DxfUnitConverter.ToMillimetres(original.Document,1);
    var expectedWarnings=original.Document.Warnings.Where(w =>
        w!="DXF $INSUNITS=1. Only millimetres (4) may be used in cutting jobs without conversion.")
        .ToArray();
    Assert(scaled.Document.InsUnits==4 &&
           scaled.Document.Warnings.SequenceEqual(expectedWarnings) &&
           scaled.Document.Warnings.Count>0,
        "Real-file unit scaling preserves all unsupported-entity warnings verbatim");
    var denied=GeometryPreflight.Inspect(scaled.Document,new Sheet(100_000,100_000));
    Assert(denied.Status==GeometryPreflightStatus.Rejected &&
           !denied.CanGenerateMachineCommands,
        "Real-file warning cannot be bypassed by explicit inch-to-mm conversion");
    bool exporterDenied=false;
    try { PreviewDxfExporter.ValidateDocument(scaled.Document); }
    catch(FormatException) { exporterDenied=true; }
    Assert(exporterDenied,"Real-file geometry-only export refuses partially imported drawings");
    // Check the reader detects corruption in the authentic byte sequence,
    // not just in miniature synthetic DXF fragments.
    var corrupted=(byte[])bytes.Clone();
    corrupted[20]=0; // binary NUL injection in original ASCII DXF data
    bool corruptionDenied=false;
    try { DxfFileReader.Read(corrupted); }
    catch(FormatException) {corruptionDenied=true;}
    Assert(corruptionDenied,"Real DXF with binary corruption is rejected");
}
// A warning that merely begins like a units warning must not be deleted.
var forgedWarning=new DxfImportResult([
    new DxfCircle("CUT",new Point2(50,50),10)],
    ["DXF $INSUNITS=1. Only millimetres (4) may be used in cutting jobs without conversion.",
     "DXF $INSUNITS=1. Unexpected additional validation problem."],1);
var forgedResult=DxfUnitConverter.ToMillimetres(forgedWarning,1);
Assert(forgedResult.Document.Warnings.Count==1 &&
       forgedResult.Document.Warnings[0].Contains("Unexpected additional validation problem"),
    "Unit conversion removes only the exact known units notice, not warning prefixes");

// Explicit scale conversion never guesses missing/contradictory DXF units.
var imperialDocument=new DxfImportResult([
    new DxfLine("CUT",new Point2(1,2),new Point2(3,4)),
    new DxfCircle("CUT",new Point2(2,2),0.5)
],["DXF $INSUNITS=1. Only millimetres (4) may be used in cutting jobs without conversion."],1);
var convertedInch=DxfUnitConverter.ToMillimetres(imperialDocument,1);
Assert(convertedInch.Document.InsUnits==4 &&
       convertedInch.Document.Warnings.Count==0 &&
       Math.Abs(((DxfLine)convertedInch.Document.Shapes[0]).Start.X-25.4)<1e-10 &&
       Math.Abs(((DxfCircle)convertedInch.Document.Shapes[1]).Radius-12.7)<1e-10 &&
       !convertedInch.CanStartMachine,
       "Explicit inch-to-mm scaling preserves analytic LINE and CIRCLE dimensions");
bool mismatchThrows=false;
try { DxfUnitConverter.ToMillimetres(imperialDocument,4); }
catch (FormatException) { mismatchThrows=true; }
Assert(mismatchThrows,"Incorrect source-unit confirmation is rejected");
bool unspecifiedThrows=false;
try { DxfUnitConverter.ToMillimetres(
    new DxfImportResult(imperialDocument.Shapes,[],0),0); }
catch (FormatException) { unspecifiedThrows=true; }
Assert(unspecifiedThrows,"Unspecified DXF source units cannot be scaled by guessing");
var retainedWarnings=DxfUnitConverter.ToMillimetres(
    imperialDocument with { Warnings=[
        "DXF $INSUNITS=1. Only millimetres (4) may be used in cutting jobs without conversion.",
        "Unsupported entity HATCH skipped"]},1);
Assert(retainedWarnings.Document.Warnings.Count==1 &&
       retainedWarnings.Document.Warnings[0].Contains("HATCH"),
       "Unit conversion never discards unsupported entity warnings");
// Configurable tolerances must affect endpoint joining without accepting crossings.
var toleranceArc=new DxfArc("CUT",new Point2(0,0),10,0,180);
var almostChord=new DxfLine("CUT",new Point2(-10,0),new Point2(10.004,0));
var strictJoin=MixedContourAssembler.Assemble([toleranceArc,almostChord],0.001);
var looseJoin=MixedContourAssembler.Assemble([toleranceArc,almostChord],0.01);
Assert(!strictJoin.Complete && looseJoin.Complete,
       "User-selected DXF tolerance controls endpoint assembly without editing the file");
var looseCrossing=MixedContourAssembler.Assemble([
    ..BoxLines(0,0,20,20), ..BoxLines(10,-5,30,10)],0.5);
Assert(!looseCrossing.Complete,
       "Maximum permitted topology tolerance cannot allow intersecting boundaries");
bool invalidToleranceRejected=false;
try { MixedContourAssembler.Assemble([toleranceArc,almostChord],double.NaN); }
catch (ArgumentOutOfRangeException) { invalidToleranceRejected=true; }
Assert(invalidToleranceRejected,"Nonfinite DXF tolerance fails closed");

// Analytic dimensional bounds: arc apexes cannot be missed by endpoint-only boxes.
var semicircleBounds=PreviewContourBounds.Measure(safeSemicircle);
Assert(semicircleBounds.IsValid &&
       Math.Abs(semicircleBounds.MinX+10)<1e-8 &&
       Math.Abs(semicircleBounds.MaxX-10)<1e-8 &&
       Math.Abs(semicircleBounds.MinY)<1e-8 &&
       Math.Abs(semicircleBounds.MaxY-10)<1e-8,
       "Analytic semicircle bounds include the arc apex");
var circularBounds=PreviewContourBounds.Measure(circleRings.ClosedPaths[0]);
Assert(circularBounds.IsValid &&
       Math.Abs(circularBounds.WidthMm-40)<1e-7 &&
       Math.Abs(circularBounds.HeightMm-40)<1e-7 &&
       !circularBounds.CanStartMachine,
       "Four exact quarter arcs recover full circular dimensions");
var majorArcBounds=PreviewContourBounds.Measure(new PreviewPath([
    new PreviewArc(new PreviewPoint(100,100),20,330,300)],false));
Assert(majorArcBounds.IsValid &&
       Math.Abs(majorArcBounds.MinX-80)<1e-7 &&
       Math.Abs(majorArcBounds.MaxX-120)<1e-7 &&
       Math.Abs(majorArcBounds.MinY-80)<1e-7,
       "Major sweep includes all appropriate cardinal extrema");
Assert(!PreviewContourBounds.Measure(new PreviewPath([
    new PreviewArc(new PreviewPoint(0,0),double.NaN,0,90)],false)).IsValid,
    "Invalid circle data cannot yield misleading CAD dimensions");

// Geometry-preserving CAD inspection transformations, without CAM permission.
var translatedSemicircle=PreviewContourTransform.Move(safeSemicircle,100,200);
var translatedMetrics=PreviewContourMetrics.Measure(translatedSemicircle);
var translatedBounds=PreviewContourBounds.Measure(translatedSemicircle);
Assert(translatedMetrics.IsValid &&
       Math.Abs(translatedMetrics.AreaMm2-halfMetrics.AreaMm2)<1e-7 &&
       Math.Abs(translatedMetrics.PerimeterMm-halfMetrics.PerimeterMm)<1e-7 &&
       Math.Abs(translatedBounds.MinX-90)<1e-7 &&
       Math.Abs(translatedBounds.MaxY-210)<1e-7,
       "Translation preserves exact analytic area and arc perimeter");
var rotatedSemicircle=PreviewContourTransform.Rotate(safeSemicircle,new PreviewPoint(0,0),90);
var rotatedMetrics=PreviewContourMetrics.Measure(rotatedSemicircle);
var rotatedBounds=PreviewContourBounds.Measure(rotatedSemicircle);
Assert(rotatedMetrics.IsValid &&
       Math.Abs(rotatedMetrics.AreaMm2-halfMetrics.AreaMm2)<1e-7 &&
       Math.Abs(rotatedBounds.MinX+10)<1e-7 &&
       Math.Abs(rotatedBounds.MaxY-10)<1e-7,
       "Quarter-turn retains analytic semicircular geometry");
Assert(rotatedSemicircle.Segments.OfType<PreviewArc>().Count()==1 &&
       !rotatedMetrics.CanStartMachine,
       "CAD transform retains ARC segment without authorizing equipment control");
bool invalidTransform=false;
try { PreviewContourTransform.Move(safeSemicircle,double.PositiveInfinity,0); }
catch (FormatException) { invalidTransform=true; }
Assert(invalidTransform,"Nonfinite CAD transform rejected");

// Text-defined 2D sketch and parametric 3D extrusion share exact dimensions.
var textPlate=TextPlateSketch.Parse("PLATE 200 100 5; HOLE 50 50 10; HOLE 150 50 10");
double plateArea=20000-2*Math.PI*100;
Assert(textPlate.Drawing2D.Shapes.Count==6 &&
       Math.Abs(textPlate.NetAreaMm2-plateArea)<1e-7 &&
       Math.Abs(textPlate.VolumeMm3-plateArea*5)<1e-7 &&
       !textPlate.CanStartMachine && !textPlate.IsManufacturingValidated,
       "2D rectangle and holes match parametric 3D extrusion volume");
var plateAssembly=MixedContourAssembler.Assemble(textPlate.Drawing2D.Shapes);
Assert(plateAssembly.Complete && plateAssembly.ClosedPaths.Count==3,
       "Text plate uses the same analytical DXF contour assembly as file import");
var plateTopology=PreviewContourTopology.Analyze(plateAssembly.ClosedPaths);
Assert(plateTopology.IsValid &&
       Math.Abs(plateTopology.NetAreaMm2-textPlate.NetAreaMm2)<1e-6,
       "Independent analytical ring topology agrees with text plate area");
Assert(GeometryPreflight.Inspect(textPlate.Drawing2D,new Sheet(200,100)).Status ==
       GeometryPreflightStatus.ReadyForCamReview,
       "Text plate's simple verified 2D subset passes geometry-only preflight");
static bool RejectPlate(string value)
{
    try { TextPlateSketch.Parse(value);return false; }
    catch(FormatException) {return true;}
}
Assert(RejectPlate("PLATE 200 100 5; HOLE 10 10 10"),
       "Hole tangent to plate edge rejected");
Assert(RejectPlate("PLATE 200 100 5; HOLE 50 50 10; HOLE 70 50 10"),
       "Two externally tangent holes rejected");
Assert(RejectPlate("PLATE NaN 100 5") &&
       RejectPlate("PLATE 200 100 5; HOLE 50 50 -2") &&
       RejectPlate("PLATE 200 100 5; UNSUPPORTED 20 30 2"),
       "Malformed, nonfinite, negative and unsupported text geometry rejected");

// Restricted editable geometry DXF round trip: transformed LINE/ARC remains analytic.
var movedRoundTripSource=PreviewContourTransform.Move(safeSemicircle,60,75);
var movedText=PreviewDxfExporter.Export([movedRoundTripSource]);
var movedDocument=DxfReader.Parse(movedText);
Assert(movedDocument.InsUnits==4 && movedDocument.Warnings.Count==0 &&
       movedDocument.Shapes.Count==2,
       "DXF exporter emits supported millimetre LINE/ARC entities");
var movedReimport=MixedContourAssembler.Assemble(movedDocument.Shapes);
Assert(movedReimport.Complete && movedReimport.ClosedPaths.Count==1,
       "Exported semicircular ring reopens as a closed analytic contour");
var movedReimportArea=PreviewContourMetrics.Measure(movedReimport.ClosedPaths[0]);
Assert(movedReimportArea.IsValid &&
       Math.Abs(movedReimportArea.AreaMm2-halfMetrics.AreaMm2)<1e-6,
       "DXF round trip preserves exact semicircular area");
var clockwiseExport=PreviewDxfExporter.Export([reversedSemidisc]);
var clockwiseParsed=MixedContourAssembler.Assemble(DxfReader.Parse(clockwiseExport).Shapes);
Assert(clockwiseParsed.Complete &&
       Math.Abs(PreviewContourMetrics.Measure(clockwiseParsed.ClosedPaths[0]).AreaMm2-
                halfMetrics.AreaMm2)<1e-6,
       "Clockwise arcs round-trip through DXF's counterclockwise ARC encoding");
var holesExport=PreviewDxfExporter.Export(circleRings.ClosedPaths);
var holesReimport=MixedContourAssembler.Assemble(DxfReader.Parse(holesExport).Shapes);
Assert(holesReimport.Complete &&
       Math.Abs(PreviewContourTopology.Analyze(holesReimport.ClosedPaths).NetAreaMm2-
                circleNesting.NetAreaMm2)<1e-6,
       "Nested circular rings survive DXF serialization without flattened arcs");
bool rejectedBadExport=false;
try { PreviewDxfExporter.Export([
    new PreviewPath([new PreviewLine(new(0,0),new(5,0))],false)
]); }
catch(FormatException) { rejectedBadExport=true; }
Assert(rejectedBadExport,"Open geometries cannot be exported as verified closed CAD");
bool rejectedIntersectingExport=false;
try { PreviewDxfExporter.Export(topologyCrossing.Rings.Count==0
    ? [new PreviewPath([
        new PreviewLine(new(0,0),new(10,10)),
        new PreviewLine(new(10,10),new(0,10)),
        new PreviewLine(new(0,10),new(10,0)),
        new PreviewLine(new(10,0),new(0,0))],true)]
    : []); }
catch(FormatException) { rejectedIntersectingExport=true; }
Assert(rejectedIntersectingExport, "Self-intersecting DXF geometry is refused");

// Straight closed LWPOLYLINE is exported through exact line geometry.
var polyForExport=new DxfPolyline("CUT",
    [new Point2(0,0),new Point2(80,0),
     new Point2(80,40),new Point2(0,40)],true);
var assembledPoly=MixedContourAssembler.Assemble([polyForExport]);
Assert(assembledPoly.Complete && assembledPoly.ClosedPaths.Count==1,
       "Closed straight LWPOLYLINE joins as a verified preview ring");
var exportedPoly=PreviewDxfExporter.Export(assembledPoly.ClosedPaths);
var reassembledPoly=MixedContourAssembler.Assemble(DxfReader.Parse(exportedPoly).Shapes);
Assert(reassembledPoly.Complete && reassembledPoly.ClosedPaths.Count==1 &&
       Math.Abs(PreviewContourMetrics.Measure(reassembledPoly.ClosedPaths[0]).AreaMm2-3200)<1e-7,
       "LWPOLYLINE exports as a valid closed LINE ring with preserved area");
Assert(!MixedContourAssembler.Assemble([polyForExport with { Closed=false }]).Complete,
       "Open LWPOLYLINE is rejected for editable contour export");
Assert(!MixedContourAssembler.Assemble([new DxfPolyline("CUT",
    [new Point2(0,0),new Point2(80,0),new Point2(80,0),new Point2(0,40)],true)]).Complete,
       "Zero-length edges in LWPOLYLINE are rejected");

// Bulge preview notices permit geometry-only export, never a CAM approval.
var safeBulgeDocument = new DxfImportResult([bulgedHalf,chord],
    ["Bulged LWPOLYLINE is preview-only: CAD/CAM geometry is not yet certified."],4);
var safeBulgePaths=PreviewDxfExporter.ValidateDocument(safeBulgeDocument);
Assert(safeBulgePaths.Count==1 &&
       Math.Abs(PreviewContourMetrics.Measure(safeBulgePaths[0]).AreaMm2-50*Math.PI)<1e-6,
       "Known bulge preview notice does not prevent verified geometry-only export");
var safeBulgeReimport=MixedContourAssembler.Assemble(
    DxfReader.Parse(PreviewDxfExporter.Export(safeBulgePaths)).Shapes);
Assert(safeBulgeReimport.Complete &&
       Math.Abs(PreviewContourMetrics.Measure(safeBulgeReimport.ClosedPaths[0]).AreaMm2-
                50*Math.PI)<1e-6,
       "Bulge converts to true ARC with preserved analytic area on reimport");
bool unsupportedWarningBlocked=false;
try { PreviewDxfExporter.ValidateDocument(safeBulgeDocument with
    { Warnings=["Unsupported entity SPLINE skipped"] }); }
catch(FormatException) { unsupportedWarningBlocked=true; }
Assert(unsupportedWarningBlocked,
       "Unknown or skipped DXF entities block edited drawing export");
bool unknownUnitBlocked=false;
try { PreviewDxfExporter.ValidateDocument(safeBulgeDocument with { InsUnits=0 }); }
catch(FormatException) { unknownUnitBlocked=true; }
Assert(unknownUnitBlocked,
       "Unconfirmed units block edited drawing export even with valid contour");

// B2 CAD drafting: relocate a single LINE vertex, not the entire shape.
var editSquare=new PreviewPath([
    new PreviewLine(new(0,0),new(10,0)),
    new PreviewLine(new(10,0),new(10,10)),
    new PreviewLine(new(10,10),new(0,10)),
    new PreviewLine(new(0,10),new(0,0))
],true);
var movedVertex=PreviewVertexEditor.MoveVertex(
    [editSquare],0,1,new PreviewPoint(12,0));
Assert(movedVertex.IsValidForPreview && !movedVertex.CanStartMachine &&
       Math.Abs(movedVertex.NetAreaMm2-110)<1e-8,
       "Moving one polygon vertex changes area without changing other vertices");
var firstEditedLine=(PreviewLine)movedVertex.Rings[0].Segments[0];
var secondEditedLine=(PreviewLine)movedVertex.Rings[0].Segments[1];
Assert(firstEditedLine.End==new PreviewPoint(12,0) &&
       secondEditedLine.Start==new PreviewPoint(12,0) &&
       firstEditedLine.Start==new PreviewPoint(0,0),
       "Both adjacent edges share the edited vertex exactly");
var savedVertexText=PreviewDxfExporter.Export(movedVertex.Rings);
var reopenedVertex=MixedContourAssembler.Assemble(DxfReader.Parse(savedVertexText).Shapes);
Assert(reopenedVertex.Complete &&
       Math.Abs(PreviewContourMetrics.Measure(reopenedVertex.ClosedPaths[0]).AreaMm2-110)<1e-7,
       "Single-vertex edit survives DXF export and reimport");
static bool RejectVertexEdit(IReadOnlyList<PreviewPath> paths,
    int ring, int vertex,PreviewPoint target)
{
    try { PreviewVertexEditor.MoveVertex(paths,ring,vertex,target);return false; }
    catch(FormatException) {return true; }
}
Assert(RejectVertexEdit([editSquare],0,1,new PreviewPoint(0,0)),
       "Vertex edit creating a zero-length edge is rejected");
Assert(RejectVertexEdit([editSquare],0,1,new PreviewPoint(-5,5)),
       "Vertex edit producing a self-intersection is rejected");
Assert(RejectVertexEdit([editSquare],0,1,new PreviewPoint(double.NaN,0)),
       "Nonfinite target rejected");
Assert(RejectVertexEdit([editSquare],3,0,new PreviewPoint(2,2)) &&
       RejectVertexEdit([editSquare],0,99,new PreviewPoint(2,2)),
       "Invalid ring/vertex indices are rejected");
var roundArcRing=new PreviewPath([
    new PreviewArc(new(0,0),10,0,180),
    new PreviewLine(new(-10,0),new(10,0))],true);
// Adding a vertex along a straight edge preserves geometry and round-trip DXF.
var insertedVertex=PreviewVertexEditor.InsertVertex(
    [editSquare],0,0,new PreviewPoint(5,0));
Assert(insertedVertex.Rings[0].Segments.Count==5 &&
       Math.Abs(insertedVertex.NetAreaMm2-100)<1e-8 &&
       !insertedVertex.CanStartMachine,
       "Vertex insertion subdivides a straight edge without changing area");
var insertedDxf=PreviewDxfExporter.Export(insertedVertex.Rings);
var insertedRoundTrip=MixedContourAssembler.Assemble(DxfReader.Parse(insertedDxf).Shapes);
Assert(insertedRoundTrip.Complete &&
       Math.Abs(PreviewContourTopology.Analyze(insertedRoundTrip.ClosedPaths).NetAreaMm2-100)<1e-7,
       "Inserted vertex survives DXF round-trip");
static bool RejectVertexInsertion(IReadOnlyList<PreviewPath> rings, int ring,
    int segment, PreviewPoint point)
{
    try { PreviewVertexEditor.InsertVertex(rings,ring,segment,point);return false; }
    catch(FormatException) { return true; }
}
Assert(RejectVertexInsertion([editSquare],0,0,new PreviewPoint(5,2)) &&
       RejectVertexInsertion([editSquare],0,0,new PreviewPoint(0,0)) &&
       RejectVertexInsertion([editSquare],0,0,new PreviewPoint(15,0)) &&
       RejectVertexInsertion([editSquare],0,99,new PreviewPoint(5,0)) &&
       RejectVertexInsertion([roundArcRing],0,0,new PreviewPoint(5,0)),
       "Vertex insertion rejects off-edge, endpoint, outside, invalid and arc edits");
Assert(RejectVertexEdit([roundArcRing],0,1,new PreviewPoint(1,1)),
       "Analytical ARC is never silently distorted by LINE-only vertex editor");
var outerRing=new PreviewPath([
    new PreviewLine(new(0,0),new(20,0)),
    new PreviewLine(new(20,0),new(20,20)),
    new PreviewLine(new(20,20),new(0,20)),
    new PreviewLine(new(0,20),new(0,0))],true);
var innerRing=new PreviewPath([
    new PreviewLine(new(5,5),new(15,5)),
    new PreviewLine(new(15,5),new(15,15)),
    new PreviewLine(new(15,15),new(5,15)),
    new PreviewLine(new(5,15),new(5,5))],true);
Assert(RejectVertexEdit([outerRing,innerRing],1,0,new PreviewPoint(0,0)),
       "Vertex editor rejects edits making an inner hole touch the outer boundary");


// Removing a vertex joins adjacent straight edges, with no machinery permission.
var deletedCorner = PreviewVertexEditor.DeleteVertex([editSquare], 0, 1);
Assert(deletedCorner.IsValidForPreview && !deletedCorner.CanStartMachine &&
       deletedCorner.Rings[0].Segments.Count == 3 &&
       Math.Abs(deletedCorner.NetAreaMm2 - 50) < 1e-8,
       "Deleting a square corner creates a valid triangle with recalculated area");
var deletedRoundTrip = MixedContourAssembler.Assemble(
    DxfReader.Parse(PreviewDxfExporter.Export(deletedCorner.Rings)).Shapes);
Assert(deletedRoundTrip.Complete &&
       Math.Abs(PreviewContourTopology.Analyze(deletedRoundTrip.ClosedPaths).NetAreaMm2-50)<1e-7,
       "Deleted corner survives DXF round trip");
var deleteInserted = PreviewVertexEditor.DeleteVertex(insertedVertex.Rings, 0, 1);
Assert(deleteInserted.Rings[0].Segments.Count == 4 &&
       Math.Abs(deleteInserted.NetAreaMm2-100)<1e-8,
       "Deleting an inserted collinear vertex restores original area");
static bool RejectVertexDeletion(IReadOnlyList<PreviewPath> rings, int ring, int vertex)
{
    try { PreviewVertexEditor.DeleteVertex(rings,ring,vertex); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectVertexDeletion([editSquare],0,99) &&
       RejectVertexDeletion([editSquare],3,0) &&
       RejectVertexDeletion(deletedCorner.Rings,0,0) &&
       RejectVertexDeletion([roundArcRing],0,0),
       "Deletion rejects invalid indices, triangles and arc rings");

// A valid edited polygon must not silently reclassify a previously nested
// hole as an independent part. This applies even without intersecting edges.
var roofOutline = new PreviewPath([
    new PreviewLine(new(0,0),new(20,0)),
    new PreviewLine(new(20,0),new(20,20)),
    new PreviewLine(new(20,20),new(10,30)),
    new PreviewLine(new(10,30),new(0,20)),
    new PreviewLine(new(0,20),new(0,0))
],true);
var roofHole = new PreviewPath([
    new PreviewLine(new(9,22),new(11,22)),
    new PreviewLine(new(11,22),new(11,24)),
    new PreviewLine(new(11,24),new(9,24)),
    new PreviewLine(new(9,24),new(9,22))
],true);
var nestedRoof = PreviewContourTopology.Analyze([roofOutline,roofHole]);
Assert(nestedRoof.IsValid && nestedRoof.Rings[1].ParentIndex==0 &&
       !nestedRoof.Rings[1].IsMaterialBoundary,
       "Initial roof geometry contains an inner hole");
Assert(RejectVertexDeletion([roofOutline,roofHole],0,3),
       "Deleting outer apex cannot silently change a hole to a separate part");
Assert(RejectVertexEdit([roofOutline,roofHole],0,3,new PreviewPoint(10,19)),
       "Moving apex cannot silently convert a hole into a separate part");
var safeRoofMove = PreviewVertexEditor.MoveVertex(
    [roofOutline,roofHole],0,3,new PreviewPoint(10,32));
var safeRoofTopology = PreviewContourTopology.Analyze(safeRoofMove.Rings);
Assert(safeRoofTopology.IsValid && safeRoofTopology.Rings[1].ParentIndex==0 &&
       !safeRoofTopology.Rings[1].IsMaterialBoundary,
       "Safe outline edit retains exactly the original hole ownership");
var safeRoofRoundTrip = PreviewDxfExporter.ValidateDocument(
    DxfReader.Parse(PreviewDxfExporter.Export(safeRoofMove.Rings)));
Assert(PreviewContourTopology.Analyze(safeRoofRoundTrip).Rings[1].ParentIndex==0,
       "Nested-hole ownership survives verified DXF export/import");

// B2 dimensional CAD editing: change one edge to an exact target length.
// The end vertex slides along the original edge ray while the start is fixed.
var lengthenedSquare = PreviewVertexEditor.SetEdgeLength([editSquare],0,0,12);
var lengthEdge=(PreviewLine)lengthenedSquare.Rings[0].Segments[0];
Assert(lengthenedSquare.IsValidForPreview && !lengthenedSquare.CanStartMachine &&
       lengthEdge.Start==new PreviewPoint(0,0) &&
       Math.Abs(lengthEdge.End.X-12)<1e-9 &&
       Math.Abs(lengthEdge.End.Y)<1e-9 &&
       Math.Abs(lengthenedSquare.NetAreaMm2-110)<1e-8,
       "Changing first edge length preserves its anchor and recomputes material area");
var closingLength = PreviewVertexEditor.SetEdgeLength([editSquare],0,3,12);
var lastEdge=(PreviewLine)closingLength.Rings[0].Segments[3];
Assert(Math.Abs(lastEdge.End.Y+2)<1e-8 &&
       lastEdge.Start==new PreviewPoint(0,10) &&
       Math.Abs(closingLength.NetAreaMm2-110)<1e-8,
       "Last closing edge moves vertex zero and keeps its end attached");
var lengthReopened=PreviewDxfExporter.ValidateDocument(
    DxfReader.Parse(PreviewDxfExporter.Export(lengthenedSquare.Rings)));
Assert(PreviewContourTopology.Analyze(lengthReopened).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(lengthReopened).NetAreaMm2-110)<1e-7,
       "Dimensioned contour survives checked DXF export and reimport");
static bool RejectEdgeLength(IReadOnlyList<PreviewPath> rings,
    int ring, int segment, double length)
{
    try { PreviewVertexEditor.SetEdgeLength(rings,ring,segment,length); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectEdgeLength([editSquare],0,0,0) &&
       RejectEdgeLength([editSquare],0,0,double.NaN) &&
       RejectEdgeLength([editSquare],0,0,double.PositiveInfinity) &&
       RejectEdgeLength([editSquare],0,0,1_000_001) &&
       RejectEdgeLength([editSquare],0,99,12) &&
       RejectEdgeLength([roundArcRing],0,0,12),
       "Dimension editing rejects zero, invalid, extreme, missing and arc dimensions");
Assert(RejectEdgeLength([roofOutline,roofHole],0,2,6),
       "Reducing angled roof edge must reject loss of enclosed hole or crossing");

// B2 absolute XY bearing editing for a straight, closed CAD contour.
var angledSquare = PreviewVertexEditor.SetEdgeAngle([editSquare],0,0,30);
var angledEdge = (PreviewLine)angledSquare.Rings[0].Segments[0];
var angleLength = Math.Sqrt(
    Math.Pow(angledEdge.End.X - angledEdge.Start.X,2) +
    Math.Pow(angledEdge.End.Y - angledEdge.Start.Y,2));
Assert(angledSquare.IsValidForPreview && !angledSquare.CanStartMachine &&
       angledEdge.Start == new PreviewPoint(0,0) &&
       Math.Abs(angledEdge.End.X - 5 * Math.Sqrt(3)) < 1e-8 &&
       Math.Abs(angledEdge.End.Y - 5) < 1e-8 &&
       Math.Abs(angleLength - 10) < 1e-8,
       "SetEdgeAngle preserves edge start and length, moving its end to 30 degrees");
var angledReopened = PreviewDxfExporter.ValidateDocument(
    DxfReader.Parse(PreviewDxfExporter.Export(angledSquare.Rings)));
var angledReopenedEdge = (PreviewLine)angledReopened[0].Segments[0];
Assert(PreviewContourTopology.Analyze(angledReopened).IsValid &&
       Math.Abs(Math.Atan2(angledReopenedEdge.End.Y - angledReopenedEdge.Start.Y,
                           angledReopenedEdge.End.X - angledReopenedEdge.Start.X)
                  - Math.PI / 6) < 1e-8,
       "Requested 30 degree angle survives DXF save and reimport");
var closingAngle = PreviewVertexEditor.SetEdgeAngle([editSquare],0,3,-90);
Assert(PreviewContourTopology.Analyze(closingAngle.Rings).IsValid &&
       Math.Abs(closingAngle.NetAreaMm2 - 100) < 1e-8,
       "Closing edge supports negative clockwise bearing without topology corruption");
static bool RejectEdgeAngle(IReadOnlyList<PreviewPath> rings,
    int ring, int segment, double degrees)
{
    try { PreviewVertexEditor.SetEdgeAngle(rings,ring,segment,degrees); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectEdgeAngle([editSquare],0,0,90) &&
       RejectEdgeAngle([editSquare],0,0,double.NaN) &&
       RejectEdgeAngle([editSquare],0,0,double.PositiveInfinity) &&
       RejectEdgeAngle([editSquare],0,0,361) &&
       RejectEdgeAngle([editSquare],0,99,30) &&
       RejectEdgeAngle([roundArcRing],0,0,30),
       "Angle editing rejects degenerate, nonfinite, unbounded, missing and ARC input");
Assert(RejectEdgeAngle([roofOutline,roofHole],0,2,0),
       "Angle editing must reject edits that destroy nested hole ownership");

// Round-trip integrity must compare each ring, not merely its total area.
// A changed rectangle can preserve area while altering width/height.
var verifiedRect = PreviewDxfRoundTripVerifier.Verify(
    [editSquare],PreviewDxfExporter.Export([editSquare]));
Assert(verifiedRect.Count == 1 && verifiedRect[0].Segments.Count == 4,
       "Per-ring round-trip integrity accepts unchanged straight geometry");
var holeChecked = PreviewDxfRoundTripVerifier.Verify(
    [outerRing,innerRing],PreviewDxfExporter.Export([outerRing,innerRing]));
Assert(holeChecked.Count == 2,
       "Round-trip verifier preserves nested hole and material area");
var curveChecked = PreviewDxfRoundTripVerifier.Verify(
    [safeSemicircle],PreviewDxfExporter.Export([safeSemicircle]));
Assert(curveChecked.Count == 1 &&
       curveChecked[0].Segments.Any(s => s is PreviewArc),
       "Round-trip verifier preserves analytic circular arcs");
static bool RejectAlteredDxf(IReadOnlyList<PreviewPath> original, string content)
{
    try { PreviewDxfRoundTripVerifier.Verify(original,content); return false; }
    catch (FormatException) { return true; }
}
var sameAreaDifferentShape = new PreviewPath([
    new PreviewLine(new(0,0),new(20,0)),
    new PreviewLine(new(20,0),new(20,5)),
    new PreviewLine(new(20,5),new(0,5)),
    new PreviewLine(new(0,5),new(0,0))
],true);
Assert(RejectAlteredDxf([editSquare],PreviewDxfExporter.Export([sameAreaDifferentShape])),
       "DXF geometry change is rejected even when net area stays identical");
var shiftedRect = PreviewContourTransform.Move(editSquare,2,0);
Assert(RejectAlteredDxf([editSquare],PreviewDxfExporter.Export([shiftedRect])),
       "A translation with identical area and perimeter is still detected");
Assert(RejectAlteredDxf([outerRing,innerRing],
       PreviewDxfExporter.Export([outerRing])),
       "Dropping a nested hole from DXF export fails integrity check");

// Mirror images of an asymmetric notch can have the same area,
// perimeter, entity counts and bounding box, yet different CAD shape.
var asymmetricNotch = new PreviewPath([
    new PreviewLine(new(0,0),new(10,0)),
    new PreviewLine(new(10,0),new(10,10)),
    new PreviewLine(new(10,10),new(3,7)),
    new PreviewLine(new(3,7),new(0,10)),
    new PreviewLine(new(0,10),new(0,0))
],true);
var mirroredNotch = new PreviewPath([
    new PreviewLine(new(0,0),new(10,0)),
    new PreviewLine(new(10,0),new(10,10)),
    new PreviewLine(new(10,10),new(7,7)),
    new PreviewLine(new(7,7),new(0,10)),
    new PreviewLine(new(0,10),new(0,0))
],true);
var leftNotchMetrics = PreviewContourMetrics.Measure(asymmetricNotch);
var rightNotchMetrics = PreviewContourMetrics.Measure(mirroredNotch);
Assert(leftNotchMetrics.IsValid && rightNotchMetrics.IsValid &&
       Math.Abs(leftNotchMetrics.AreaMm2-rightNotchMetrics.AreaMm2)<1e-8 &&
       Math.Abs(leftNotchMetrics.PerimeterMm-rightNotchMetrics.PerimeterMm)<1e-8,
       "Asymmetric mirror regression uses identical area and perimeter");
Assert(RejectAlteredDxf([asymmetricNotch],
       PreviewDxfExporter.Export([mirroredNotch])),
       "Segment-by-segment DXF verifier rejects mirrored geometry despite equal metrics");

// B2: numeric editing of actual DXF CIRCLE holes, with strict containment.
var circleDoc = Document(rectangle, circleHole);
var enlargedHole = PreviewCircleHoleEditor.SetHole(circleDoc, 0, new PreviewPoint(100,50), 30);
var circleAfter = (DxfCircle)enlargedHole.Shapes[1];
Assert(Math.Abs(circleAfter.Radius-15)<1e-9 &&
       Math.Abs(PreviewContourTopology.Analyze(
           PreviewDxfExporter.ValidateDocument(enlargedHole)).NetAreaMm2-
           (20000-Math.PI*225))<1e-6,
       "Increasing circular hole diameter updates exact net area");
var shiftedHole = PreviewCircleHoleEditor.SetHole(circleDoc,0,new PreviewPoint(120,50),20);
Assert(((DxfCircle)shiftedHole.Shapes[1]).Center.X==120 &&
       PreviewContourTopology.Analyze(
           PreviewDxfExporter.ValidateDocument(shiftedHole)).IsValid,
       "Moving a circular hole preserves containment and CAD topology");
var twoCircleDoc = Document(rectangle,
    new DxfCircle("HOLE",new Point2(50,50),10),
    new DxfCircle("HOLE",new Point2(100,50),10));
var secondMoved = PreviewCircleHoleEditor.SetHole(
    twoCircleDoc,1,new PreviewPoint(115,50),20);
Assert(((DxfCircle)secondMoved.Shapes[1]).Center.X==50 &&
       ((DxfCircle)secondMoved.Shapes[2]).Center.X==115,
       "Circle indexing targets only the selected DXF hole");
static bool RejectHoleEdit(DxfImportResult doc, int index, double x, double y, double diameter)
{
    try { PreviewCircleHoleEditor.SetHole(doc,index,new PreviewPoint(x,y),diameter); return false; }
    catch (FormatException) {return true;}
}
Assert(RejectHoleEdit(circleDoc,0,195,50,20) &&
       RejectHoleEdit(circleDoc,0,100,50,110) &&
       RejectHoleEdit(circleDoc,0,100,50,0) &&
       RejectHoleEdit(circleDoc,0,double.NaN,50,20) &&
       RejectHoleEdit(circleDoc,99,100,50,20),
       "Hole editing rejects boundary contact, oversize, zero, NaN and invalid index");
Assert(RejectHoleEdit(twoCircleDoc,0,90,50,20),
       "Hole editing rejects intersections with another circular hole");
Assert(RejectHoleEdit(Document(new DxfCircle("CUT",new Point2(50,50),20)),
       0,50,50,30),
       "An outer circular part cannot be edited as an inner hole");
var editedHolePaths = PreviewDxfExporter.ValidateDocument(enlargedHole);
var reopenedHolePaths = PreviewDxfRoundTripVerifier.Verify(
    editedHolePaths, PreviewDxfExporter.Export(editedHolePaths));
Assert(reopenedHolePaths.Count==2 &&
       PreviewContourTopology.Analyze(reopenedHolePaths).IsValid,
       "Edited circular hole survives strict DXF export/reimport checks");

// B2: bounded in-memory CAD preview history; geometry-only, not machine state.
var historySource = Document(rectangle,circleHole);
var history = new DxfPreviewHistory(historySource);
Assert(!history.CanUndo && !history.CanRedo &&
       history.Current.Shapes.Count==2,"Fresh DXF history starts with no undo/redo");
var changedCircle = PreviewCircleHoleEditor.SetHole(history.Current,0,new(100,50),30);
history.Push(changedCircle);
Assert(history.CanUndo && !history.CanRedo &&
       ((DxfCircle)history.Current.Shapes[1]).Radius==15,
       "Verified circle edit is recorded in history");
var previousCircle = history.Undo();
Assert(((DxfCircle)previousCircle.Shapes[1]).Radius==10 &&
       history.CanRedo && !history.CanUndo,
       "Undo restores previous hole dimensions");
var forwardCircle = history.Redo();
Assert(((DxfCircle)forwardCircle.Shapes[1]).Radius==15 &&
       !history.CanRedo,"Redo restores edited hole dimensions");
// A returned shape list or source array must never alter stored undo entries.
var stolenList = (DxfShape[])history.Current.Shapes;
stolenList[1] = new DxfCircle("HOLE",new Point2(100,50),1);
Assert(((DxfCircle)history.Current.Shapes[1]).Radius==15,
       "Returned DXF history snapshots are isolated from caller mutation");
var mutableVertices = new List<Point2> {new(0,0),new(10,0),new(10,10),new(0,10)};
var vertexHistory = new DxfPreviewHistory(Document(new DxfPolyline(
    "CUT",mutableVertices,true)));
mutableVertices[0]=new Point2(500,500);
Assert(((DxfPolyline)vertexHistory.Current.Shapes[0]).Vertices[0]==new Point2(0,0),
       "History deep-copies polyline vertices");
// New edit after undo discards the abandoned redo branch.
history.Undo();
history.Push(PreviewCircleHoleEditor.SetHole(history.Current,0,new(100,50),25));
Assert(!history.CanRedo && history.CanUndo &&
       ((DxfCircle)history.Current.Shapes[1]).Radius==12.5,
       "New edit after undo invalidates redo branch");
var bounded = new DxfPreviewHistory(historySource);
for(int i=0;i<40;i++) bounded.Push(historySource);
Assert(bounded.UndoCount==30 && !bounded.CanRedo,
       "Undo history remains bounded at thirty previous states");
for(int i=0;i<30;i++) bounded.Undo();
Assert(!bounded.CanUndo && bounded.RedoCount==30,
       "Oldest retained snapshot is the fixed undo boundary");

// B2: adding a circular hole inside a verified outer material boundary.
var justPlate = Document(rectangle);
var withAddedHole = PreviewCircleHoleEditor.AddHole(
    justPlate, new PreviewPoint(100,50), 20);
var afterAdd = PreviewContourTopology.Analyze(
    PreviewDxfExporter.ValidateDocument(withAddedHole));
Assert(afterAdd.IsValid && afterAdd.Rings.Count==2 &&
       afterAdd.Rings[1].Depth==1 && !afterAdd.Rings[1].IsMaterialBoundary &&
       Math.Abs(afterAdd.NetAreaMm2-(20000-Math.PI*100))<1e-6,
       "New DXF hole is nested in part and material area decreases");
var withTwoAddedHoles = PreviewCircleHoleEditor.AddHole(
    withAddedHole,new PreviewPoint(145,50),20);
Assert(PreviewContourTopology.Analyze(
       PreviewDxfExporter.ValidateDocument(withTwoAddedHoles)).Rings.Count==3,
       "A second independently verified circular hole can be added");
static bool RejectAddHole(DxfImportResult document, double x,double y,double diameter)
{
    try {PreviewCircleHoleEditor.AddHole(document,new PreviewPoint(x,y),diameter); return false;}
    catch (FormatException) {return true;}
}
Assert(RejectAddHole(justPlate,198,50,20) &&
       RejectAddHole(justPlate,100,50,0) &&
       RejectAddHole(justPlate,100,50,double.NaN) &&
       RejectAddHole(justPlate,double.PositiveInfinity,50,20),
       "Hole creation rejects boundary, nonpositive and nonfinite dimensions");
Assert(RejectAddHole(withAddedHole,105,50,20),
       "New hole must not intersect an existing hole");
Assert(RejectAddHole(circleDoc,100,50,6),
       "A hole nested inside another void cannot become a material island");
var addedHolePaths=PreviewDxfExporter.ValidateDocument(withAddedHole);
Assert(PreviewDxfRoundTripVerifier.Verify(addedHolePaths,
       PreviewDxfExporter.Export(addedHolePaths)).Count==2,
       "New circular hole survives strict DXF export and reimport");
var addingHistory = new DxfPreviewHistory(justPlate);
addingHistory.Push(withAddedHole);
Assert(addingHistory.Undo().Shapes.Count==1 &&
       addingHistory.Redo().Shapes.Count==2,
       "New hole can be undone and redone without modifying original DXF");

// Stage 2: explicit 3D constructive solid geometry for a plate with holes.
var scadPlate = TextPlateSketch.Parse("PLATE 200 100 5; HOLE 50 50 10; HOLE 150 50 8");
var scad = PlateOpenScadExporter.Export(scadPlate);
Assert(scad.Contains("difference()") &&
       scad.Contains("cube([200, 100, 5]") &&
       scad.Contains("translate([50, 50, 0]) cylinder(h = 5, r = 10") &&
       scad.Contains("translate([150, 50, 0]) cylinder(h = 5, r = 8") &&
       scad.Split("cylinder(").Length - 1 == 2,
       "OpenSCAD exports a three-dimensional solid with both real through-holes");
Assert(Math.Abs(scadPlate.VolumeMm3 -
       (20000 - Math.PI*164)*5) < 1e-7,
       "3D parametric volume matches 2D analytical geometry and plate thickness");
var emptyScad = PlateOpenScadExporter.Export(TextPlateSketch.Parse("PLATE 20 10 2"));
Assert(emptyScad.Contains("cube([20, 10, 2]") &&
       !emptyScad.Contains("cylinder("), "Solid plate without holes exports correctly");
var deScad = PlateOpenScadExporter.Export(TextPlateSketch.Parse("PLATE 12.5 7.25 2.5"));
Assert(deScad.Contains("12.5") && !deScad.Contains("12,5"),
       "3D export uses invariant decimal formatting");
static bool RejectScad(ExtrudedPlatePreview plate)
{
    try { PlateOpenScadExporter.Export(plate); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectScad(scadPlate with { ThicknessMm = -1 }) &&
       RejectScad(scadPlate with { NetAreaMm2 = 123 }) &&
       RejectScad(scadPlate with { VolumeMm3 = double.NaN }),
       "3D exporter refuses inconsistent dimensions and nonfinite volume");

// Synchronized 3D is generated from CURRENT DXF state, not original text.
var syncedOriginal = DxfSynchronized3DExporter.Export(withAddedHole,5);
Assert(syncedOriginal.HoleCount==1 &&
       syncedOriginal.OpenScad.Contains("linear_extrude(height = 5") &&
       syncedOriginal.OpenScad.Contains("cylinder(h = ") &&
       syncedOriginal.OpenScad.Contains("r = 10") &&
       Math.Abs(syncedOriginal.VolumeMm3 -
           (20000-Math.PI*100)*5)<1e-6 &&
       !syncedOriginal.CanStartMachine,
       "Current edited DXF produces an analytic 3D extrusion with actual holes");
var currentHoleMoved = PreviewCircleHoleEditor.SetHole(
    withAddedHole,0,new PreviewPoint(120,50),30);
var syncedHoleMoved = DxfSynchronized3DExporter.Export(currentHoleMoved,7);
Assert(syncedHoleMoved.OpenScad.Contains("translate([120, 50, -") &&
       syncedHoleMoved.OpenScad.Contains("r = 15") &&
       Math.Abs(syncedHoleMoved.VolumeMm3 -
           (20000-Math.PI*225)*7)<1e-6,
       "3D tracks changed DXF hole center, radius, thickness and exact volume");
var syncedUndo = new DxfPreviewHistory(withAddedHole);
syncedUndo.Push(currentHoleMoved);
Assert(DxfSynchronized3DExporter.Export(syncedUndo.Undo(),5)
           .OpenScad.Contains("r = 10") &&
       DxfSynchronized3DExporter.Export(syncedUndo.Redo(),5)
           .OpenScad.Contains("r = 15"),
       "3D export follows undo/redo drawing state, not stale text parameters");
var changedOuter = PreviewVertexEditor.MoveVertex(
    PreviewDxfExporter.ValidateDocument(withAddedHole),0,1,new PreviewPoint(210,0));
var changedOuterDoc = DxfReader.Parse(
    PreviewDxfExporter.Export(changedOuter.Rings));
var syncedOuter = DxfSynchronized3DExporter.Export(changedOuterDoc,5);
Assert(syncedOuter.OpenScad.Contains("[210, 0]") &&
       Math.Abs(syncedOuter.NetAreaMm2-changedOuter.NetAreaMm2)<1e-6 &&
       syncedOuter.HoleCount==1,
       "3D tracks edited outer LINE geometry and reconstructed ARC circle holes");
static bool RejectSynchronized3D(DxfImportResult source,double thickness)
{
    try { DxfSynchronized3DExporter.Export(source,thickness); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectSynchronized3D(withAddedHole,0) &&
       RejectSynchronized3D(withAddedHole,double.NaN) &&
       RejectSynchronized3D(withAddedHole,double.PositiveInfinity) &&
       RejectSynchronized3D(Document(new DxfCircle("CUT",new Point2(50,50),20)),5),
       "3D refuses zero/nonfinite thickness and unsupported curved exteriors");
var twoPartsDoc=Document(rectangle,
    Closed("CUT",new Point2(300,0),new Point2(320,0),
          new Point2(320,20),new Point2(300,20)));
Assert(RejectSynchronized3D(twoPartsDoc,5),
       "3D still refuses multiple independent parts");
// Rectangle + rectangular hole + true circle: all three profiles must be
// represented by CSG, with a matching analytic 2D area and 3D volume.
var mixedHoles3D = DxfSynchronized3DExporter.Export(
    Document(rectangle,innerHole,circleHole),6);
Assert(mixedHoles3D.HoleCount == 2 &&
       mixedHoles3D.OpenScad.Contains("polygon(points = [[20, 20]") &&
       mixedHoles3D.OpenScad.Contains("cylinder(h = ") &&
       mixedHoles3D.OpenScad.Contains("r = 10") &&
       mixedHoles3D.OpenScad.Split("linear_extrude(height = ").Length - 1 == 2 &&
       Math.Abs(mixedHoles3D.NetAreaMm2 -
           (20000-400-Math.PI*100)) < 1e-6 &&
       Math.Abs(mixedHoles3D.VolumeMm3 -
           (20000-400-Math.PI*100)*6)<1e-5,
       "3D extrudes exact polygon hole and circular hole from current DXF");
var polygonOnly3D = DxfSynchronized3DExporter.Export(
    Document(rectangle,innerHole),3);
Assert(polygonOnly3D.HoleCount == 1 &&
       !polygonOnly3D.OpenScad.Contains("cylinder(") &&
       polygonOnly3D.OpenScad.Contains("polygon(points = [[20, 20]"),
       "3D handles a polygonal hole without a circular cutter");
var editedPolygonHole = PreviewVertexEditor.MoveVertex(
    PreviewDxfExporter.ValidateDocument(Document(rectangle,innerHole)),
    1,1,new PreviewPoint(45,20));
var editedPolygonDoc = DxfReader.Parse(
    PreviewDxfExporter.Export(editedPolygonHole.Rings));
var editedPolygon3D = DxfSynchronized3DExporter.Export(editedPolygonDoc,4);
Assert(editedPolygon3D.OpenScad.Contains("[45, 20]") &&
       Math.Abs(editedPolygon3D.NetAreaMm2-editedPolygonHole.NetAreaMm2)<1e-6,
       "3D polygon hole follows vertex editing and DXF export/reimport");
// A material island surrounded by a void needs a more general CSG hierarchy.
var islandDoc = Document(rectangle,innerHole,
    Closed("ISLAND",new Point2(25,25),new Point2(30,25),
           new Point2(30,30),new Point2(25,30)));
Assert(RejectSynchronized3D(islandDoc,4),
       "3D refuses nested material island instead of silently losing it");

// B2 closing regression: multi-part and material-island CSG from current DXF.
var assemblyTwoParts = DxfAssembly3DExporter.Export(twoPartsDoc, 6);
Assert(assemblyTwoParts.MaterialRegionCount == 2 &&
       assemblyTwoParts.HoleCount == 0 &&
       assemblyTwoParts.OpenScad.Contains("union()") &&
       assemblyTwoParts.OpenScad.Split("difference()").Length-1 == 2 &&
       Math.Abs(assemblyTwoParts.VolumeMm3-(20000+400)*6)<1e-6,
       "Two separate DXF parts become two material regions in 3D CSG");
var assemblyIsland = DxfAssembly3DExporter.Export(islandDoc, 4);
Assert(assemblyIsland.MaterialRegionCount == 2 &&
       assemblyIsland.HoleCount == 1 &&
       assemblyIsland.OpenScad.Split("difference()").Length-1 == 2 &&
       Math.Abs(assemblyIsland.VolumeMm3-(20000-400+25)*4)<1e-6,
       "A nested material island is restored after subtracting its containing hole");
var assemblyMixed = DxfAssembly3DExporter.Export(
    Document(rectangle,innerHole,circleHole),5);
Assert(assemblyMixed.MaterialRegionCount == 1 &&
       assemblyMixed.HoleCount == 2 &&
       assemblyMixed.OpenScad.Contains("cylinder(h = ") &&
       assemblyMixed.OpenScad.Contains("polygon(points = [[20, 20]") &&
       Math.Abs(assemblyMixed.VolumeMm3 -
           (20000-400-Math.PI*100)*5)<1e-6,
       "Mixed polygonal and round DXF holes export into one 3D part");
var assemblyUndo = new DxfPreviewHistory(withAddedHole);
assemblyUndo.Push(currentHoleMoved);
var volumeBefore = DxfAssembly3DExporter.Export(assemblyUndo.Current,5).VolumeMm3;
var volumeAfterUndo = DxfAssembly3DExporter.Export(assemblyUndo.Undo(),5).VolumeMm3;
Assert(volumeAfterUndo>volumeBefore &&
       Math.Abs(volumeAfterUndo - (20000-Math.PI*100)*5)<1e-6,
       "3D assembly follows current DXF undo state including hole dimensions");
static bool RejectAssembly3D(DxfImportResult drawing,double thickness)
{
    try { DxfAssembly3DExporter.Export(drawing,thickness); return false; }
    catch(FormatException) {return true;}
}
Assert(RejectAssembly3D(Document(new DxfCircle("CUT",new Point2(0,0),20)),0) &&
       RejectAssembly3D(Document(rectangle,innerHole),double.NaN) &&
       RejectAssembly3D(Document(rectangle,innerHole),double.PositiveInfinity),
       "3D assembly exporter rejects invalid thickness without generating files");

// B2: edit a true circle reconstructed from four DXF ARC segments, not only CIRCLE.
var circleAsArcsDoc = DxfReader.Parse(PreviewDxfExporter.Export(
    PreviewDxfExporter.ValidateDocument(Document(rectangle,circleHole))));
Assert(circleAsArcsDoc.Shapes.All(s=>s is DxfLine or DxfArc) &&
       circleAsArcsDoc.Shapes.Count(s=>s is DxfArc)==4,
       "CIRCLE becomes four analytical ARC entities after DXF export");
var editedArcDiameter = PreviewArcCircleEditor.SetDiameter(circleAsArcsDoc,1,30);
var editedArcRings = PreviewDxfExporter.ValidateDocument(editedArcDiameter);
var editedArcTopo = PreviewContourTopology.Analyze(editedArcRings);
Assert(editedArcTopo.IsValid &&
       editedArcRings[1].Segments.All(s=>s is PreviewArc a &&
           Math.Abs(a.RadiusMm-15)<1e-8) &&
       Math.Abs(editedArcTopo.NetAreaMm2 -
           (20000-Math.PI*225)) < 1e-6,
       "Four-ARC circular hole diameter edit changes exact area");
var arcCircleHistory = new DxfPreviewHistory(circleAsArcsDoc);
arcCircleHistory.Push(editedArcDiameter);
Assert(Math.Abs(((PreviewArc)PreviewDxfExporter.ValidateDocument(
        arcCircleHistory.Undo())[1].Segments[0]).RadiusMm-10)<1e-8 &&
       Math.Abs(((PreviewArc)PreviewDxfExporter.ValidateDocument(
        arcCircleHistory.Redo())[1].Segments[0]).RadiusMm-15)<1e-8,
       "Four-ARC diameter edit can be undone and redone");
static bool RejectArcCircleDiameter(DxfImportResult doc,int index,double diameter)
{
    try{PreviewArcCircleEditor.SetDiameter(doc,index,diameter);return false;}
    catch(FormatException){return true;}
}
Assert(RejectArcCircleDiameter(circleAsArcsDoc,1,0) &&
       RejectArcCircleDiameter(circleAsArcsDoc,1,double.NaN) &&
       RejectArcCircleDiameter(circleAsArcsDoc,1,250) &&
       RejectArcCircleDiameter(circleAsArcsDoc,1,20_000_000) &&
       RejectArcCircleDiameter(circleAsArcsDoc,99,30) &&
       RejectArcCircleDiameter(circleAsArcsDoc,0,30),
       "Four-ARC editor rejects nonfinite/outsize, index and noncircle rings");
var twoArcHolesDoc = DxfReader.Parse(PreviewDxfExporter.Export(
    PreviewDxfExporter.ValidateDocument(twoCircleDoc)));
Assert(RejectArcCircleDiameter(twoArcHolesDoc,1,110),
       "Four-ARC editing rejects circle collisions with the other hole");
var editedArcScad = DxfAssembly3DExporter.Export(editedArcDiameter,5);
Assert(editedArcScad.OpenScad.Contains("r = 15") &&
       Math.Abs(editedArcScad.VolumeMm3-(20000-Math.PI*225)*5)<1e-6,
       "Four-ARC changed geometry reaches synchronized 3D assembly");

// Stage 2: change one analytical ARC radius while locking chord endpoints.
var semicircleArcSet = PreviewArcRadiusEditor.SetRadius([safeSemicircle],0,0,12);
var modifiedArc = (PreviewArc)semicircleArcSet.Rings[0].Segments[0];
var beforeArcArea=PreviewContourMetrics.Measure(safeSemicircle).AreaMm2;
Assert(semicircleArcSet.IsValidForPreview &&
       !semicircleArcSet.CanStartMachine &&
       Math.Abs(modifiedArc.RadiusMm-12)<1e-9 &&
       Math.Abs(modifiedArc.SweepDegrees)<180 &&
       semicircleArcSet.NetAreaMm2<beforeArcArea,
       "Radius edit preserves chord endpoints and decreases semicircular cap area");
var radiusDxf=PreviewDxfExporter.Export(semicircleArcSet.Rings);
var radiusRoundtrip=PreviewDxfRoundTripVerifier.Verify(
    semicircleArcSet.Rings,radiusDxf);
Assert(radiusRoundtrip[0].Segments.OfType<PreviewArc>()
           .Any(a=>Math.Abs(a.RadiusMm-12)<1e-8),
       "Edited ARC radius survives exact DXF export/import");
static bool RejectArcRadius(IReadOnlyList<PreviewPath> rings,int ring,int segment,double radius)
{
    try { PreviewArcRadiusEditor.SetRadius(rings,ring,segment,radius);return false; }
    catch(FormatException) {return true;}
}
Assert(RejectArcRadius([safeSemicircle],0,0,9) &&
       RejectArcRadius([safeSemicircle],0,0,0) &&
       RejectArcRadius([safeSemicircle],0,0,double.NaN) &&
       RejectArcRadius([safeSemicircle],0,0,double.PositiveInfinity) &&
       RejectArcRadius([safeSemicircle],0,1,12) &&
       RejectArcRadius([safeSemicircle],4,0,12),
       "ARC radius editing rejects impossible radii, nonfinite data and LINE segments");
var nestedHalfDisc = new PreviewPath([
    new PreviewArc(new(50,50),10,0,180),
    new PreviewLine(new(40,50),new(60,50))],true);
var rectContainingArc = new PreviewPath([
    new PreviewLine(new(0,0),new(100,0)),
    new PreviewLine(new(100,0),new(100,100)),
    new PreviewLine(new(100,100),new(0,100)),
    new PreviewLine(new(0,100),new(0,0))],true);
var nestedArcSet=PreviewArcRadiusEditor.SetRadius(
    [rectContainingArc,nestedHalfDisc],1,0,12);
Assert(PreviewContourTopology.Analyze(nestedArcSet.Rings).IsValid &&
       nestedArcSet.Rings.Count==2,
       "Nested analytic ARC edit retains hole identity");
var restoredHalfDisc = PreviewArcRadiusEditor.SetRadius(
    nestedArcSet.Rings,1,0,10);
Assert(Math.Abs(restoredHalfDisc.NetAreaMm2 -
       PreviewContourTopology.Analyze([rectContainingArc,nestedHalfDisc]).NetAreaMm2) < 1e-6 &&
       Math.Abs(((PreviewArc)restoredHalfDisc.Rings[1].Segments[0]).SweepDegrees-180)<1e-7,
       "Endpoint-locked ARC radius can be restored without changing nested topology");

// B2 numeric regression: both ARC directions and minor/major sweeps. Use
// an independent circular-segment area formula, not the CAD integration code.
int testedArcBranches = 0;
foreach (double originalSweep in new[]{60d,120d,240d,300d,-60d,-120d,-240d,-300d})
{
    var arcCenter = new PreviewPoint(500,500);
    var initialArc = new PreviewArc(arcCenter,20,25,originalSweep);
    static PreviewPoint ArcAt(PreviewArc a, double angle)
    {
        var rad = angle * Math.PI / 180;
        return new PreviewPoint(a.Center.X+a.RadiusMm*Math.Cos(rad),
                                a.Center.Y+a.RadiusMm*Math.Sin(rad));
    }
    var fixedStart=ArcAt(initialArc,initialArc.StartDegrees);
    var fixedEnd=ArcAt(initialArc,initialArc.StartDegrees+initialArc.SweepDegrees);
    var closedArc = new PreviewPath([
        initialArc,new PreviewLine(fixedEnd,fixedStart)],true);
    var originalMeasure=PreviewContourTopology.Analyze([closedArc]);
    Assert(originalMeasure.IsValid,"ARC regression source must be a valid ring");
    foreach(double targetRadius in new[]{25d,40d})
    {
        var editedArcRing=PreviewArcRadiusEditor.SetRadius(
            [closedArc],0,0,targetRadius).Rings[0];
        var changed=(PreviewArc)editedArcRing.Segments[0];
        var currentStart=ArcAt(changed,changed.StartDegrees);
        var currentEnd=ArcAt(changed,changed.StartDegrees+changed.SweepDegrees);
        Assert(Math.Abs(changed.RadiusMm-targetRadius)<1e-8 &&
               Math.Sqrt(Math.Pow(currentStart.X-fixedStart.X,2)+
                         Math.Pow(currentStart.Y-fixedStart.Y,2))<1e-6 &&
               Math.Sqrt(Math.Pow(currentEnd.X-fixedEnd.X,2)+
                         Math.Pow(currentEnd.Y-fixedEnd.Y,2))<1e-6,
               "CW/CCW ARC edit preserves chord endpoints and the exact radius");
        Assert(Math.Sign(changed.SweepDegrees)==Math.Sign(originalSweep) &&
               (Math.Abs(changed.SweepDegrees)>180)==(Math.Abs(originalSweep)>180),
               "ARC edit preserves CW/CCW direction and original minor/major branch");
        var absoluteSweep=Math.Abs(changed.SweepDegrees)*Math.PI/180;
        var independentArea=0.5*targetRadius*targetRadius*
            (absoluteSweep-Math.Sin(absoluteSweep));
        var independentPerimeter=targetRadius*absoluteSweep+
            Math.Sqrt(Math.Pow(fixedStart.X-fixedEnd.X,2)+
                      Math.Pow(fixedStart.Y-fixedEnd.Y,2));
        var actualMeasure=PreviewContourMetrics.Measure(editedArcRing);
        Assert(actualMeasure.IsValid &&
               Math.Abs(actualMeasure.AreaMm2-independentArea)<1e-6 &&
               Math.Abs(actualMeasure.PerimeterMm-independentPerimeter)<1e-6,
               "Analytic ARC area and perimeter match independent circular segment equations");
        testedArcBranches++;
    }
    var halfChord=Math.Sqrt(Math.Pow(fixedStart.X-fixedEnd.X,2)+
                            Math.Pow(fixedStart.Y-fixedEnd.Y,2))/2;
    Assert(RejectArcRadius([closedArc],0,0,halfChord*(1-1e-12)),
           "Radius a fraction below half of the chord is physically impossible");
}
Assert(testedArcBranches==16,
       "All CW/CCW and minor/major analytical ARC radius scenarios executed");

// Stage 2 B3: independent four-level containment oracle for 3D CSG.
// Alternating external material, void, island and inner void must be
// preserved without flattening the topology into a single outer rectangle.
int verifiedFourLevelScenarios = 0;
foreach (double shift in new[] { 0d, 125d, 1_000d })
foreach (double scale in new[] { 0.5d, 1d, 2d, 5d })
{
    DxfPolyline NestedSquare(string layer, double inset)
    {
        double loX = shift + inset * scale;
        double loY = shift / 2 + inset * scale;
        double hiX = shift + (1000 - inset) * scale;
        double hiY = shift / 2 + (1000 - inset) * scale;
        return Closed(layer, new Point2(loX, loY), new Point2(hiX, loY),
            new Point2(hiX, hiY), new Point2(loX, hiY));
    }
    var nestedDrawing = Document(
        NestedSquare("OUTER", 0),
        NestedSquare("VOID", 100),
        NestedSquare("ISLAND", 200),
        NestedSquare("ISLAND_HOLE", 300));
    var nestedRings = PreviewDxfExporter.ValidateDocument(nestedDrawing);
    var nestedTopology = PreviewContourTopology.Analyze(nestedRings);
    var expectedNestedArea = (1_000_000d - 640_000d +
                              360_000d - 160_000d) * scale * scale;
    Assert(nestedTopology.IsValid && nestedTopology.Rings.Count == 4 &&
           nestedTopology.Rings.Count(r => r.IsMaterialBoundary) == 2 &&
           nestedTopology.Rings.Count(r => !r.IsMaterialBoundary) == 2 &&
           Math.Abs(nestedTopology.NetAreaMm2 - expectedNestedArea) <
               Math.Max(1e-5, expectedNestedArea * 1e-10),
           "Four-level containment matches independent alternating-square area oracle");
    var nestedAssembly = DxfAssembly3DExporter.Export(nestedDrawing, 3);
    Assert(nestedAssembly.MaterialRegionCount == 2 &&
           nestedAssembly.HoleCount == 2 &&
           nestedAssembly.OpenScad.Split("difference()").Length - 1 == 2 &&
           nestedAssembly.OpenScad.Split("linear_extrude(").Length - 1 == 4 &&
           Math.Abs(nestedAssembly.VolumeMm3 - expectedNestedArea * 3) <
               Math.Max(1e-4, expectedNestedArea * 3e-10) &&
           !nestedAssembly.CanStartMachine &&
           !nestedAssembly.IsManufacturingValidated,
           "Four-level DXF CSG assembly preserves nested islands, holes and exact volume");
    var nestedDxf = PreviewDxfExporter.Export(nestedRings);
    var restoredRings = PreviewDxfRoundTripVerifier.Verify(nestedRings, nestedDxf);
    Assert(restoredRings.Count == 4 &&
           Math.Abs(PreviewContourTopology.Analyze(restoredRings).NetAreaMm2 -
                    expectedNestedArea) < Math.Max(1e-5, expectedNestedArea * 1e-10),
           "Nested four-level DXF export/import preserves all analytical ring topology");
    verifiedFourLevelScenarios++;
}
Assert(verifiedFourLevelScenarios == 12,
       "All four-level translated/scaled 3D assembly regression cases ran");

// B3 independent analytic oracle: four-level material hierarchy with
// three concentric circular boundaries. This exercises ARC-derived topology,
// not just the straight-edged nesting covered by the rectangular matrix.
int verifiedCircularNesting = 0;
foreach (var scale in new[] { 0.5d, 1d, 3d })
foreach (var shift in new[] { 0d, 350d, 1500d })
{
    Point2 Shifted(double x, double y) => new(shift + scale*x, shift/2 + scale*y);
    var circularHierarchy = Document(
        Closed("BASE", Shifted(0,0), Shifted(1000,0),
               Shifted(1000,1000), Shifted(0,1000)),
        new DxfCircle("LARGE_VOID", Shifted(500,500), 300*scale),
        new DxfCircle("MATERIAL_ISLAND", Shifted(500,500), 180*scale),
        new DxfCircle("INNER_VOID", Shifted(500,500), 60*scale));
    // Independent elementary geometry: outer square minus the large disc,
    // plus the island disc, minus the innermost disc.
    var expectedCircularArea = scale*scale *
        (1_000_000d - Math.PI*(300d*300 - 180d*180 + 60d*60));
    var circlePaths = PreviewDxfExporter.ValidateDocument(circularHierarchy);
    var circleTopology = PreviewContourTopology.Analyze(circlePaths);
    Assert(circleTopology.IsValid && circleTopology.Rings.Count == 4 &&
           circleTopology.Rings.Count(r=>r.IsMaterialBoundary) == 2 &&
           circleTopology.Rings.Count(r=>!r.IsMaterialBoundary) == 2 &&
           Math.Abs(circleTopology.NetAreaMm2 - expectedCircularArea) <
               Math.Max(1e-5, expectedCircularArea*1e-9),
           "Concentric circle hierarchy matches independent analytic area");
    var circleAssembly = DxfAssembly3DExporter.Export(circularHierarchy, 4);
    Assert(circleAssembly.MaterialRegionCount == 2 &&
           circleAssembly.HoleCount == 2 &&
           circleAssembly.OpenScad.Split("cylinder(h = ").Length-1 == 3 &&
           Math.Abs(circleAssembly.VolumeMm3 - expectedCircularArea*4) <
               Math.Max(1e-4, expectedCircularArea*4e-9) &&
           !circleAssembly.CanStartMachine,
           "Concentric circle hierarchy exports three analytic cylinders and correct volume");
    var circularDxf = PreviewDxfExporter.Export(circlePaths);
    var roundtrippedCircles = PreviewDxfRoundTripVerifier.Verify(
        circlePaths, circularDxf);
    Assert(roundtrippedCircles.Count == 4 &&
           Math.Abs(PreviewContourTopology.Analyze(roundtrippedCircles).NetAreaMm2 -
                    expectedCircularArea) < Math.Max(1e-5, expectedCircularArea*1e-9),
           "Three concentric circular rings survive analytic DXF round-trip");
    verifiedCircularNesting++;
}
Assert(verifiedCircularNesting == 9,
       "All translated and scaled concentric four-level scenarios ran");

// B3 multi-part independent geometry oracle: two disjoint rectangles,
// each with its own circular through-hole, in translated/scaled drawings.
// The outer regions must never be merged or the holes reassigned.
int verifiedMultiPartHoles = 0;
foreach (var scale in new[] { 0.5d, 1d, 2d })
foreach (var shift in new[] { 0d, 300d, 1200d })
{
    Point2 Positioned(double x, double y) =>
        new(shift + x*scale, shift/3 + y*scale);
    var multiPartDrawing = Document(
        Closed("PART_A", Positioned(0,0), Positioned(500,0),
               Positioned(500,500), Positioned(0,500)),
        new DxfCircle("HOLE_A", Positioned(250,250), 50*scale),
        Closed("PART_B", Positioned(1000,100), Positioned(1300,100),
               Positioned(1300,400), Positioned(1000,400)),
        new DxfCircle("HOLE_B", Positioned(1150,250), 30*scale));
    var expectedArea = scale*scale *
        (250000d + 90000d - Math.PI*(2500d + 900d));
    var verifiedPaths = PreviewDxfExporter.ValidateDocument(multiPartDrawing);
    var topology = PreviewContourTopology.Analyze(verifiedPaths);
    Assert(topology.IsValid && topology.Rings.Count == 4 &&
           topology.Rings.Count(r=>r.Depth==0 && r.IsMaterialBoundary)==2 &&
           topology.Rings.Count(r=>r.Depth==1 && !r.IsMaterialBoundary)==2 &&
           Math.Abs(topology.NetAreaMm2-expectedArea) <
               Math.Max(1e-5,expectedArea*1e-9),
           "Two separated plates and circular holes match independent area oracle");
    var assembly = DxfAssembly3DExporter.Export(multiPartDrawing, 6);
    Assert(assembly.MaterialRegionCount==2 && assembly.HoleCount==2 &&
           assembly.OpenScad.Split("difference()").Length-1==2 &&
           assembly.OpenScad.Split("cylinder(h = ").Length-1==2 &&
           Math.Abs(assembly.VolumeMm3-expectedArea*6) <
               Math.Max(1e-4,expectedArea*6e-9) &&
           !assembly.CanStartMachine,
           "Multi-part CSG preserves both material regions and both circular voids");
    var reimported = PreviewDxfRoundTripVerifier.Verify(
        verifiedPaths,PreviewDxfExporter.Export(verifiedPaths));
    var afterRoundTrip = PreviewContourTopology.Analyze(reimported);
    Assert(afterRoundTrip.IsValid && reimported.Count==4 &&
           Math.Abs(afterRoundTrip.NetAreaMm2-expectedArea) <
               Math.Max(1e-5,expectedArea*1e-9),
           "Separated plates with holes preserve exact analytic DXF round-trip");
    verifiedMultiPartHoles++;
}
Assert(verifiedMultiPartHoles==9,
       "All translated and scaled independent multi-part hole scenarios ran");

// B2: three complete rigid-edit primitives for verified CAD contours:
// translate, rotate, and vertical reflection. Preserve exact circular arcs,
// two-dimensional nesting and analytical area throughout DXF round-trips.
var rigidSource = Document(
    Closed("RIGID_OUTER",new Point2(0,0),new Point2(300,0),
           new Point2(300,200),new Point2(0,200)),
    new DxfCircle("RIGID_HOLE",new Point2(100,100),30));
var rigidPaths = PreviewDxfExporter.ValidateDocument(rigidSource);
var rigidArea = 60000d - Math.PI * 900;
var rigidInitial = PreviewContourTopology.Analyze(rigidPaths);
Assert(rigidInitial.IsValid && rigidPaths.Count==2 &&
       Math.Abs(rigidInitial.NetAreaMm2-rigidArea)<1e-6,
       "Rigid editor fixture is verified nested LINE/ARC geometry");

var rigidTranslated = PreviewRigidTransformEditor.Translate(rigidPaths,70,80);
var translatedTopo = PreviewContourTopology.Analyze(rigidTranslated.Rings);
Assert(rigidTranslated.IsValidForPreview && !rigidTranslated.CanStartMachine &&
       translatedTopo.IsValid && translatedTopo.Rings.Count==2 &&
       Math.Abs(rigidTranslated.NetAreaMm2-rigidArea)<1e-6 &&
       rigidTranslated.Rings.SelectMany(p=>p.Segments)
           .OfType<PreviewArc>().Any(a=>Math.Abs(a.Center.X-170)<1e-7 &&
                                          Math.Abs(a.Center.Y-180)<1e-7),
       "Translate moves hole and material as one rigid CAD assembly");
PreviewDxfRoundTripVerifier.Verify(rigidTranslated.Rings,
    PreviewDxfExporter.Export(rigidTranslated.Rings));

var rigidRotated = PreviewRigidTransformEditor.Rotate(rigidPaths,new(0,0),90);
var rotatedTopo = PreviewContourTopology.Analyze(rigidRotated.Rings);
Assert(rigidRotated.IsValidForPreview && rotatedTopo.IsValid &&
       Math.Abs(rotatedTopo.NetAreaMm2-rigidArea)<1e-6 &&
       rigidRotated.Rings.SelectMany(p=>p.Segments)
           .OfType<PreviewArc>().Any(a=>Math.Abs(a.Center.X+100)<1e-7 &&
                                          Math.Abs(a.Center.Y-100)<1e-7),
       "Rotate turns material and circular hole around a fixed pivot");
PreviewDxfRoundTripVerifier.Verify(rigidRotated.Rings,
    PreviewDxfExporter.Export(rigidRotated.Rings));

// B2: a selected, non-origin pivot must stay fixed while LINE/ARC
// boundaries rotate rigidly around it; inverse restores exact topology.
var selectedPivot = new PreviewPoint(100,100);
var pivotRotated = PreviewRigidTransformEditor.Rotate(
    rigidPaths,selectedPivot,90);
var pivotTopology = PreviewContourTopology.Analyze(pivotRotated.Rings);
Assert(pivotTopology.IsValid && pivotTopology.Rings.Count==2 &&
       pivotRotated.Rings.SelectMany(p=>p.Segments)
           .OfType<PreviewArc>().All(a=>
               Math.Abs(a.Center.X-selectedPivot.X)<1e-7 &&
               Math.Abs(a.Center.Y-selectedPivot.Y)<1e-7) &&
       pivotRotated.Rings.SelectMany(p=>p.Segments)
           .OfType<PreviewLine>().Any(l=>
               Math.Abs(l.Start.X-200)<1e-7 &&
               Math.Abs(l.Start.Y-0)<1e-7) &&
       Math.Abs(pivotTopology.NetAreaMm2-rigidArea)<1e-6 &&
       !pivotRotated.CanStartMachine,
       "Non-origin rotation pivot remains fixed and material/hole geometry is preserved");
var pivotRestored = PreviewRigidTransformEditor.Rotate(
    pivotRotated.Rings,selectedPivot,-90);
var pivotReopened = PreviewDxfRoundTripVerifier.Verify(
    pivotRestored.Rings,PreviewDxfExporter.Export(pivotRestored.Rings));
Assert(PreviewContourTopology.Analyze(pivotReopened).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(pivotReopened).NetAreaMm2-rigidArea)<1e-6 &&
       pivotReopened.Count==rigidPaths.Count,
       "Non-origin pivot rotation and inverse survive exact DXF round-trip");
bool badPivotRejected = false;
try
{
    PreviewRigidTransformEditor.Rotate(
        rigidPaths,new PreviewPoint(double.NaN,0),90);
}
catch(FormatException) { badPivotRejected = true; }
Assert(badPivotRejected,
       "Invalid rotation pivot coordinates must fail without changing the DXF");

var rigidMirrored = PreviewRigidTransformEditor.MirrorVertical(rigidPaths,150);
var mirrorTopo = PreviewContourTopology.Analyze(rigidMirrored.Rings);
Assert(rigidMirrored.IsValidForPreview && mirrorTopo.IsValid &&
       Math.Abs(mirrorTopo.NetAreaMm2-rigidArea)<1e-6 &&
       rigidMirrored.Rings.SelectMany(p=>p.Segments)
           .OfType<PreviewArc>().Any(a=>Math.Abs(a.Center.X-200)<1e-7 &&
                                          Math.Abs(a.Center.Y-100)<1e-7 &&
                                          a.SweepDegrees<0),
       "Reflect preserves hole ownership and reverses ARC handedness");
PreviewDxfRoundTripVerifier.Verify(rigidMirrored.Rings,
    PreviewDxfExporter.Export(rigidMirrored.Rings));

// B2: uniform scaling of a plate with circular hole preserves precise
// ARC curvature and hole ownership, changing net area by factor squared.
var scaledDxf=PreviewRigidTransformEditor.ScaleUniform(
    rigidPaths,new PreviewPoint(100,100),2);
var scaledTopology=PreviewContourTopology.Analyze(scaledDxf.Rings);
Assert(scaledDxf.IsValidForPreview && !scaledDxf.CanStartMachine &&
       scaledTopology.IsValid && scaledTopology.Rings.Count==2 &&
       Math.Abs(scaledTopology.NetAreaMm2-rigidArea*4)<1e-5 &&
       scaledDxf.Rings.SelectMany(p=>p.Segments).OfType<PreviewArc>()
           .Any(a=>Math.Abs(a.Center.X-100)<1e-7 &&
                   Math.Abs(a.Center.Y-100)<1e-7 &&
                   Math.Abs(a.RadiusMm-60)<1e-7),
       "Uniform DXF scaling doubles hole radius and quadruples net area");
var restoredScale=PreviewRigidTransformEditor.ScaleUniform(
    scaledDxf.Rings,new PreviewPoint(100,100),0.5);
var scaleRoundTrip=PreviewDxfRoundTripVerifier.Verify(
    restoredScale.Rings,PreviewDxfExporter.Export(restoredScale.Rings));
var restoredScaleTopology=PreviewContourTopology.Analyze(scaleRoundTrip);
Assert(restoredScaleTopology.IsValid &&
       scaleRoundTrip.Count==rigidPaths.Count &&
       Math.Abs(restoredScaleTopology.NetAreaMm2-rigidArea)<1e-6 &&
       scaleRoundTrip.SelectMany(p=>p.Segments).OfType<PreviewArc>()
           .Any(a=>Math.Abs(a.RadiusMm-30)<1e-7),
       "Inverse uniform scaling restores analytic hole radius and DXF topology");
foreach(var invalidFactor in new[]{
    double.NaN,double.PositiveInfinity,0,-1,0.001,101
})
{
    bool invalidScaleRejected=false;
    try
    {
        PreviewRigidTransformEditor.ScaleUniform(
            rigidPaths,new PreviewPoint(100,100),invalidFactor);
    }
    catch(FormatException) { invalidScaleRejected=true; }
    Assert(invalidScaleRejected,
           "Invalid uniform scale factor must fail closed");
}
bool invalidScalePivotRejected=false;
try
{
    PreviewRigidTransformEditor.ScaleUniform(
        rigidPaths,new PreviewPoint(double.NaN,0),2);
}
catch(FormatException) { invalidScalePivotRejected=true; }
Assert(invalidScalePivotRejected,
       "Nonfinite uniform scale pivot must fail closed");

// B2: horizontal reflection around arbitrary Y must preserve exact arcs,
// circular-hole ownership and net area. Two reflections are an identity.
var horizontalMirrored=PreviewRigidTransformEditor.MirrorHorizontal(rigidPaths,125);
var horizontalTopology=PreviewContourTopology.Analyze(horizontalMirrored.Rings);
Assert(horizontalMirrored.IsValidForPreview && !horizontalMirrored.CanStartMachine &&
       horizontalTopology.IsValid && horizontalTopology.Rings.Count==2 &&
       Math.Abs(horizontalTopology.NetAreaMm2-rigidArea)<1e-6 &&
       horizontalMirrored.Rings.SelectMany(p=>p.Segments)
           .OfType<PreviewArc>().Any(a=>Math.Abs(a.Center.X-100)<1e-7 &&
                                          Math.Abs(a.Center.Y-150)<1e-7 &&
                                          a.SweepDegrees<0),
       "Horizontal mirror moves circular hole across arbitrary Y and reverses ARC direction");
var twiceHorizontal=PreviewRigidTransformEditor.MirrorHorizontal(
    horizontalMirrored.Rings,125);
var horizontalSerialized=PreviewDxfExporter.Export(twiceHorizontal.Rings);
var horizontalReopened=PreviewDxfRoundTripVerifier.Verify(
    twiceHorizontal.Rings,horizontalSerialized);
var horizontalRestored=PreviewContourTopology.Analyze(horizontalReopened);
Assert(horizontalRestored.IsValid && horizontalReopened.Count==rigidPaths.Count &&
       Math.Abs(horizontalRestored.NetAreaMm2-rigidArea)<1e-6 &&
       horizontalReopened.SelectMany(p=>p.Segments)
           .OfType<PreviewArc>().Any(a=>Math.Abs(a.Center.X-100)<1e-7 &&
                                          Math.Abs(a.Center.Y-100)<1e-7),
       "Double horizontal reflection and DXF round-trip restore original hole and area");
bool invalidHorizontalAxisRejected=false;
try { PreviewRigidTransformEditor.MirrorHorizontal(rigidPaths,double.NaN); }
catch(FormatException) { invalidHorizontalAxisRejected=true; }
Assert(invalidHorizontalAxisRejected,
       "Nonfinite horizontal mirror axis is rejected before any editing");

static bool RejectRigidEdit(Func<PreviewVertexEditResult> edit)
{
    try { edit(); return false; }
    catch (FormatException) { return true; }
}
Assert(RejectRigidEdit(()=>PreviewRigidTransformEditor.Translate(
           rigidPaths,double.NaN,0)) &&
       RejectRigidEdit(()=>PreviewRigidTransformEditor.Translate(
           rigidPaths,1_100_000,0)) &&
       RejectRigidEdit(()=>PreviewRigidTransformEditor.Rotate(
           rigidPaths,new(0,0),double.PositiveInfinity)) &&
       RejectRigidEdit(()=>PreviewRigidTransformEditor.MirrorVertical(
           rigidPaths,double.NaN)),
       "Invalid rigid transform input is rejected before geometry can be exported");
// Compose all three operations, then undo them with inverse rigid transforms.
var rigidComposite = PreviewRigidTransformEditor.MirrorVertical(
    PreviewRigidTransformEditor.Rotate(
        PreviewRigidTransformEditor.Translate(rigidPaths,40,25).Rings,
        new(0,0),90).Rings,100);
var inverseMirror = PreviewRigidTransformEditor.MirrorVertical(
    rigidComposite.Rings,100);
var inverseRotate = PreviewRigidTransformEditor.Rotate(
    inverseMirror.Rings,new(0,0),-90);
var inverseTranslate = PreviewRigidTransformEditor.Translate(
    inverseRotate.Rings,-40,-25);
var restoredRigidTopology=PreviewContourTopology.Analyze(inverseTranslate.Rings);
Assert(restoredRigidTopology.IsValid &&
       Math.Abs(restoredRigidTopology.NetAreaMm2-rigidArea)<1e-6 &&
       inverseTranslate.Rings.Count==rigidPaths.Count,
       "Combined translate/rotate/mirror and their inverse preserve verified topology");

// Stage 3 research-backed, strictly non-executable plasma profile checks.
// All values here are synthetic regression fixtures, NOT cutting settings.
var syntheticPlasmaProfile = new PlasmaProcessProfile(
    Material: "SYNTHETIC TEST MATERIAL", ThicknessMm: 4,
    PowerSourceModel: "TEST SOURCE", ControllerModel: "TEST CONTROLLER",
    CutChartReference: "SYNTHETIC TEST CHART ONLY",
    CurrentAmps: 20, FeedRateMmPerMinute: 1000, KerfWidthMm: 1,
    PierceDelaySeconds: 0.5, PierceHeightMm: 3, CutHeightMm: 1,
    OperatorConfirmed: true, RequiresArcOk: true, ArcOkSupported: true,
    ThcRequested: false, ThcSupported: false, ZMotionSupported: true);
Assert(syntheticPlasmaProfile.ValidateForPlanning().Count == 0 &&
       !syntheticPlasmaProfile.CanStartMachine,
       "Structurally coherent synthetic plasma profile remains non-executable");
Assert(syntheticPlasmaProfile with { PierceDelaySeconds = 0 }
       is { CanStartMachine: false },
       "Zero delay is a representable documented value, never machine approval");
Assert((syntheticPlasmaProfile with { PierceDelaySeconds = -1 })
           .ValidateForPlanning().Any(x=>x.Contains("Pierce delay")) &&
       (syntheticPlasmaProfile with { PierceDelaySeconds = double.NaN })
           .ValidateForPlanning().Any(x=>x.Contains("Pierce delay")) &&
       (syntheticPlasmaProfile with { PierceDelaySeconds = double.PositiveInfinity })
           .ValidateForPlanning().Any(x=>x.Contains("Pierce delay")),
       "Negative and nonfinite pierce timing must fail planning checks");
Assert((syntheticPlasmaProfile with { PierceHeightMm = 0.5 })
           .ValidateForPlanning().Any(x=>x.Contains("exceed cut height")) &&
       (syntheticPlasmaProfile with { ZMotionSupported = false })
           .ValidateForPlanning().Any(x=>x.Contains("Z motion")),
       "Unsupported Z configuration or inverted pierce/cut heights fail closed");
Assert((syntheticPlasmaProfile with { RequiresArcOk = true, ArcOkSupported = false })
           .ValidateForPlanning().Any(x=>x.Contains("Arc OK")) &&
       (syntheticPlasmaProfile with { ThcRequested = true, ThcSupported = false })
           .ValidateForPlanning().Any(x=>x.Contains("THC")),
       "Requested hardware feedback cannot be assumed present");
Assert((syntheticPlasmaProfile with { CutChartReference = "" })
           .ValidateForPlanning().Any(x=>x.Contains("cut-chart")) &&
       (syntheticPlasmaProfile with { OperatorConfirmed = false })
           .ValidateForPlanning().Any(x=>x.Contains("Operator")) &&
       (syntheticPlasmaProfile with { KerfWidthMm = -0.01 })
           .ValidateForPlanning().Any(x=>x.Contains("Kerf")),
       "Unreferenced, unconfirmed and invalid profiles cannot pass planning validation");

// Stage 3 isolated, virtual-time plasma pierce rehearsal.
// No G-code, no real waiting and no controller or torch output.
var pierceReady = PlasmaPierceSimulator.Rehearse(
    syntheticPlasmaProfile,
    new PlasmaPierceSimulator.Inputs(ArcOkAtSeconds: 0.2));
Assert(pierceReady.Completed && pierceReady.Failure is null &&
       !pierceReady.CanStartMachine && !pierceReady.ProducesGCode &&
       string.Join("|",pierceReady.Events.Select(x=>x.Stage)) ==
           "PiercePositioned|ArcRequested|ArcConfirmed|PierceDelayCompleted|CutPhaseReadyForSimulation" &&
       Math.Abs(pierceReady.Events[^1].VirtualSeconds-0.7)<1e-10,
       "Verified synthetic Arc OK enables the virtual pierce delay, not hardware motion");
var pierceNoArc = PlasmaPierceSimulator.Rehearse(
    syntheticPlasmaProfile,
    new PlasmaPierceSimulator.Inputs(ArcOkAtSeconds:double.PositiveInfinity));
Assert(!pierceNoArc.Completed && pierceNoArc.Failure!.Contains("timeout") &&
       pierceNoArc.Events[^1].Stage=="Fault" &&
       !pierceNoArc.Events.Any(x=>x.Stage=="CutPhaseReadyForSimulation"),
       "Arc OK timeout must never enter the simulated cut phase");
var pierceLostArc = PlasmaPierceSimulator.Rehearse(
    syntheticPlasmaProfile,
    new PlasmaPierceSimulator.Inputs(ArcOkAtSeconds:0.2,ArcLostAtSeconds:0.4));
Assert(!pierceLostArc.Completed &&
       pierceLostArc.Failure!.Contains("lost during pierce delay") &&
       !pierceLostArc.Events.Any(x=>x.Stage=="CutPhaseReadyForSimulation"),
       "Arc OK loss during pierce delay aborts the virtual sequence");
foreach(var inputs in new[]{
    new PlasmaPierceSimulator.Inputs(0.2,Connected:false),
    new PlasmaPierceSimulator.Inputs(0.2,EmergencyStop:true),
    new PlasmaPierceSimulator.Inputs(0.2,Alarm:true),
    new PlasmaPierceSimulator.Inputs(0.2,ArcOkTimeoutSeconds:double.NaN),
    new PlasmaPierceSimulator.Inputs(-1)
})
{
    var faulted = PlasmaPierceSimulator.Rehearse(syntheticPlasmaProfile,inputs);
    Assert(!faulted.Completed && faulted.Events[^1].Stage=="Fault" &&
           !faulted.CanStartMachine && !faulted.ProducesGCode,
           "Connection, stop, alarm, malformed timing and invalid arc timing fail closed");
}
var pierceOpenLoop = PlasmaPierceSimulator.Rehearse(
    syntheticPlasmaProfile with {RequiresArcOk=false},
    new PlasmaPierceSimulator.Inputs(0.2));
Assert(!pierceOpenLoop.Completed &&
       pierceOpenLoop.Failure!.Contains("mandatory"),
       "Open-loop plasma simulation is not implicitly enabled");
var zeroPierce = PlasmaPierceSimulator.Rehearse(
    syntheticPlasmaProfile with { PierceDelaySeconds=0 },
    new PlasmaPierceSimulator.Inputs(0.15));
Assert(zeroPierce.Completed &&
       Math.Abs(zeroPierce.Events[^1].VirtualSeconds-0.15)<1e-10 &&
       !zeroPierce.CanStartMachine,
       "Zero virtual delay is represented without enabling machinery");

// Stage 3 multi-contour virtual pierce rehearsal: all episodes have their
// own local virtual time. No actual cutting time, travel, G-code or tool IO.
var multiPierceAttempts = new[]
{
    new PlasmaPierceBatchSimulator.Attempt("INNER-HOLE-1",
        new PlasmaPierceSimulator.Inputs(ArcOkAtSeconds:0.1)),
    new PlasmaPierceBatchSimulator.Attempt("INNER-HOLE-2",
        new PlasmaPierceSimulator.Inputs(ArcOkAtSeconds:0.2)),
    new PlasmaPierceBatchSimulator.Attempt("OUTER-BOUNDARY",
        new PlasmaPierceSimulator.Inputs(ArcOkAtSeconds:0.3))
};
var multiPiercePass = PlasmaPierceBatchSimulator.Rehearse(
    syntheticPlasmaProfile,multiPierceAttempts);
Assert(multiPiercePass.Completed && multiPiercePass.Failure is null &&
       multiPiercePass.Outcomes.Count==3 &&
       multiPiercePass.Outcomes.Select(x=>x.ContourId).SequenceEqual(
           new[]{"INNER-HOLE-1","INNER-HOLE-2","OUTER-BOUNDARY"}) &&
       multiPiercePass.Outcomes.All(x=>x.Result.Completed) &&
       !multiPiercePass.CanStartMachine &&
       !multiPiercePass.ProducesGCode &&
       !multiPiercePass.AllowsAutomaticResume,
       "Three ordered pierce rehearsals complete as separate virtual episodes only");
var multiPierceFailure = PlasmaPierceBatchSimulator.Rehearse(
    syntheticPlasmaProfile,
    new[]{
        multiPierceAttempts[0],
        new PlasmaPierceBatchSimulator.Attempt("INNER-HOLE-2",
            new PlasmaPierceSimulator.Inputs(double.PositiveInfinity)),
        multiPierceAttempts[2]
    });
Assert(!multiPierceFailure.Completed &&
       multiPierceFailure.Outcomes.Count==2 &&
       multiPierceFailure.Outcomes[0].Result.Completed &&
       !multiPierceFailure.Outcomes[1].Result.Completed &&
       multiPierceFailure.Failure!.Contains("INNER-HOLE-2") &&
       !multiPierceFailure.Outcomes.Any(x=>x.ContourId=="OUTER-BOUNDARY") &&
       !multiPierceFailure.AllowsAutomaticResume,
       "Second-contour arc timeout halts the whole virtual job before exterior");
var multiPierceLostArc = PlasmaPierceBatchSimulator.Rehearse(
    syntheticPlasmaProfile,
    new[]{
        multiPierceAttempts[0],
        new PlasmaPierceBatchSimulator.Attempt("INNER-HOLE-2",
            new PlasmaPierceSimulator.Inputs(0.1,ArcLostAtSeconds:0.4)),
        multiPierceAttempts[2]
    });
Assert(!multiPierceLostArc.Completed &&
       multiPierceLostArc.Outcomes.Count==2 &&
       multiPierceLostArc.Failure!.Contains("lost during pierce delay"),
       "Arc loss during second pierce forbids continuing the virtual sequence");
var multiPierceDuplicate = PlasmaPierceBatchSimulator.Rehearse(
    syntheticPlasmaProfile,
    new[]{multiPierceAttempts[0],multiPierceAttempts[0]});
Assert(!multiPierceDuplicate.Completed &&
       multiPierceDuplicate.Outcomes.Count==0 &&
       multiPierceDuplicate.Failure!.Contains("Duplicate"),
       "Duplicate contour names are rejected before simulation begins");
Assert(!PlasmaPierceBatchSimulator.Rehearse(
           syntheticPlasmaProfile,
           Array.Empty<PlasmaPierceBatchSimulator.Attempt>()).Completed &&
       !PlasmaPierceBatchSimulator.Rehearse(
           syntheticPlasmaProfile,
           Enumerable.Range(0,101).Select(i=>new PlasmaPierceBatchSimulator.Attempt(
               $"PART-{i}",new PlasmaPierceSimulator.Inputs(0.1))).ToArray()).Completed,
       "Virtual job rejects empty and unbounded pierce attempt counts");
var multiPierceInvalidProfile=PlasmaPierceBatchSimulator.Rehearse(
    syntheticPlasmaProfile with {OperatorConfirmed=false},multiPierceAttempts);
Assert(!multiPierceInvalidProfile.Completed &&
       multiPierceInvalidProfile.Outcomes.Count==1 &&
       !multiPierceInvalidProfile.AllowsAutomaticResume,
       "Unconfirmed process profile cannot advance to subsequent contour attempts");

// B2: a verified line-only plate can receive a symmetric 5mm corner
// chamfer without moving or losing a circular internal hole.
var chamferSource=Document(
    Closed("CHAMFER_PLATE",new Point2(0,0),new Point2(100,0),
        new Point2(100,80),new Point2(0,80)),
    new DxfCircle("CHAMFER_HOLE",new Point2(50,40),10));
var chamferRings=PreviewDxfExporter.ValidateDocument(chamferSource);
var chamferOriginal=PreviewContourTopology.Analyze(chamferRings);
var chamfered=PreviewChamferEditor.ChamferVertex(chamferRings,0,0,5);
var chamferTopology=PreviewContourTopology.Analyze(chamfered.Rings);
Assert(chamfered.IsValidForPreview && !chamfered.CanStartMachine &&
       chamferTopology.IsValid && chamferTopology.Rings.Count==2 &&
       chamfered.Rings[0].Segments.Count==5 &&
       chamfered.Rings[1].Segments.Count==chamferRings[1].Segments.Count &&
       Math.Abs(chamferTopology.NetAreaMm2-
           (chamferOriginal.NetAreaMm2-12.5))<1e-6,
       "A 5mm 90-degree chamfer cuts 12.5mm2 while preserving circular hole");
var chamferReopened=PreviewDxfRoundTripVerifier.Verify(
    chamfered.Rings,PreviewDxfExporter.Export(chamfered.Rings));
Assert(PreviewContourTopology.Analyze(chamferReopened).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(chamferReopened)
                    .NetAreaMm2-chamferTopology.NetAreaMm2)<1e-6,
       "Chamfered geometry passes exact DXF round trip");
foreach (var setback in new[]{-1d,0d,0.001,80d,100d,double.NaN,
                               double.PositiveInfinity})
{
    bool refused=false;
    try { PreviewChamferEditor.ChamferVertex(chamferRings,0,0,setback); }
    catch(FormatException) { refused=true; }
    Assert(refused,"Impossible chamfer setback must fail closed");
}
bool unsupportedChamferArc=false;
try { PreviewChamferEditor.ChamferVertex(chamferRings,1,0,5); }
catch(FormatException) { unsupportedChamferArc=true; }
Assert(unsupportedChamferArc,
       "Chamfer refuses circular ARC contours rather than flattening them");
Assert(chamferRings[0].Segments.Count==4 &&
       Math.Abs(PreviewContourTopology.Analyze(chamferRings).NetAreaMm2-
                chamferOriginal.NetAreaMm2)<1e-6,
       "Chamfer does not mutate original preview: Undo history can retain it");

// Stage 2 B2: exact tangent ARC fillet at the (0,0) convex corner of a
// 100x80 plate with a circular hole. 10mm fillet reduces material area by
// R^2*(1 - pi/4) and must preserve the complete inner hole.
var filletSource=Document(
    Closed("FILLET_PLATE",new Point2(0,0),new Point2(100,0),
        new Point2(100,80),new Point2(0,80)),
    new DxfCircle("FILLET_HOLE",new Point2(50,40),10));
var filletRings=PreviewDxfExporter.ValidateDocument(filletSource);
var filletBefore=PreviewContourTopology.Analyze(filletRings);
var filleted=PreviewCornerFilletEditor.FilletVertex(filletRings,0,0,10);
var filletAfter=PreviewContourTopology.Analyze(filleted.Rings);
var filletArcs=filleted.Rings[0].Segments.OfType<PreviewArc>().ToArray();
Assert(filleted.IsValidForPreview && !filleted.CanStartMachine &&
       filletAfter.IsValid && filletAfter.Rings.Count==2 &&
       filleted.Rings[0].Segments.Count==5 &&
       filletArcs.Length==1 &&
       Math.Abs(filletArcs[0].RadiusMm-10)<1e-7 &&
       Math.Abs(filletArcs[0].Center.X-10)<1e-7 &&
       Math.Abs(filletArcs[0].Center.Y-10)<1e-7 &&
       Math.Abs(filletArcs[0].SweepDegrees-90)<1e-7 &&
       Math.Abs(filletAfter.NetAreaMm2-
           (filletBefore.NetAreaMm2-100*(1-Math.PI/4)))<1e-5 &&
       filleted.Rings[1].Segments.Count==filletRings[1].Segments.Count,
       "Fillet builds true tangent quarter-circle, analytically changes area and keeps hole");
var filletDxf=PreviewDxfExporter.Export(filleted.Rings);
var filletReopened=PreviewDxfRoundTripVerifier.Verify(filleted.Rings,filletDxf);
Assert(PreviewContourTopology.Analyze(filletReopened).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(filletReopened).NetAreaMm2-
                filletAfter.NetAreaMm2)<1e-5,
       "Fillet retains exact analytic ARC and holes across DXF export and reimport");
foreach(var r in new[]{double.NaN,double.PositiveInfinity,-1d,0d,0.001d,80d,100d})
{
    bool rejected=false;
    try { PreviewCornerFilletEditor.FilletVertex(filletRings,0,0,r); }
    catch(FormatException) { rejected=true; }
    Assert(rejected,"Nonfinite or non-fitting fillet radius must fail closed");
}
bool filletOnCircleRejected=false;
try { PreviewCornerFilletEditor.FilletVertex(filletRings,1,0,10); }
catch(FormatException) { filletOnCircleRejected=true; }
Assert(filletOnCircleRejected,"Fillet refuses ARC rings rather than flattening curves");
Assert(filletRings[0].Segments.Count==4 &&
       Math.Abs(PreviewContourTopology.Analyze(filletRings).NetAreaMm2-
                filletBefore.NetAreaMm2)<1e-6,
       "Fillet leaves source DXF unchanged for Undo and failed operations");

// B2 concave fillet: U-shaped notch in a CCW plate. The tangent ARC at
// the inside notch corner is clockwise, adds R^2(1-pi/4) to material,
// and must neither move nor consume an unrelated round hole.
var notchSource=Document(
    Closed("NOTCH_PLATE",new Point2(0,0),new Point2(100,0),
        new Point2(100,100),new Point2(60,100),new Point2(60,40),
        new Point2(40,40),new Point2(40,100),new Point2(0,100)),
    new DxfCircle("NOTCH_HOLE",new Point2(20,20),5));
var notchPaths=PreviewDxfExporter.ValidateDocument(notchSource);
var notchBefore=PreviewContourTopology.Analyze(notchPaths);
Assert(notchBefore.IsValid && notchPaths.Count==2,
       "U-notch and independent circular hole form valid source topology");
var notchFillet=PreviewCornerFilletEditor.FilletVertex(notchPaths,0,4,10);
var notchAfter=PreviewContourTopology.Analyze(notchFillet.Rings);
var notchArc=notchFillet.Rings[0].Segments.OfType<PreviewArc>().Single();
Assert(notchFillet.IsValidForPreview && !notchFillet.CanStartMachine &&
       notchAfter.IsValid && notchAfter.Rings.Count==2 &&
       notchFillet.Rings[0].Segments.Count==9 &&
       Math.Abs(notchArc.Center.X-50)<1e-7 &&
       Math.Abs(notchArc.Center.Y-50)<1e-7 &&
       Math.Abs(notchArc.RadiusMm-10)<1e-7 &&
       Math.Abs(notchArc.SweepDegrees+90)<1e-7 &&
       Math.Abs(notchAfter.NetAreaMm2-
            (notchBefore.NetAreaMm2+100*(1-Math.PI/4)))<1e-5 &&
       notchFillet.Rings[1].Segments.Count==notchPaths[1].Segments.Count,
       "Concave inside corner becomes clockwise quarter-ARC and increases area");
var notchRoundtrip=PreviewDxfRoundTripVerifier.Verify(
    notchFillet.Rings,PreviewDxfExporter.Export(notchFillet.Rings));
Assert(PreviewContourTopology.Analyze(notchRoundtrip).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(notchRoundtrip).NetAreaMm2-
           notchAfter.NetAreaMm2)<1e-5,
       "Concave fillet survives exact DXF round-trip");
bool tooLargeNotchFilletRejected=false;
try { PreviewCornerFilletEditor.FilletVertex(notchPaths,0,4,21); }
catch(FormatException) { tooLargeNotchFilletRejected=true; }
Assert(tooLargeNotchFilletRejected,
       "Concave fillet exceeding the notch edge is rejected");
Assert(notchPaths[0].Segments.Count==8 &&
       Math.Abs(PreviewContourTopology.Analyze(notchPaths).NetAreaMm2-
           notchBefore.NetAreaMm2)<1e-6,
       "Concave edit never mutates original DXF source");

// B2: a second fillet can now be applied to an already curved LINE/ARC
// exterior provided its selected corner joins two LINE segments. The
// previous exact ARC must survive unchanged and the circular hole stay put.
var successiveFillet=PreviewCornerFilletEditor.FilletVertex(
    filleted.Rings,0,1,5);
var successiveTopo=PreviewContourTopology.Analyze(successiveFillet.Rings);
var successiveArcs=successiveFillet.Rings[0].Segments.OfType<PreviewArc>().ToArray();
Assert(successiveFillet.IsValidForPreview && !successiveFillet.CanStartMachine &&
       successiveTopo.IsValid &&
       successiveFillet.Rings[0].Segments.Count==6 &&
       successiveArcs.Length==2 &&
       successiveArcs.Any(a=>Math.Abs(a.Center.X-10)<1e-7 &&
                             Math.Abs(a.Center.Y-10)<1e-7 &&
                             Math.Abs(a.RadiusMm-10)<1e-7) &&
       successiveArcs.Any(a=>Math.Abs(a.Center.X-95)<1e-7 &&
                             Math.Abs(a.Center.Y-5)<1e-7 &&
                             Math.Abs(a.RadiusMm-5)<1e-7) &&
       Math.Abs(successiveTopo.NetAreaMm2-
           (filletAfter.NetAreaMm2-25*(1-Math.PI/4)))<1e-5 &&
       successiveFillet.Rings[1].Segments.Count==filleted.Rings[1].Segments.Count,
       "Fillet of a LINE corner in mixed LINE/ARC ring preserves existing arc and hole");
var successiveDxf=PreviewDxfExporter.Export(successiveFillet.Rings);
var successiveReopened=PreviewDxfRoundTripVerifier.Verify(
    successiveFillet.Rings,successiveDxf);
Assert(PreviewContourTopology.Analyze(successiveReopened).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(successiveReopened).NetAreaMm2-
           successiveTopo.NetAreaMm2)<1e-5,
       "Second fillet survives DXF export and import with two exact exterior arcs");
bool adjacentArcRefused=false;
try { PreviewCornerFilletEditor.FilletVertex(filleted.Rings,0,0,5); }
catch(FormatException) { adjacentArcRefused=true; }
Assert(adjacentArcRefused,
       "Fillet at an ARC/LINE junction is safely refused, not approximated");
Assert(filleted.Rings[0].Segments.Count==5 &&
       filleted.Rings[0].Segments.OfType<PreviewArc>().Count()==1,
       "Second fillet cannot mutate original DXF history snapshot");

// B2: exact tangent addition at a LINE -> clockwise ARC junction.
// All geometry is synthetic drafting-only and remains non-executable.
var lineArcBase=new PreviewPath(new PreviewSegment[]{
    new PreviewLine(new PreviewPoint(0,0),new PreviewPoint(100,0)),
    new PreviewArc(new PreviewPoint(120,0),20,180,-90),
    new PreviewLine(new PreviewPoint(120,20),new PreviewPoint(120,100)),
    new PreviewLine(new PreviewPoint(120,100),new PreviewPoint(0,100)),
    new PreviewLine(new PreviewPoint(0,100),new PreviewPoint(0,0))
},true);
var lineArcRings=new PreviewPath[]{lineArcBase,filletRings[1]};
var lineArcBefore=PreviewContourTopology.Analyze(lineArcRings);
Assert(lineArcBefore.IsValid,"LINE/ARC synthetic source with a hole is valid");
var tangentFillet=PreviewLineArcFilletEditor.FilletIncomingLine(
    lineArcRings,0,1,5);
var tangentTopology=PreviewContourTopology.Analyze(tangentFillet.Rings);
var tangentArcs=tangentFillet.Rings[0].Segments.OfType<PreviewArc>().ToArray();
Assert(tangentFillet.IsValidForPreview && !tangentFillet.CanStartMachine &&
       tangentTopology.IsValid && tangentFillet.Rings[0].Segments.Count==6 &&
       tangentArcs.Length==2 &&
       tangentArcs.Any(a=>Math.Abs(a.RadiusMm-5)<1e-6) &&
       tangentArcs.Any(a=>Math.Abs(a.RadiusMm-20)<1e-6) &&
       tangentFillet.Rings[1].Segments.Count==lineArcRings[1].Segments.Count,
       "Incoming LINE to clockwise ARC fillet retains source analytic ARC and hole");
var tangentRoundTrip=PreviewDxfRoundTripVerifier.Verify(
    tangentFillet.Rings,PreviewDxfExporter.Export(tangentFillet.Rings));
Assert(PreviewContourTopology.Analyze(tangentRoundTrip).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(tangentRoundTrip).NetAreaMm2-
           tangentTopology.NetAreaMm2)<1e-5,
       "New tangent LINE/ARC fillet passes exact DXF round-trip");
foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,0d,-1d,1_000_001d})
{
    bool rejected=false;
    try { PreviewLineArcFilletEditor.FilletIncomingLine(lineArcRings,0,1,invalid); }
    catch(FormatException){ rejected=true; }
    Assert(rejected,"Invalid LINE/ARC fillet radius fails before geometry changes");
}
bool wrongArcIndexRejected=false;
try { PreviewLineArcFilletEditor.FilletIncomingLine(lineArcRings,0,2,5); }
catch(FormatException){ wrongArcIndexRejected=true; }
Assert(wrongArcIndexRejected,"LINE/ARC fillet refuses unsupported segment selection");
Assert(lineArcBase.Segments.Count==5,
       "LINE/ARC fillet preserves source preview for Undo");

// B2 ARC -> LINE is the exact reverse of an existing LINE -> ARC
// solution. Preserve arc geometry, ring topology and source for Undo.
PreviewSegment ReverseArcLineTestSegment(PreviewSegment segment) => segment switch
{
    PreviewLine line => new PreviewLine(line.End,line.Start),
    PreviewArc arc => new PreviewArc(arc.Center,arc.RadiusMm,
                                   arc.StartDegrees+arc.SweepDegrees,-arc.SweepDegrees),
    _ => throw new FormatException("Unsupported test segment.")
};
PreviewPath ReverseArcLineTestRing(PreviewPath path) =>
    new(path.Segments.Reverse().Select(ReverseArcLineTestSegment).ToArray(),true);
var reversedLineArcBase=ReverseArcLineTestRing(lineArcBase);
var arcLineRings=new PreviewPath[]{reversedLineArcBase,filletRings[1]};
var arcLineBefore=PreviewContourTopology.Analyze(arcLineRings);
Assert(arcLineBefore.IsValid &&
       reversedLineArcBase.Segments[3] is PreviewArc &&
       reversedLineArcBase.Segments[4] is PreviewLine,
       "Reversed synthetic contour has ARC immediately before selected LINE");
var arcLineFillet=PreviewLineArcFilletEditor.FilletOutgoingLine(
    arcLineRings,0,4,5);
var arcLineAfter=PreviewContourTopology.Analyze(arcLineFillet.Rings);
var arcLineArcs=arcLineFillet.Rings[0].Segments.OfType<PreviewArc>().ToArray();
Assert(arcLineFillet.IsValidForPreview && !arcLineFillet.CanStartMachine &&
       arcLineAfter.IsValid &&
       arcLineFillet.Rings[0].Segments.Count==6 &&
       arcLineArcs.Length==2 &&
       arcLineArcs.Any(a=>Math.Abs(a.RadiusMm-5)<1e-6) &&
       arcLineArcs.Any(a=>Math.Abs(a.RadiusMm-20)<1e-6) &&
       arcLineFillet.Rings[1].Segments.Count==arcLineRings[1].Segments.Count &&
       Math.Abs(arcLineAfter.NetAreaMm2-tangentTopology.NetAreaMm2)<1e-5,
       "ARC to LINE mirrors LINE to ARC tangent solution and preserves hole and net area");
var arcLineRoundtrip=PreviewDxfRoundTripVerifier.Verify(
    arcLineFillet.Rings,PreviewDxfExporter.Export(arcLineFillet.Rings));
Assert(PreviewContourTopology.Analyze(arcLineRoundtrip).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(arcLineRoundtrip).NetAreaMm2-
                arcLineAfter.NetAreaMm2)<1e-5,
       "ARC to LINE fillet survives exact DXF export and reimport");
foreach(var radius in new[]{double.NaN,double.PositiveInfinity,0d,-2d,1_000_001d})
{
    bool rejected=false;
    try { PreviewLineArcFilletEditor.FilletOutgoingLine(arcLineRings,0,4,radius); }
    catch(FormatException) { rejected=true; }
    Assert(rejected,"Invalid ARC/LINE radius must fail without modifying drawing");
}
bool wrongLineRejected=false;
try { PreviewLineArcFilletEditor.FilletOutgoingLine(arcLineRings,0,2,5); }
catch(FormatException) { wrongLineRejected=true; }
Assert(wrongLineRejected,"ARC/LINE solver refuses a segment not preceded by ARC");
Assert(arcLineRings[0].Segments.Count==5 &&
       Math.Abs(PreviewContourTopology.Analyze(arcLineRings).NetAreaMm2-
                arcLineBefore.NetAreaMm2)<1e-6,
       "ARC to LINE editor leaves source geometry intact for Undo");

// B2: two clockwise quarter-circle ARCs with a sharp 90-degree
// direction change at their shared endpoint. Fillet must be analytical,
// tangent in traversal direction, and leave a separate circular hole intact.
var twoArcProfile=new PreviewPath(new PreviewSegment[]{
    new PreviewLine(new PreviewPoint(0,0),new PreviewPoint(100,0)),
    new PreviewArc(new PreviewPoint(120,0),20,180,-90),
    new PreviewArc(new PreviewPoint(140,20),20,180,-90),
    new PreviewLine(new PreviewPoint(140,40),new PreviewPoint(140,100)),
    new PreviewLine(new PreviewPoint(140,100),new PreviewPoint(0,100)),
    new PreviewLine(new PreviewPoint(0,100),new PreviewPoint(0,0))
},true);
var arcArcRings=new PreviewPath[]{twoArcProfile,filletRings[1]};
var arcArcBefore=PreviewContourTopology.Analyze(arcArcRings);
Assert(arcArcBefore.IsValid && arcArcRings.Length==2,
       "Two quarter-circle ARC source with separate hole is topologically valid");
var arcArcResult=PreviewArcArcFilletEditor.Fillet(arcArcRings,0,2,5);
var arcArcAfter=PreviewContourTopology.Analyze(arcArcResult.Rings);
var arcArcSegments=arcArcResult.Rings[0].Segments.OfType<PreviewArc>().ToArray();
Assert(arcArcResult.IsValidForPreview && !arcArcResult.CanStartMachine &&
       arcArcAfter.IsValid && arcArcAfter.Rings.Count==2 &&
       arcArcResult.Rings[0].Segments.Count==7 &&
       arcArcSegments.Length==3 &&
       arcArcSegments.Any(a=>Math.Abs(a.RadiusMm-5)<1e-6) &&
       arcArcSegments.Count(a=>Math.Abs(a.RadiusMm-20)<1e-6)==2 &&
       arcArcResult.Rings[1].Segments.Count==arcArcRings[1].Segments.Count,
       "ARC/ARC tangent fillet retains both original curvature radii and hole");
var arcArcDxf=PreviewDxfExporter.Export(arcArcResult.Rings);
var arcArcVerified=PreviewDxfRoundTripVerifier.Verify(arcArcResult.Rings,arcArcDxf);
Assert(PreviewContourTopology.Analyze(arcArcVerified).IsValid &&
       Math.Abs(PreviewContourTopology.Analyze(arcArcVerified).NetAreaMm2-
                arcArcAfter.NetAreaMm2)<1e-5,
       "Analytic ARC/ARC fillet survives exact DXF export and round trip");
foreach(var invalidRadius in new[]{double.NaN,double.PositiveInfinity,0d,-1d,1_000_001d})
{
    bool rejected=false;
    try { PreviewArcArcFilletEditor.Fillet(arcArcRings,0,2,invalidRadius); }
    catch(FormatException){rejected=true;}
    Assert(rejected,"Invalid ARC/ARC tangent fillet radius must fail closed");
}
bool invalidArcArcSelection=false;
try { PreviewArcArcFilletEditor.Fillet(arcArcRings,0,3,5); }
catch(FormatException){invalidArcArcSelection=true;}
Assert(invalidArcArcSelection,"ARC/ARC solver refuses a LINE instead of adjacent ARC");
Assert(twoArcProfile.Segments.Count==6 &&
       Math.Abs(PreviewContourTopology.Analyze(arcArcRings).NetAreaMm2-
                arcArcBefore.NetAreaMm2)<1e-6,
       "ARC/ARC fillet never mutates original Undo snapshot");

// Stage 2 B2: one-shot horizontal/vertical geometric edge constraints.
var horizontalSource=Document(
    Closed("SKEW_BOTTOM",new Point2(0,0),new Point2(100,3),
           new Point2(100,100),new Point2(0,100)),
    new DxfCircle("AXIS_HOLE",new Point2(50,50),10));
var horizontalRings=PreviewDxfExporter.ValidateDocument(horizontalSource);
var beforeHorizontal=PreviewContourTopology.Analyze(horizontalRings);
var horizontalAligned=PreviewAxisConstraintEditor.AlignEdge(
    horizontalRings,0,0,horizontal:true);
var alignedHorizontalEdge=(PreviewLine)horizontalAligned.Rings[0].Segments[0];
var oldHorizontalEdge=(PreviewLine)horizontalRings[0].Segments[0];
var oldHorizontalLength=Math.Sqrt(
    Math.Pow(oldHorizontalEdge.End.X-oldHorizontalEdge.Start.X,2)+
    Math.Pow(oldHorizontalEdge.End.Y-oldHorizontalEdge.Start.Y,2));
Assert(horizontalAligned.IsValidForPreview && !horizontalAligned.CanStartMachine &&
       Math.Abs(alignedHorizontalEdge.End.Y-alignedHorizontalEdge.Start.Y)<1e-7 &&
       Math.Abs(alignedHorizontalEdge.End.X-oldHorizontalLength)<1e-7 &&
       PreviewContourTopology.Analyze(horizontalAligned.Rings).IsValid &&
       horizontalAligned.Rings[1].Segments.Count==horizontalRings[1].Segments.Count,
       "Horizontal constraint preserves length and circle hole topology");
PreviewDxfRoundTripVerifier.Verify(horizontalAligned.Rings,
    PreviewDxfExporter.Export(horizontalAligned.Rings));

var verticalSource=Document(
    Closed("SKEW_RIGHT",new Point2(0,0),new Point2(100,0),
           new Point2(103,100),new Point2(0,100)),
    new DxfCircle("AXIS_HOLE2",new Point2(50,50),10));
var verticalRings=PreviewDxfExporter.ValidateDocument(verticalSource);
var verticalAligned=PreviewAxisConstraintEditor.AlignEdge(
    verticalRings,0,1,horizontal:false);
var alignedVerticalEdge=(PreviewLine)verticalAligned.Rings[0].Segments[1];
var oldVerticalEdge=(PreviewLine)verticalRings[0].Segments[1];
var oldVerticalLength=Math.Sqrt(
    Math.Pow(oldVerticalEdge.End.X-oldVerticalEdge.Start.X,2)+
    Math.Pow(oldVerticalEdge.End.Y-oldVerticalEdge.Start.Y,2));
Assert(verticalAligned.IsValidForPreview && !verticalAligned.CanStartMachine &&
       Math.Abs(alignedVerticalEdge.End.X-alignedVerticalEdge.Start.X)<1e-7 &&
       Math.Abs(alignedVerticalEdge.End.Y-oldVerticalLength)<1e-7 &&
       PreviewContourTopology.Analyze(verticalAligned.Rings).IsValid &&
       verticalAligned.Rings[1].Segments.Count==verticalRings[1].Segments.Count,
       "Vertical constraint keeps edge length and internal hole unchanged");
PreviewDxfRoundTripVerifier.Verify(verticalAligned.Rings,
    PreviewDxfExporter.Export(verticalAligned.Rings));
Assert(Math.Abs(PreviewContourTopology.Analyze(horizontalRings).NetAreaMm2-
                beforeHorizontal.NetAreaMm2)<1e-6 &&
       Math.Abs(oldHorizontalEdge.End.Y-3)<1e-7 &&
       Math.Abs(oldVerticalEdge.End.X-103)<1e-7,
       "Constraint does not mutate source DXF preview / Undo snapshot");
bool axisArcRejected=false;
try { PreviewAxisConstraintEditor.AlignEdge(filletRings,1,0,true); }
catch(FormatException){axisArcRejected=true;}
Assert(axisArcRejected,"Axis constraint refuses ARC instead of distorting it");
bool axisIndexRejected=false;
try { PreviewAxisConstraintEditor.AlignEdge(verticalRings,0,99,false); }
catch(FormatException){axisIndexRejected=true;}
Assert(axisIndexRejected,"Axis constraint refuses nonexistent edge");

// Stage 2 B2: persistent axis rules follow the DXF Undo/Redo snapshot.
var pinnedSource=horizontalAligned.Rings;
var pinHorizontal=new PreviewAxisRule(0,0,true);
var pinnedRules=PreviewPersistentAxisConstraints.Add(
    pinnedSource,Array.Empty<PreviewAxisRule>(),pinHorizontal);
Assert(pinnedRules.Count==1 && pinnedRules[0]==pinHorizontal,
       "Already aligned edge can be pinned with an indexed axis relation");
var compatiblePinnedEdit=PreviewVertexEditor.MoveVertex(
    pinnedSource,0,1,new PreviewPoint(110,0));
PreviewPersistentAxisConstraints.RequireCompatibleEdit(
    pinnedSource,compatiblePinnedEdit.Rings,pinnedRules);
Assert(PreviewContourTopology.Analyze(compatiblePinnedEdit.Rings).IsValid,
       "Pinned horizontal edge accepts compatible vertex edits");
bool incompatiblePinnedRejected=false;
try
{
    var incompatible=PreviewVertexEditor.MoveVertex(
        pinnedSource,0,1,new PreviewPoint(110,8));
    PreviewPersistentAxisConstraints.RequireCompatibleEdit(
        pinnedSource,incompatible.Rings,pinnedRules);
}
catch(FormatException){incompatiblePinnedRejected=true;}
Assert(incompatiblePinnedRejected,
       "Pinned horizontal edge rejects an edit that changes Y coordinate");
bool opposingPinRejected=false;
try
{
    PreviewPersistentAxisConstraints.Add(
        pinnedSource,pinnedRules,new PreviewAxisRule(0,0,false));
}
catch(FormatException){opposingPinRejected=true;}
Assert(opposingPinRejected,"Contradictory horizontal and vertical rules are rejected");
var pinnedInitialDxf=DxfReader.Parse(PreviewDxfExporter.Export(pinnedSource));
var persistentHistory=new DxfPreviewHistory(pinnedInitialDxf);
Assert(persistentHistory.CurrentAxisRules.Count==0,
       "Fresh DXF session starts without inherited axis rules");
persistentHistory.Push(pinnedInitialDxf,pinnedRules);
var pinnedChangedDxf=DxfReader.Parse(
    PreviewDxfExporter.Export(compatiblePinnedEdit.Rings));
persistentHistory.Push(pinnedChangedDxf,pinnedRules);
Assert(persistentHistory.CurrentAxisRules.SequenceEqual(pinnedRules) &&
       persistentHistory.UndoCount==2,
       "Compatible DXF edit and axis rules share same history state");
persistentHistory.Undo();
Assert(persistentHistory.CurrentAxisRules.SequenceEqual(pinnedRules),
       "Undo of geometry retains rules from prior pinned state");
persistentHistory.Undo();
Assert(persistentHistory.CurrentAxisRules.Count==0,
       "Undo of rule registration restores unpinned state");
persistentHistory.Redo();
Assert(persistentHistory.CurrentAxisRules.SequenceEqual(pinnedRules),
       "Redo restores rule registration atomically");
var unpinnedRules=PreviewPersistentAxisConstraints.Remove(
    persistentHistory.CurrentAxisRules,pinHorizontal);
persistentHistory.Push(persistentHistory.Current,unpinnedRules);
Assert(persistentHistory.CurrentAxisRules.Count==0 &&
       !persistentHistory.CanRedo,
       "Rule removal creates a new history branch and discards stale redo");
var newSession=new DxfPreviewHistory(pinnedInitialDxf);
Assert(newSession.CurrentAxisRules.Count==0,
       "Opening another DXF does not leak old indexed constraints");
bool invalidPinRejected=false;
try
{
    PreviewPersistentAxisConstraints.Add(
        pinnedSource,pinnedRules,new PreviewAxisRule(1,0,true));
}
catch(FormatException){invalidPinRejected=true;}
Assert(invalidPinRejected,"Persistent rule refuses ARC-only hole");

// B2: bounded automatic single-vertex projection onto saved relations.
var autoProjected=PreviewPersistentAxisConstraints.MoveVertexConstrained(
    pinnedSource,0,1,new PreviewPoint(112,23),pinnedRules);
var projectedEdge=(PreviewLine)autoProjected.Rings[0].Segments[0];
Assert(autoProjected.IsValidForPreview && !autoProjected.CanStartMachine &&
       Math.Abs(projectedEdge.End.X-112)<1e-7 &&
       Math.Abs(projectedEdge.End.Y-projectedEdge.Start.Y)<1e-7 &&
       PreviewContourTopology.Analyze(autoProjected.Rings).IsValid &&
       autoProjected.Rings[1].Segments.Count==pinnedSource[1].Segments.Count,
       "Single vertex move projects requested point onto horizontal relation");
PreviewDxfRoundTripVerifier.Verify(autoProjected.Rings,
    PreviewDxfExporter.Export(autoProjected.Rings));
var constrainedRectangle=PreviewDxfExporter.ValidateDocument(Document(
    Closed("CONSTRAINT_RECT",new Point2(0,0),new Point2(100,0),
           new Point2(100,100),new Point2(0,100))));
var twoAxisRules=PreviewPersistentAxisConstraints.Add(
    constrainedRectangle,
    PreviewPersistentAxisConstraints.Add(
        constrainedRectangle,Array.Empty<PreviewAxisRule>(),
        new PreviewAxisRule(0,0,true)),
    new PreviewAxisRule(0,1,false));
var projectedCorner=PreviewPersistentAxisConstraints.MoveVertexConstrained(
    constrainedRectangle,0,1,new PreviewPoint(97,12),twoAxisRules);
var projectedCornerPoint=((PreviewLine)projectedCorner.Rings[0].Segments[1]).Start;
Assert(Math.Abs(projectedCornerPoint.X-100)<1e-7 &&
       Math.Abs(projectedCornerPoint.Y)<1e-7 &&
       PreviewContourTopology.Analyze(projectedCorner.Rings).IsValid,
       "Two orthogonal pinned edges keep the shared vertex fixed on both axes");
bool collapsedProjectionRejected=false;
try {
    PreviewPersistentAxisConstraints.MoveVertexConstrained(
        pinnedSource,0,1,new PreviewPoint(0,23),pinnedRules);
} catch(FormatException) {collapsedProjectionRejected=true;}
Assert(collapsedProjectionRejected,
       "Constraint projection cannot collapse an edge to zero length");
Assert(Math.Abs(((PreviewLine)pinnedSource[0].Segments[0]).End.X-
                 oldHorizontalLength)<1e-7,
       "Projection never mutates the source Undo snapshot");

// B2: propagation through the actual graph of retained axis constraints.
// Moving bottom right of the rectangle drives bottom left's Y and top
// right's X while leaving the unrelated top left vertex untouched.
var propagated=PreviewAxisPropagationEditor.MoveConnectedVertices(
    constrainedRectangle,0,1,new PreviewPoint(110,10),twoAxisRules);
var propagatedLines=propagated.Rings[0].Segments.Cast<PreviewLine>().ToArray();
var propagatedPoints=propagatedLines.Select(x=>x.Start).ToArray();
Assert(propagated.IsValidForPreview && !propagated.CanStartMachine &&
       Math.Abs(propagatedPoints[0].X-0)<1e-7 &&
       Math.Abs(propagatedPoints[0].Y-10)<1e-7 &&
       Math.Abs(propagatedPoints[1].X-110)<1e-7 &&
       Math.Abs(propagatedPoints[1].Y-10)<1e-7 &&
       Math.Abs(propagatedPoints[2].X-110)<1e-7 &&
       Math.Abs(propagatedPoints[2].Y-100)<1e-7 &&
       Math.Abs(propagatedPoints[3].X-0)<1e-7 &&
       Math.Abs(propagatedPoints[3].Y-100)<1e-7 &&
       PreviewContourTopology.Analyze(propagated.Rings).IsValid,
       "Axis propagation moves multiple vertices only along pinned coordinate graphs");
PreviewPersistentAxisConstraints.RequireCompatibleEdit(
    constrainedRectangle,propagated.Rings,twoAxisRules);
PreviewDxfRoundTripVerifier.Verify(propagated.Rings,
    PreviewDxfExporter.Export(propagated.Rings));
var propagatedWithHole=PreviewAxisPropagationEditor.MoveConnectedVertices(
    new PreviewPath[]{constrainedRectangle[0],horizontalRings[1]},
    0,1,new PreviewPoint(110,10),twoAxisRules);
Assert(PreviewContourTopology.Analyze(propagatedWithHole.Rings).IsValid &&
       propagatedWithHole.Rings[1].Segments.Count==
           horizontalRings[1].Segments.Count,
       "Multi-vertex propagation preserves separate circular hole");
bool rejectedCollapsedGraph=false;
try {
    PreviewAxisPropagationEditor.MoveConnectedVertices(
        constrainedRectangle,0,1,new PreviewPoint(0,0),twoAxisRules);
} catch(FormatException) {rejectedCollapsedGraph=true;}
Assert(rejectedCollapsedGraph,
       "Graph propagation rejects zero-width contour without modifying source");
Assert(Math.Abs(((PreviewLine)constrainedRectangle[0].Segments[0]).End.X-100)<1e-7 &&
       Math.Abs(((PreviewLine)constrainedRectangle[0].Segments[0]).End.Y)<1e-7,
       "Multi-vertex solver leaves original snapshot unchanged");

// Stage 2 B2: persistent edge length, one-vertex circle projection and
// atomic Undo/Redo state; existing axis rules still guard incompatible edits.
var lengthSource=PreviewDxfExporter.ValidateDocument(Document(
    Closed("LENGTH_PLATE",new Point2(0,0),new Point2(100,0),
           new Point2(100,100),new Point2(0,100)),
    new DxfCircle("LENGTH_HOLE",new Point2(50,50),10)));
var pinnedLength=PreviewPersistentLengthConstraints.Add(
    lengthSource,Array.Empty<PreviewLengthRule>(),0,0);
Assert(pinnedLength.Count==1 &&
       Math.Abs(pinnedLength[0].LengthMm-100)<1e-8,
       "Register exact measured length of selected LINE edge");
var projectedLength=PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
    lengthSource,0,1,new PreviewPoint(80,-60),pinnedLength,
    Array.Empty<PreviewAxisRule>());
var movedEdge=(PreviewLine)projectedLength.Rings[0].Segments[0];
var movedLength=Math.Sqrt(Math.Pow(movedEdge.End.X-movedEdge.Start.X,2)+
                          Math.Pow(movedEdge.End.Y-movedEdge.Start.Y,2));
Assert(projectedLength.IsValidForPreview && !projectedLength.CanStartMachine &&
       Math.Abs(movedLength-100)<1e-7 &&
       Math.Abs(movedEdge.End.X-80)<1e-7 &&
       Math.Abs(movedEdge.End.Y+60)<1e-7 &&
       PreviewContourTopology.Analyze(projectedLength.Rings).IsValid &&
       projectedLength.Rings[1].Segments.Count==lengthSource[1].Segments.Count,
       "Pinned R100 edge follows requested ray while preserving hole and topology");
PreviewDxfRoundTripVerifier.Verify(projectedLength.Rings,
    PreviewDxfExporter.Export(projectedLength.Rings));
var projectedDifferent=PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
    lengthSource,0,1,new PreviewPoint(100,-50),pinnedLength,
    Array.Empty<PreviewAxisRule>());
var differentEdge=(PreviewLine)projectedDifferent.Rings[0].Segments[0];
Assert(Math.Abs(Math.Sqrt(Math.Pow(differentEdge.End.X,2)+
                            Math.Pow(differentEdge.End.Y,2))-100)<1e-7,
       "Nontrivial requested vertex position is projected onto exact length circle");
var lengthAxis=PreviewPersistentAxisConstraints.Add(
    lengthSource,Array.Empty<PreviewAxisRule>(),new PreviewAxisRule(0,0,true));
bool lengthAxisConflict=false;
try {
    PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
        lengthSource,0,1,new PreviewPoint(80,60),pinnedLength,lengthAxis);
} catch(FormatException){lengthAxisConflict=true;}
Assert(lengthAxisConflict,
       "Pinned length plus pinned horizontal axis cannot silently violate either");
// Two adjacent pinned lengths: calculate both circle intersections and
// select the branch closest to the requested point, without moving anchors.
var dualSource=PreviewDxfExporter.ValidateDocument(Document(
    Closed("DUAL_LENGTH",new Point2(0,0),new Point2(80,20),
           new Point2(120,100),new Point2(0,100)),
    new DxfCircle("DUAL_HOLE",new Point2(20,90),5)));
var firstDual=PreviewPersistentLengthConstraints.Add(
    dualSource,Array.Empty<PreviewLengthRule>(),0,0);
var dualLengths=PreviewPersistentLengthConstraints.Add(
    dualSource,firstDual,0,1);
var dualOriginal=PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
    dualSource,0,1,new PreviewPoint(80,20),dualLengths,
    Array.Empty<PreviewAxisRule>());
var dualAlternative=PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
    dualSource,0,1,new PreviewPoint(34,75),dualLengths,
    Array.Empty<PreviewAxisRule>());
var dualPoint=((PreviewLine)dualAlternative.Rings[0].Segments[1]).Start;
Assert(dualOriginal.IsValidForPreview && dualAlternative.IsValidForPreview &&
       !dualAlternative.CanStartMachine &&
       Math.Abs(dualPoint.X-80)>10 &&
       Math.Abs(dualPoint.Y-20)>10 &&
       PreviewContourTopology.Analyze(dualAlternative.Rings).IsValid &&
       dualAlternative.Rings[1].Segments.Count==dualSource[1].Segments.Count,
       "Two fixed lengths select the alternate valid circle intersection");
PreviewPersistentLengthConstraints.RequireCompatibleEdit(
    dualSource,dualAlternative.Rings,dualLengths);
PreviewDxfRoundTripVerifier.Verify(dualAlternative.Rings,
    PreviewDxfExporter.Export(dualAlternative.Rings));
var firstDualLine=(PreviewLine)dualAlternative.Rings[0].Segments[0];
var secondDualLine=(PreviewLine)dualAlternative.Rings[0].Segments[1];
Assert(Math.Abs(Math.Sqrt(Math.Pow(firstDualLine.End.X-firstDualLine.Start.X,2)+
                           Math.Pow(firstDualLine.End.Y-firstDualLine.Start.Y,2))-
                dualLengths[0].LengthMm)<1e-6 &&
       Math.Abs(Math.Sqrt(Math.Pow(secondDualLine.End.X-secondDualLine.Start.X,2)+
                           Math.Pow(secondDualLine.End.Y-secondDualLine.Start.Y,2))-
                dualLengths[1].LengthMm)<1e-6,
       "Both fixed lengths remain exact after circle-circle solution");
bool wrongDualAxisRejected=false;
try {
    var extraAxis=PreviewPersistentAxisConstraints.Add(
        dualSource,Array.Empty<PreviewAxisRule>(),new PreviewAxisRule(0,3,false));
    // Vertex 0 is on that axis, but vertex 1 is not: this unrelated axis
    // remains valid while both lengths are solved.
    PreviewPersistentLengthConstraints.MoveVertexOnPinnedLength(
        dualSource,0,1,new PreviewPoint(34,75),dualLengths,extraAxis);
} catch(FormatException){wrongDualAxisRejected=true;}
Assert(!wrongDualAxisRejected,
       "Unrelated pinned axis must not block dual-length intersection");

bool invalidLengthEdgeRejected=false;
try {PreviewPersistentLengthConstraints.Add(
    lengthSource,pinnedLength,1,0);}
catch(FormatException){invalidLengthEdgeRejected=true;}
Assert(invalidLengthEdgeRejected,
       "Cannot attach persistent length to a circular ARC-only hole");
var initialLengthDxf=DxfReader.Parse(PreviewDxfExporter.Export(lengthSource));
var lengthHistory=new DxfPreviewHistory(initialLengthDxf);
lengthHistory.Push(initialLengthDxf,Array.Empty<PreviewAxisRule>(),pinnedLength);
var changedLengthDxf=DxfReader.Parse(
    PreviewDxfExporter.Export(projectedDifferent.Rings));
lengthHistory.Push(changedLengthDxf,Array.Empty<PreviewAxisRule>(),pinnedLength);
Assert(lengthHistory.CurrentLengthRules.SequenceEqual(pinnedLength),
       "Committed geometry and saved length rule share one history state");
lengthHistory.Undo();
Assert(lengthHistory.CurrentLengthRules.SequenceEqual(pinnedLength),
       "Undo geometry retains pinned length from previous state");
lengthHistory.Undo();
Assert(lengthHistory.CurrentLengthRules.Count==0,
       "Undo pin restores state without length constraint");
lengthHistory.Redo();
Assert(lengthHistory.CurrentLengthRules.SequenceEqual(pinnedLength),
       "Redo pin restores exact constrained length");
var lengthUnpinned=PreviewPersistentLengthConstraints.Remove(pinnedLength,0,0);
lengthHistory.Push(lengthHistory.Current,Array.Empty<PreviewAxisRule>(),lengthUnpinned);
Assert(lengthHistory.CurrentLengthRules.Count==0 && !lengthHistory.CanRedo,
       "Removing length pin discards stale redo branch");
Assert(new DxfPreviewHistory(initialLengthDxf).CurrentLengthRules.Count==0 &&
       Math.Abs(((PreviewLine)lengthSource[0].Segments[0]).End.X-100)<1e-7,
       "New DXF session has no inherited length rules and original stays unchanged");

// Core CAD preview tolerance policy (not manufacturing precision).
var defaultPreviewPolicy = PreviewGeometryTolerancePolicy.Create();
Assert(defaultPreviewPolicy.JoinMm == 0.01 &&
       defaultPreviewPolicy.TopologyMm == 0.01,
       "Default core preview tolerances are explicit");
var strictPreviewPolicy = PreviewGeometryTolerancePolicy.Create(0.0001, 0.0002);
Assert(Math.Abs(strictPreviewPolicy.JoinMm - 0.0001) < 1e-12 &&
       Math.Abs(strictPreviewPolicy.TopologyMm - 0.0002) < 1e-12,
       "Independent core join and topology tolerances can be specified");
foreach (var invalid in new [] { double.NaN, double.PositiveInfinity,
                                 -1, 0, 1e-10, 1.001 })
{
    bool rejected = false;
    try { PreviewGeometryTolerancePolicy.Create(invalid, 0.01); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Assert(rejected, "Invalid core join tolerance rejected");
    rejected = false;
    try { PreviewGeometryTolerancePolicy.Create(0.01, invalid); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Assert(rejected, "Invalid core topology tolerance rejected");
}
var checkedContour = tangentFillet.Rings[0];
Assert(defaultPreviewPolicy.ValidateContour(checkedContour)
           .SequenceEqual(PreviewContourValidator.Validate(checkedContour, 0.01)),
       "Explicit tolerance policy delegates to the existing contour checker");
Assert(defaultPreviewPolicy.Analyze(tangentFillet.Rings).IsValid ==
       PreviewContourTopology.Analyze(tangentFillet.Rings, 0.01).IsValid,
       "Core tolerance policy preserves topology validity at default settings");
Assert(!defaultPreviewPolicy.Analyze(tangentFillet.Rings).CanStartMachine,
       "Preview tolerance configuration cannot authorize machine motion");
Assert(defaultPreviewPolicy.ValidateDrawing(tangentFillet.Rings).IsValid ==
       defaultPreviewPolicy.Analyze(tangentFillet.Rings).IsValid,
       "Full drawing check preserves default topology for existing geometry");
Assert(!defaultPreviewPolicy.ValidateDrawing(tangentFillet.Rings).CanStartMachine,
       "Full drawing validation never enables machine motion");
Assert(!defaultPreviewPolicy.ValidateDrawing(Array.Empty<PreviewPath>()).IsValid,
       "Drawing validation rejects empty input");
Assert(!defaultPreviewPolicy.ValidateDrawing(
           new PreviewPath[] { null! }).IsValid,
       "Drawing validation rejects null rings without throwing");

// Explicitly test that intersection precision changes a borderline LINE/LINE
// contact classification instead of merely storing a configuration number.
static PreviewPath PreviewSquare(double x0, double y0, double x1, double y1)
{
    var corners = new[] {
        new PreviewPoint(x0,y0), new PreviewPoint(x1,y0),
        new PreviewPoint(x1,y1), new PreviewPoint(x0,y1) };
    return new PreviewPath(Enumerable.Range(0,4)
        .Select(i => (PreviewSegment)new PreviewLine(
            corners[i], corners[(i+1)%4])).ToArray(), true);
}
var precisionLeft = PreviewSquare(0,0,10,10);
var precisionRight = PreviewSquare(10.00005,0,20.00005,10);
var preciseIntersections = PreviewGeometryTolerancePolicy.Create(
    0.01,0.01,1e-7);
var conservativeIntersections = PreviewGeometryTolerancePolicy.Create(
    0.01,0.01,1e-4);
Assert(preciseIntersections.ValidatePair(precisionLeft,precisionRight).Count==0,
       "Separated rectangles remain distinct under precise intersection tolerance");
Assert(conservativeIntersections.ValidatePair(precisionLeft,precisionRight).Count>0,
       "Conservative intersection tolerance rejects near-contact ambiguity");
Assert(preciseIntersections.IntersectionMm==1e-7 &&
       conservativeIntersections.IntersectionMm==1e-4,
       "Core intersection tolerance is explicitly configurable");
foreach(var invalidIntersection in new[]{
    -1d,0d,double.NaN,double.NegativeInfinity,1e-10,0.011 })
{
    bool rejected=false;
    try { PreviewGeometryTolerancePolicy.Create(0.01,0.01,invalidIntersection); }
    catch(ArgumentOutOfRangeException) { rejected=true; }
    Assert(rejected,"Unsafe analytic intersection tolerance rejected");
}

// Coincident circular sectors cross the 360-degree seam with a deliberately
// short overlapping ARC interval, shorter than 0.01 degree. The two sectors
// also meet at their centers; check the *overlap* diagnostic specifically.
static PreviewPath TestSector(double startDegrees,double sweepDegrees)
{
    var center = new PreviewPoint(0,0);
    static PreviewPoint At(double deg) =>
        new(Math.Cos(deg*Math.PI/180),Math.Sin(deg*Math.PI/180));
    return new PreviewPath([
        new PreviewArc(center,1,startDegrees,sweepDegrees),
        new PreviewLine(At(startDegrees+sweepDegrees),center),
        new PreviewLine(center,At(startDegrees))
    ],true);
}
var seamSector = TestSector(350,20);
var shortOverlapSector = TestSector(9.999,20);
var disjointSector = TestSector(10.001,20);
var seamOverlap = preciseIntersections.ValidatePair(seamSector,shortOverlapSector);
var seamDisjoint = preciseIntersections.ValidatePair(seamSector,disjointSector);
Assert(seamOverlap.Any(x=>x.Contains("Ambiguous coincident or overlapping",StringComparison.Ordinal)),
       "Short ARC overlap across 360-degree seam must be detected");
Assert(!seamDisjoint.Any(x=>x.Contains("Ambiguous coincident or overlapping",StringComparison.Ordinal)),
       "Nonoverlapping ARC sweeps must not be mistaken for coincident arcs");




// Optional independent-parser fixture export for CI. All source data
// comes from already verified CAD preview geometry; another implementation
// (ezdxf) independently parses and measures the serialized DXF entities.
var externalDxfDirectory=Environment.GetEnvironmentVariable("AI_CUTPRO_CI_DXF_DIR");
if(!string.IsNullOrWhiteSpace(externalDxfDirectory))
{
    Directory.CreateDirectory(externalDxfDirectory);
    // Independent GEOS test: two disjoint plates, four nested material/void
    // levels in the first and a separate hole in the second. All boundaries
    // are simple LINE rectangles with a known elementary area oracle.
    var geosNestedDrawing=new PreviewPath[]
    {
        PreviewSquare(0,0,100,100),
        PreviewSquare(20,20,80,80),
        PreviewSquare(35,35,65,65),
        PreviewSquare(45,45,55,55),
        PreviewSquare(150,0,210,60),
        PreviewSquare(170,20,190,40)
    };
    Assert(PreviewGeometryTolerancePolicy.Create().ValidateDrawing(geosNestedDrawing).IsValid,
           "GEOS external multi-part nested fixture passes core preview");
    var externalSamples=new (string Name,IReadOnlyList<PreviewPath> Rings)[]
    {
        ("line_arc_tangent",tangentFillet.Rings),
        ("arc_arc_tangent",arcArcResult.Rings),
        ("two_pinned_lengths",dualAlternative.Rings),
        ("constrained_rectangle_with_hole",propagatedWithHole.Rings),
        ("two_parts_nested_island",geosNestedDrawing)
    };
    var manifest=new List<object>();
    foreach(var sample in externalSamples)
    {
        var name=sample.Name+".dxf";
        File.WriteAllText(Path.Combine(externalDxfDirectory,name),
                          PreviewDxfExporter.Export(sample.Rings));
        manifest.Add(new
        {
            file=name,
            lines=sample.Rings.Sum(r=>r.Segments.Count(x=>x is PreviewLine)),
            arcs=sample.Rings.Sum(r=>r.Segments.Count(x=>x is PreviewArc)),
            radii=sample.Rings.SelectMany(r=>r.Segments)
                .OfType<PreviewArc>().Select(a=>a.RadiusMm)
                .OrderBy(x=>x).ToArray(),
            perimeter=sample.Rings.Sum(r=>PreviewContourMetrics.Measure(r).PerimeterMm),
            // This fixture has one outer rectangle and one verified circular hole.
            // The external parser must independently recover that nesting.
            expected_rings=sample.Name=="constrained_rectangle_with_hole" ? (int?)2 :
                           sample.Name=="two_parts_nested_island" ? (int?)6 : null,
            expected_holes=sample.Name=="constrained_rectangle_with_hole" ? (int?)1 :
                           sample.Name=="two_parts_nested_island" ? (int?)3 : null
        });
    }
    File.WriteAllText(Path.Combine(externalDxfDirectory,"manifest.json"),
                      JsonSerializer.Serialize(manifest));
}

Console.WriteLine("AI CutPro core checks passed.");
