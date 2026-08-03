using System;
using System.Collections.Generic;
using System.Linq;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Restricciones por escritorio: un desk restringido sólo admite apps de su whitelist; el resto
/// se manda a MAIN. Persiste en settings.ini: [Restricted] NombreDesk=1 y [Whitelist_NombreDesk] proc.exe=1.
///
/// Clave por NOMBRE, no por índice — igual que PinStore y la navegación. La identidad del desk es su
/// nombre, no su posición; reordenar escritorios ya no rompe las restricciones. El índice volátil se
/// resuelve en runtime desde el nombre cuando hay que mover una ventana, nunca se persiste.
/// </summary>
public sealed class RestrictionStore
{
    // Procesos que nunca se mueven por restricción (sistema + la propia app).
    private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        "ampzdesktopbooster.exe", "explorer.exe", "dwm.exe", "winlogon.exe", "csrss.exe", "svchost.exe",
        "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe", "applicationframehost.exe",
        // textinputhost.exe = "Experiencia de entrada de Windows" (Windows Input Experience): host
        // invisible del shell (teclado táctil/emojis/IME). Reporta IsWindowVisible=true aunque esté
        // cloaked, así que sin esto el scan lo manda a MAIN. Mismo grupo que los de arriba.
        "textinputhost.exe",
    };

    private readonly IniFile _ini = new(AppPaths.SettingsIni);
    private readonly HashSet<string> _restricted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _whitelists = new(StringComparer.OrdinalIgnoreCase);

    public RestrictionStore() => Load();

    public bool IsExempt(string proc) => Exempt.Contains(proc);
    public bool IsRestricted(string deskName) => _restricted.Contains(deskName);

    /// <summary>
    /// Un desk es "restringible" si NO es MAIN y NO es un "DESK +N" (esos son de espacio).
    /// CONSOLES, MISCS y similares califican. Misma regla que el legacy.
    /// </summary>
    public static bool IsRestrictable(string deskName) =>
        deskName != "" &&
        !deskName.Contains("MAIN", StringComparison.OrdinalIgnoreCase) &&
        !deskName.Contains("DESK +", StringComparison.OrdinalIgnoreCase);

    public void SetRestricted(string deskName, bool on)
    {
        if (on)
        {
            _restricted.Add(deskName);
            _ini.Write("Restricted", deskName, "1");
            if (!_whitelists.ContainsKey(deskName))
                _whitelists[deskName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _restricted.Remove(deskName);
            _ini.Delete("Restricted", deskName);
        }
    }

    public bool IsWhitelisted(string deskName, string proc) =>
        _whitelists.TryGetValue(deskName, out var set) && set.Contains(proc);

    public int WhitelistCount(string deskName) =>
        _whitelists.TryGetValue(deskName, out var set) ? set.Count : 0;

    public void AddToWhitelist(string deskName, string proc)
    {
        if (!_whitelists.TryGetValue(deskName, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _whitelists[deskName] = set;
        }
        set.Add(proc);
        _ini.Write("Whitelist_" + deskName, proc, "1");
    }

    /// <summary>Procesos permitidos en un desk (vacío si no tiene). Para gestionar la whitelist desde la config.</summary>
    public IReadOnlyCollection<string> Whitelist(string deskName) =>
        _whitelists.TryGetValue(deskName, out var set) ? set : Array.Empty<string>();

    public void RemoveFromWhitelist(string deskName, string proc)
    {
        if (_whitelists.TryGetValue(deskName, out var set))
            set.Remove(proc);
        _ini.Delete("Whitelist_" + deskName, proc);
    }

    private void Load()
    {
        foreach (var kv in _ini.ReadSection("Restricted"))
        {
            // Compat: el formato viejo usaba el ÍNDICE como clave (un entero). Identidad posicional,
            // ya inválida — la descartamos. El usuario re-protege una vez con el modelo por nombre.
            if (int.TryParse(kv.Key, out _)) continue;
            _restricted.Add(kv.Key);
        }

        foreach (var name in _restricted.ToList())
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _ini.ReadSection("Whitelist_" + name))
                set.Add(kv.Key);
            _whitelists[name] = set;
        }
    }
}
