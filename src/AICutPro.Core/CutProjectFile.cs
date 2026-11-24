using System.Text;

namespace AICutPro.Core;

/// <summary>
/// Local project-file persistence. A complete validated JSON snapshot is written
/// to a sibling temporary file before replacing the destination. Never truncate
/// an existing project to prepare a write. No CAM or machine commands.
/// </summary>
public static class CutProjectFile
{
    public static void Save(string path, CutProject project)
    {
        // Validation precedes any filesystem modification.
        var json = CutProjectSerializer.Serialize(project);
        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Project destination requires a directory.", nameof(path));
        var temporaryPath = Path.Combine(folder,
            ".aicutpro-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporaryFile = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                ownsTemporaryFile = true;
                using (var writer = new StreamWriter(stream,
                           new UTF8Encoding(false, true), 8192, leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }

            // Same-directory rename avoids cross-volume copy/truncation.
            // If the filesystem refuses replacement, do not retry by deleting
            // or truncating the existing destination.
            File.Move(temporaryPath, fullPath, overwrite: true);
            ownsTemporaryFile = false;
        }
        finally
        {
            if (ownsTemporaryFile)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
