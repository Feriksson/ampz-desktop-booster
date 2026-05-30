using System.IO;
using System.Text.Json;

namespace AmpzDesktopBooster.Services;

/// <summary>Cada widget que la barra sabe mostrar. El enum es el contrato modular.</summary>
public enum WidgetKind
{
    Clock,
    Date,
    Cpu,
    Ram,
    Network,
    Battery,
}

/// <summary>
/// Qué widgets están activos. Se PERSISTE en %APPDATA%\AmpzDesktopBooster\widgets.json.
/// Un toggle que no se recuerda no sirve para nada — por eso persistimos.
/// Defaults: solo hora y RAM (lo que el usuario dijo que realmente importa).
/// </summary>
public sealed class WidgetSettings
{
    public bool Clock { get; set; } = true;
    public bool Date { get; set; } = false;
    public bool Cpu { get; set; } = false;
    public bool Ram { get; set; } = true;
    public bool Network { get; set; } = false;
    public bool Battery { get; set; } = false;

    // ---- Acceso genérico por enum: permite que el tray sea agnóstico del orden ----

    public bool Get(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => Clock,
        WidgetKind.Date => Date,
        WidgetKind.Cpu => Cpu,
        WidgetKind.Ram => Ram,
        WidgetKind.Network => Network,
        WidgetKind.Battery => Battery,
        _ => false,
    };

    public void Set(WidgetKind kind, bool value)
    {
        switch (kind)
        {
            case WidgetKind.Clock: Clock = value; break;
            case WidgetKind.Date: Date = value; break;
            case WidgetKind.Cpu: Cpu = value; break;
            case WidgetKind.Ram: Ram = value; break;
            case WidgetKind.Network: Network = value; break;
            case WidgetKind.Battery: Battery = value; break;
        }
    }

    // ---- Persistencia ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath
    {
        get
        {
            // Config de usuario va en AppData, NO junto al exe. Es lo correcto.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmpzDesktopBooster");
            return Path.Combine(dir, "widgets.json");
        }
    }

    public static WidgetSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<WidgetSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // settings corrupto o ilegible → arrancamos con defaults, no crasheamos
        }
        return new WidgetSettings();
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // si no podemos guardar (permisos, disco), seguimos en memoria igual
        }
    }
}
