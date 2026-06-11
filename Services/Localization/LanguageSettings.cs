using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Localization;

/// <summary>
/// Preferencia de idioma persistida en <c>language.json</c> (%APPDATA%). Mismo patrón resiliente que
/// el resto de las configs: <see cref="Load"/> con fallback a default si el archivo falta o está
/// corrupto; <see cref="Save"/> silencioso. Un fallo de disco NUNCA voltea la app.
///
/// Default la PRIMERA vez (sin archivo): se detecta por el idioma del SO — español si el sistema está
/// en español, inglés en cualquier otro caso. Así un usuario angloparlante la ve en inglés de entrada.
/// </summary>
public sealed class LanguageSettings
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "language.json");

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("language")]
    public AppLanguage Language { get; set; } = DetectDefault();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Idioma por defecto según el SO: español si el sistema está en español, si no inglés.</summary>
    private static AppLanguage DetectDefault() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Spanish
            : AppLanguage.English;

    public static LanguageSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<LanguageSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupto → default por SO, no crasheamos */ }
        return new LanguageSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* disco/permisos → seguimos en memoria */ }
    }
}
