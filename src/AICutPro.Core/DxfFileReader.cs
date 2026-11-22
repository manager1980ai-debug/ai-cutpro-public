using System.Globalization;
using System.Text;

namespace AICutPro.Core;

/// <summary>Information about decoding an ASCII DXF file; does not imply CAM compatibility.</summary>
public sealed record DxfFileReadResult(DxfImportResult Document, string EncodingName, string? AcadVersion);

/// <summary>
/// Byte-oriented import for ASCII DXF of different AutoCAD generations.
/// Unknown code pages, malformed bytes, binary DXF and uncertain formats fail closed.
/// </summary>
public static class DxfFileReader
{
    private static readonly HashSet<string> KnownVersions =
    [
        "AC1009", "AC1012", "AC1014", "AC1015", "AC1018", "AC1021",
        "AC1024", "AC1027", "AC1032"
    ];

    public static DxfFileReadResult Read(byte[] bytes)
    {
        try { return ReadCore(bytes); }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("DXF contains malformed text bytes for the declared encoding.", ex);
        }
    }

    private static DxfFileReadResult ReadCore(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > 8_000_000)
            throw new FormatException("DXF file must contain 1..8000000 bytes.");
        if (bytes.AsSpan().StartsWith("AutoCAD Binary DXF"u8))
            throw new FormatException("Binary DXF is not supported; export as ASCII DXF.");
        if (bytes.AsSpan().IndexOf((byte)0) >= 0 &&
            !(bytes.Length >= 2 &&
              ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff))))
            throw new FormatException("Unexpected NUL bytes; binary or malformed DXF rejected.");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        string used;
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            text = new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3);
            used = "UTF-8 BOM";
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
        {
            text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            used = "UTF-16 LE BOM";
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
        {
            text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            used = "UTF-16 BE BOM";
        }
        else
        {
            // DXF group codes and HEADER variable names are ASCII, even in legacy code pages.
            var asciiView = Encoding.Latin1.GetString(bytes);
            var codePage = HeaderValue(asciiView, "$DWGCODEPAGE");
            var encoding = ResolveEncoding(codePage);
            text = encoding.GetString(bytes);
            used = codePage is null ? "UTF-8 (strict, no declared codepage)" : codePage;
        }
        if (text.IndexOf('\0') >= 0)
            throw new FormatException("DXF text contains NUL characters.");
        var version = HeaderValue(text, "$ACADVER");
        var parsed = DxfReader.Parse(text);
        var warnings = parsed.Warnings.ToList();
        if (version is not null && !KnownVersions.Contains(version.ToUpperInvariant()))
            warnings.Add($"Unknown $ACADVER={version}; DXF version requires verification.");
        var declaredPage = HeaderValue(text, "$DWGCODEPAGE");
        if (declaredPage is not null && (used.Contains("BOM", StringComparison.Ordinal) ||
            used.StartsWith("UTF-8 (strict", StringComparison.Ordinal)))
        {
            // A BOM is authoritative, but a conflicting DXF header must be reviewed.
            if (!CodePageMatchesUnicode(declaredPage, used))
                warnings.Add($"DXF encoding declaration {declaredPage} differs from {used}; manual verification required.");
        }
        return new(new DxfImportResult(parsed.Shapes, warnings, parsed.InsUnits), used, version);
    }

    private static bool CodePageMatchesUnicode(string declared, string used) =>
        used.StartsWith("UTF-8", StringComparison.Ordinal) &&
        (declared.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
         declared.Equals("UTF8", StringComparison.OrdinalIgnoreCase)) ||
        used.StartsWith("UTF-16", StringComparison.Ordinal) &&
        (declared.Equals("UTF-16", StringComparison.OrdinalIgnoreCase) ||
         declared.Equals("UTF16", StringComparison.OrdinalIgnoreCase));

    private static Encoding ResolveEncoding(string? codePage)
    {
        if (codePage is null || codePage.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
            codePage.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
            return new UTF8Encoding(false, true);
        var upper = codePage.ToUpperInvariant();
        var digits = upper.StartsWith("ANSI_", StringComparison.Ordinal) ? upper[5..] :
                     upper.StartsWith("DOS", StringComparison.Ordinal) ? upper[3..] : "";
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
            throw new FormatException($"Unsupported DXF $DWGCODEPAGE={codePage}; choose an explicitly supported encoding.");
        // Common AutoCAD ANSI/DOS code pages. Refuse arbitrary code-page identifiers.
        if (page is not (1250 or 1251 or 1252 or 1253 or 1254 or 1255 or 1256 or
                         1257 or 1258 or 932 or 936 or 949 or 950 or 437 or 850 or 866))
            throw new FormatException($"Unsupported DXF code page {page}.");
        return Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static string? HeaderValue(string text, string variable)
    {
        using var reader = new StringReader(text);
        var inHeader = false;
        var sectionPending = false;
        var awaitingValue = false;
        string? code;
        var count = 0;
        while ((code = reader.ReadLine()) != null && count++ < 250000)
        {
            var value = reader.ReadLine() ?? throw new FormatException("Truncated DXF group pair.");
            code = code.Trim();
            value = value.Trim();
            if (code == "0" && value.Equals("SECTION", StringComparison.OrdinalIgnoreCase))
            {
                sectionPending = true; awaitingValue = false; continue;
            }
            if (sectionPending && code == "2")
            {
                inHeader = value.Equals("HEADER", StringComparison.OrdinalIgnoreCase);
                sectionPending = false; continue;
            }
            if (code == "0" && value.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase))
            {
                inHeader = false; awaitingValue = false; continue;
            }
            if (!inHeader) continue;
            if (code == "9")
            {
                awaitingValue = value.Equals(variable, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (awaitingValue && (code == "1" || code == "3"))
                return value;
            awaitingValue = false;
        }
        return null;
    }
}
