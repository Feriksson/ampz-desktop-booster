using System;
using System.Collections.Generic;
using System.Linq;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Procesos anclados a un escritorio: proc.exe → NOMBRE del desk. Persiste en settings.ini [Pins].
/// Un proceso anclado, cuando aparece en otro desktop, lo mueve el <see cref="WindowGovernor"/>.
///
/// Clave por NOMBRE, no por índice — igual que la navegación (DesktopService.FindByNameFragment).
/// La identidad de un desk es su NOMBRE, no su posición: el índice es un detalle volátil de la DLL
/// que cambia cuando reordenás escritorios. Si persistiéramos el índice, mover un desk dejaría los
/// pins apuntando al desk equivocado (ese era el bug). El índice se resuelve en runtime desde el
/// nombre (FindExact) justo al mover la ventana; nunca se guarda.
/// </summary>
public sealed class PinStore
{
    // Procesos del sistema (y la propia app) que NUNCA se anclan.
    private static readonly HashSet<string> Blocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "searchui.exe", "taskmgr.exe", "systemsettings.exe", "applicationframehost.exe",
        "dwm.exe", "winlogon.exe", "csrss.exe", "svchost.exe", "ampzdesktopbooster.exe",
        "textinputhost.exe", // host invisible del shell ("Experiencia de entrada de Windows") — nunca anclar
    };

    private readonly IniFile _ini = new(AppPaths.SettingsIni);
    private readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    public PinStore() => Load();

    public bool IsBlocked(string proc) => Blocklist.Contains(proc);
    public bool IsPinned(string proc) => _pins.ContainsKey(proc);
    public bool TryGet(string proc, out string deskName) => _pins.TryGetValue(proc, out deskName!);
    public IReadOnlyDictionary<string, string> All => _pins;

    public void Pin(string proc, string deskName)
    {
        _pins[proc] = deskName;
        _ini.Write("Pins", proc, deskName);
    }

    public void Unpin(string proc)
    {
        _pins.Remove(proc);
        _ini.Delete("Pins", proc);
    }

    public void Clear()
    {
        foreach (var k in _pins.Keys.ToList()) _ini.Delete("Pins", k);
        _pins.Clear();
    }

    private void Load()
    {
        foreach (var kv in _ini.ReadSection("Pins"))
        {
            // Compat: el formato viejo guardaba el ÍNDICE (un entero) en vez del nombre. Esa identidad
            // posicional ya no sirve — la descartamos. El usuario re-ancla una vez con el modelo nuevo.
            if (int.TryParse(kv.Value, out _)) continue;
            _pins[kv.Key] = kv.Value;
        }
    }
}
