namespace AICutPro.Core;

/// <summary>
/// Stable, offline, localised explanations for errors detected while opening DXF.
/// Entries describe only actions AI CutPro can presently perform, or clearly
/// identify a future recovery feature. No CAM or cutting authorisation.
/// </summary>
public sealed record DxfDiagnosticHelp(
    string Code, string TitleRu, string TitleEn,
    string ExplanationRu, string ExplanationEn,
    string AdviceRu, string AdviceEn, bool RepairAvailable);

public static class DxfDiagnosticCatalog
{
    private static readonly IReadOnlyList<DxfDiagnosticHelp> Entries =
    [
        new("DXF-1001", "Файл пуст или слишком велик", "Empty or oversized file",
            "Допустимый размер DXF для предпросмотра: от 1 байта до 8 МБ.",
            "Preview DXF size must be between 1 byte and 8 MB.",
            "Проверьте файл и загрузите исходные DXF-данные размером не более 8 МБ. Не переименовывайте другой формат в DXF.",
            "Check the source data and load a DXF of at most 8 MB; renaming another format does not convert it.", false),
        new("DXF-1002", "Двоичный или неподдерживаемый формат DXF", "Binary or unsupported DXF format",
            "Текущий импорт поддерживает текстовые DXF. Двоичные файлы не читаются.",
            "The current importer supports text DXF, not binary files.",
            "Не меняйте расширение файла вручную. Конвертация бинарного DXF внутри AI CutPro пока не реализована; импорт заблокирован.",
            "Do not rename the extension; internal binary-DXF conversion is not yet implemented, so import is blocked.", false),
        new("DXF-1003", "Повреждена структура DXF", "Malformed DXF structure",
            "Данные обрезаны, нарушены пары групп или отсутствуют обязательные координаты.",
            "The file is truncated, DXF group pairs are invalid, or required coordinates are missing.",
            "Сохраните оригинал без изменений. Проверьте полноту файла; восстановление уцелевших объектов внутри AI CutPro запланировано, но пока недоступно.",
            "Keep the original unchanged. Check file completeness; in-app salvage is planned but not yet available.", false),
        new("DXF-1004", "Некорректная кодировка", "Invalid DXF text encoding",
            "Текст DXF содержит недопустимые байты или неподдерживаемую кодовую страницу.",
            "DXF text has invalid bytes or an unsupported code page.",
            "Проверьте сведения о кодировке файла. Автоматический подбор неподтверждённой кодировки запрещён, чтобы не повредить данные.",
            "Check the file's encoding declaration. Guessing an unverified encoding is disabled to protect data.", false),
        new("GEO-3001", "Открытая геометрия: требуется назначение", "Open geometry: intent required",
            "Незамкнутая линия может быть намеренным сквозным резом, прерывистым резом, разрезом под отгиб или повреждённым контуром детали. Без назначения операция не определена.",
            "An open path may be an intentional cut, perforation, bend relief or a broken part outline. Its purpose must be assigned.",
            "Выберите импортированную DXF LINE и назначьте операцию в окне технологических линий. Разрез, перфорация, сгиб и разметка могут быть намеренными; не замыкайте автоматически. Прочие открытые объекты пока требуют отдельной проверки.",
            "Select an imported DXF LINE and explicitly assign its preview operation. Open cut, perforation, bend and marking may be intentional; never auto-close. Other open entities still require separate review.", false),
        new("GEO-3002", "Дубли или неоднозначные соединения", "Duplicate or ambiguous segments",
            "Обнаружены повторяющиеся сегменты, ветвление или неоднозначная стыковка.",
            "Repeated segments, branching or ambiguous joining were detected.",
            "Проверьте совпадающие рёбра и вершины в предпросмотре. Удаление дубликатов внутри AI CutPro запланировано, но пока недоступно.",
            "Review coincident edges and vertices in preview. In-app duplicate removal is planned but unavailable.", false),
        new("GEO-3003", "Пересечение контуров", "Intersecting contours",
            "Границы пересекаются, касаются или имеют самопересечение.",
            "Boundaries intersect, touch or self-intersect.",
            "Проверьте контуры и отверстия в предпросмотре. Не используйте чертёж для резки: исправление пересечений ещё не автоматизировано.",
            "Inspect contours and holes in preview. Do not cut; intersection repair is not automated.", false),
        new("GEO-3004", "Неверное положение отверстия", "Invalid hole placement",
            "Отверстие не находится строго внутри одной детали или касается другого отверстия.",
            "A hole is not strictly within one part or touches another hole.",
            "Проверьте положение и размеры отверстий. Встроенные операции редактирования отдельных окружностей доступны только для проверенной геометрии.",
            "Review hole position and dimensions. In-app circle editing works only with verified geometry.", false),
        new("GEO-3099", "Геометрия не прошла проверку", "Geometry preflight rejected",
            "Проверка не смогла подтвердить корректность геометрии для дальнейшей обработки.",
            "The preflight could not establish that geometry is valid for further processing.",
            "Изучите техническое сообщение и исходный DXF в режиме просмотра. CAM и резка остаются заблокированными.",
            "Inspect the technical message and source DXF in preview. CAM and cutting remain blocked.", false),
        new("DXF-1099", "Ошибка чтения DXF", "DXF read error",
            "Возникла ошибка, которая пока не имеет отдельного диагностического кода.",
            "An import error does not yet have a more specific diagnostic code.",
            "Сохраните исходный файл и техническое сообщение. Не используйте этот чертёж для резки до проверки.",
            "Keep the original and technical message; do not use the drawing for cutting until verified.", false)
    ];

