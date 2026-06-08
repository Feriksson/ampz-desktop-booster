using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services.Attention;

/// <summary>
/// Ajustes de la feature de atención por desk. Se PERSISTE en %APPDATA%\AmpzDesktopBooster\attention.json.
/// La pestaña Atención de la config los edita; el AttentionService los relee en cada señal (son
/// esporádicas, no es hot path) → siempre toma lo último sin necesidad de recargas cableadas.
///
/// Mismo patrón de configs del repo: Load() con try/catch → defaults si corrupto; Save() silencioso
/// → si el disco falla, seguimos en memoria. La persistencia NUNCA voltea la app.
/// </summary>
public sealed class AttentionSettings
{
    /// <summary>Valor del combo para "no reproducir nada" en ese nivel.</summary>
    public const string NoneSound = "(Ninguno)";

    /// <summary>Sistema de atención ON/OFF entero (toast + widget + sonido). Off = la señal se ignora.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Master del sonido. Off = sin ruido, pero el toast y el widget siguen.</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>.wav (nombre, en %SystemRoot%\Media) para 'te necesita'. NoneSound = silencio para ese nivel.</summary>
    public string SoundActionNeeded { get; set; } = "Windows Pop-up Blocked.wav";

    /// <summary>.wav para 'tarea lista' (completed).</summary>
    public string SoundCompleted { get; set; } = "Windows Balloon.wav";

    /// <summary>Cuando el aviso es de TU escritorio actual: ¿mostrar el toast? Independiente del sonido.</summary>
    public bool ToastOnSameDesk { get; set; } = true;

    /// <summary>Cuando el aviso es de TU escritorio actual: ¿reproducir el sonido? Independiente del toast.</summary>
    public bool SoundOnSameDesk { get; set; } = true;

    /// <summary>Volumen 0..100 (lo aplica MediaPlayer; SoundPlayer no controla volumen).</summary>
    public int Volume { get; set; } = 100;

    // ── Sonidos disponibles del sistema (para poblar los combos) ──

    /// <summary>Los .wav de %SystemRoot%\Media (más "(Ninguno)" al inicio), ordenados alfabéticamente.</summary>
    public static List<string> AvailableSounds()
    {
        var list = new List<string> { NoneSound };
        try
        {
            string media = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "Media");
            if (Directory.Exists(media))
                list.AddRange(Directory.EnumerateFiles(media, "*.wav")
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)!
                    .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)!);
        }
        catch { /* sin acceso a la carpeta → al menos queda "(Ninguno)" */ }
        return list;
    }

    // ── Persistencia ──

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "attention.json");

    public static AttentionSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AttentionSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupto o ilegible → defaults */ }
        return new AttentionSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* permisos/disco → seguimos en memoria */ }
    }
}
