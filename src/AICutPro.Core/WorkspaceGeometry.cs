namespace AICutPro.Core;

/// <summary>Virtual workspace dimensions, in millimetres. No controller or machine movement is implied.</summary>
public sealed record WorkspaceGeometry(Sheet Table, Sheet MaterialSheet)
{
    public static WorkspaceGeometry Default => new(new Sheet(4000, 2000), new Sheet(3000, 1500));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        static bool DimensionOk(Sheet? sheet) => sheet is not null &&
            double.IsFinite(sheet.WidthMm) && double.IsFinite(sheet.HeightMm) &&
            sheet.WidthMm > 0 && sheet.HeightMm > 0 &&
            sheet.WidthMm <= 100000 && sheet.HeightMm <= 100000;
        if (!DimensionOk(Table)) errors.Add("Invalid machine table dimensions.");
        if (!DimensionOk(MaterialSheet)) errors.Add("Invalid material sheet dimensions.");
        if (errors.Count > 0) return errors;
        if (MaterialSheet.WidthMm > Table.WidthMm || MaterialSheet.HeightMm > Table.HeightMm)
            errors.Add("Material sheet does not fit within the virtual machine table.");
        return errors;
    }
}
