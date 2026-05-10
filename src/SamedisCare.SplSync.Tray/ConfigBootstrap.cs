using System.IO;
using System.Reflection;

namespace SamedisCare.SplSync.Tray;

/// <summary>
/// First-run bootstrap: stellt sicher, dass beim Start eine config.yml neben der EXE liegt.
///
/// Hintergrund: Wir liefern nur die EXE aus. Operations soll keine Begleitdateien
/// hinterherlegen müssen. Die Vorlage <c>config.yml.example</c> ist als
/// <c>EmbeddedResource</c> in die Assembly eingebettet (siehe Tray.csproj).
///
/// Beim ersten Start:
///   1) Wenn config.yml fehlt -> aus Resource extrahieren -> <see cref="WasCreated"/> = true.
///   2) Wenn config.yml existiert -> nichts tun.
///
/// App.xaml.cs liest <see cref="WasCreated"/> und öffnet in dem Fall den Settings-Tab,
/// damit der User die Defaults gleich anpassen kann.
/// </summary>
public static class ConfigBootstrap
{
    /// <summary>
    /// LogicalName, unter dem die Resource in die Assembly eingebunden ist
    /// (siehe Tray.csproj &lt;EmbeddedResource Include="config.yml.example"&gt;).
    /// </summary>
    private const string ResourceName = "SamedisCare.SplSync.Tray.config.yml.example";

    /// <summary>
    /// Stellt sicher, dass <paramref name="configPath"/> existiert. Liefert true, wenn die
    /// Datei in diesem Aufruf neu erzeugt wurde (= der User sollte den Settings-Dialog sehen).
    /// </summary>
    public static bool EnsureConfigExists(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("configPath darf nicht leer sein.", nameof(configPath));

        if (File.Exists(configPath)) return false;

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var template = ReadEmbeddedTemplate();
        File.WriteAllText(configPath, template);
        return true;
    }

    /// <summary>
    /// Liest die eingebettete config.yml.example. Wirft eine sprechende Exception, wenn
    /// die Resource fehlt — sehr unwahrscheinlich, weil die csproj sie immer mitbaut,
    /// aber wir wollen keine NullReferenceException irgendwo im Renderer.
    /// </summary>
    private static string ReadEmbeddedTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            // Fallback: vielleicht hat das Build-System einen anderen LogicalName erzeugt.
            // Suche nach der ersten Resource, die auf 'config.yml.example' endet.
            var fallbackName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("config.yml.example", StringComparison.OrdinalIgnoreCase));
            if (fallbackName == null)
                throw new InvalidOperationException(
                    $"Embedded resource '{ResourceName}' fehlt — bitte Tray.csproj prüfen " +
                    "(<EmbeddedResource Include=\"config.yml.example\" />).");

            using var fbStream = asm.GetManifestResourceStream(fallbackName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{fallbackName}' konnte nicht geöffnet werden.");
            using var fbReader = new StreamReader(fbStream);
            return fbReader.ReadToEnd();
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
