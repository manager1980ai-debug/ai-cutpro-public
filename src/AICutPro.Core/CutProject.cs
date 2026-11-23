using System.Text.Json;

namespace AICutPro.Core;

/// <summary>
/// Editable project data in millimetres. This is not a machine program.
/// </summary>
public sealed record CutProject(
    int FormatVersion,
    string Name,
    Sheet Sheet,
    List<Part> Parts,
    string Material,
    double ThicknessMm,
    CutProjectDxfWorkspace? DxfWorkspace = null)
{
    public const int CurrentFormatVersion = 1;

    public static CutProject CreateDefault() =>
        new(CurrentFormatVersion, "Новый проект", new Sheet(3000, 1500),
            [new Part("Кронштейн", 180, 220, 10)], "Не задан", 1);
}

/// <summary>Strict, versioned project interchange. Malformed projects are rejected rather than silently repaired.</summary>
public static class CutProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    public static string Serialize(CutProject project)
    {
        Validate(project);
        var serialized = JsonSerializer.Serialize(project, Options);
        if (serialized.Length > 20_000_000)
            throw new FormatException("Файл проекта превышает допустимый размер.");
        return serialized;
    }

    public static CutProject Deserialize(string json)
    {
        if (json is { Length: > 20_000_000 })
            throw new FormatException("Файл проекта превышает допустимый размер.");
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Файл проекта пуст.");
        CutProject? result;
        try { result = JsonSerializer.Deserialize<CutProject>(json, Options); }
        catch (JsonException ex) { throw new FormatException("Некорректный JSON проекта.", ex); }
        if (result is null) throw new FormatException("Файл проекта не содержит объект.");
        Validate(result);
        return result;
    }

    public static void Validate(CutProject? project)
    {
        if (project is null) throw new FormatException("Проект отсутствует.");
        if (project.FormatVersion != CutProject.CurrentFormatVersion)
            throw new FormatException("Неподдерживаемая версия файла проекта.");
        if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 200)
            throw new FormatException("Некорректное название проекта.");
        if (project.Sheet is null || !Positive(project.Sheet.WidthMm) || !Positive(project.Sheet.HeightMm))
            throw new FormatException("Некорректный размер листа.");
        if (project.Parts is null || project.Parts.Count is < 1 or > 10000)
            throw new FormatException("Список деталей должен содержать от 1 до 10000 записей.");
        if (project.Parts.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 200 ||
            !Positive(p.WidthMm) || !Positive(p.HeightMm) || p.Quantity is < 1 or > 100000))
            throw new FormatException("Некорректные параметры детали.");
        if (project.Parts.Sum(p => (long)p.Quantity) > 100000)
            throw new FormatException("Слишком много деталей в проекте.");
        if (string.IsNullOrWhiteSpace(project.Material) || project.Material.Length > 200 ||
            !Positive(project.ThicknessMm))
            throw new FormatException("Некорректные параметры материала.");
        if (project.DxfWorkspace is not null)
            CutProjectDxfWorkspaceCodec.Validate(project.DxfWorkspace, project.Sheet);
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
