namespace AICutPro.Core;

/// <summary>
/// Captures the exact project and preview references before asynchronous
/// file selection. A newer project or drawing must never be overwritten
/// by an older operation completing after it.
/// </summary>
public sealed record ProjectOperationSnapshot(
    CutProject Project, DxfImportResult? Preview, int SelectedPart)
{
    public bool IsCurrent(CutProject project, DxfImportResult? preview,
        int selectedPart, bool editorOpen) =>
        !editorOpen &&
        ReferenceEquals(Project, project) &&
        ReferenceEquals(Preview, preview) &&
        SelectedPart == selectedPart;
}