    public static IReadOnlyList<DxfDiagnosticHelp> All => Entries;

    public static DxfDiagnosticHelp ByCode(string code) =>
        Entries.FirstOrDefault(e => e.Code == code) ??
        throw new ArgumentOutOfRangeException(nameof(code), "Unknown DXF diagnostic code.");

    public static DxfDiagnosticHelp Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = error.Message;
        if (message.Contains("1..8000000 bytes", StringComparison.Ordinal) ||
            message.Contains("exceeds maximum supported size", StringComparison.Ordinal) ||
            message.Contains("preview size limit", StringComparison.Ordinal))
            return ByCode("DXF-1001");
        if (message.Contains("Binary DXF", StringComparison.Ordinal) ||
            message.Contains("Unexpected NUL bytes", StringComparison.Ordinal) ||
            message.Contains("NUL characters", StringComparison.Ordinal))
            return ByCode("DXF-1002");
        if (error.InnerException is System.Text.DecoderFallbackException ||
            error is System.Text.DecoderFallbackException ||
            message.Contains("code page", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DWGCODEPAGE", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("malformed text bytes", StringComparison.Ordinal))
            return ByCode("DXF-1004");
        if (error is FormatException &&
            (message.Contains("Truncated", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("DXF group", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Invalid DXF number", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Invalid DXF integer", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Invalid DXF group code", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Missing required", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Invalid $INSUNITS", StringComparison.Ordinal) ||
             message.Contains("Conflicting DXF", StringComparison.Ordinal)))
            return ByCode("DXF-1003");
        return ByCode("DXF-1099");
    }

    /// <summary>
    /// Conservative mapping from existing geometry warnings to stable help
    /// entries. Unrecognised warnings must never be silently treated as safe.
    /// </summary>
    public static DxfDiagnosticHelp ClassifyGeometry(string issue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);
        if (issue.Contains("Open chain", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Incomplete contour", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Open, degenerate", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("незамкнут", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Открытая полилиния", StringComparison.OrdinalIgnoreCase))
            return ByCode("GEO-3001");
        if (issue.Contains("Repeated segment", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Ambiguous endpoint", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Ambiguous continuation", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("branching segment", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
            return ByCode("GEO-3002");
        if (issue.Contains("отверсти", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Окружность не находится", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Круглые отверстия", StringComparison.OrdinalIgnoreCase))
            return ByCode("GEO-3004");
        if (issue.Contains("intersection", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("пересека", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("касаются", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Самопересечение", StringComparison.OrdinalIgnoreCase))
            return ByCode("GEO-3003");
        return ByCode("GEO-3099");
    }

    /// <summary>
    /// Resolve a layer only when the existing contour assembler explicitly
    /// names that exact layer. Never guess a physical X/Y point from text.
    /// Match the full prefix to preserve layer names containing spaces/colon.
    /// </summary>
    public static string? KnownIssueLayer(
        string issue, IReadOnlyList<DxfShape> importedShapes)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(importedShapes);
        foreach (var layer in importedShapes.Select(shape => shape.Layer)
                     .Where(layer => !string.IsNullOrWhiteSpace(layer))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(layer => layer.Length))
        {
            if (issue.StartsWith("Layer " + layer + ": ", StringComparison.Ordinal))
                return layer;
        }
        return null;
    }

    public static string ExplainGeometryRu(string issue)
    {
        var help = ClassifyGeometry(issue);
        return $"{help.Code}: {help.TitleRu}. {help.ExplanationRu} " +
               $"Что делать: {help.AdviceRu} Технические подробности: {issue}";
    }

    public static string ExplainRu(Exception error)
    {
        var help = Classify(error);
        return $"{help.Code}: {help.TitleRu}. {help.ExplanationRu} " +
               $"Что делать: {help.AdviceRu} Технические подробности: {error.Message}";
    }
}
