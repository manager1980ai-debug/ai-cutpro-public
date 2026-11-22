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
             message.Contains("Missing required", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Invalid $INSUNITS", StringComparison.Ordinal) ||
             message.Contains("Conflicting DXF", StringComparison.Ordinal)))
            return ByCode("DXF-1003");
        return ByCode("DXF-1099");
    }

    public static string ExplainRu(Exception error)
    {
        var help = Classify(error);
        return $"{help.Code}: {help.TitleRu}. {help.ExplanationRu} " +
               $"Что делать: {help.AdviceRu} Технические подробности: {error.Message}";
    }
}
