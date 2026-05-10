namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Finds an Actimed-printed PDF protocol for a given Prüfberichtsnummer in the
/// configured drop-folder. Implements 5.7 of CLAUDE.md.
/// </summary>
public class PdfLocator
{
    private readonly string _root;

    public PdfLocator(string root) => _root = root ?? string.Empty;

    /// <summary>Returns the absolute path of a matching PDF, or null if none was found.</summary>
    public string? Find(string pruefberichtsnummer)
    {
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root)) return null;
        if (string.IsNullOrWhiteSpace(pruefberichtsnummer)) return null;

        // Glob: anything containing the report number, ending in .pdf, recursive.
        var pattern = $"*{pruefberichtsnummer}*.pdf";
        try
        {
            var matches = Directory.EnumerateFiles(_root, pattern, SearchOption.AllDirectories);
            return matches.FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
