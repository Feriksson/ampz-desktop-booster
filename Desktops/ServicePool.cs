using System;
using System.Collections.Generic;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Una "pool" de servicios sobre una lista concreta de <see cref="ServiceEntry"/>. Mismo patrón
/// exacto que <see cref="PathPool"/> — abstrae de DÓNDE viven los datos: la misma clase envuelve la
/// pool de un scope (<c>_data.Services["Espacio/Contexto"]</c>) o la GLOBAL (<c>_data.SharedServices</c>),
/// y toda mutación persiste de inmediato llamando al callback de guardado.
///
/// Se copia la forma de PathPool a propósito y no se "generaliza" las dos en una clase con genéricos:
/// las entradas no comparten campos (una tiene Path, la otra Command/WorkDir/Port) y el molde común
/// terminaría siendo una lista con un save() — o sea, nada. Dos clases chatas y legibles le ganan a
/// una abstracción que no abstrae.
/// </summary>
public sealed class ServicePool
{
    private readonly List<ServiceEntry> _entries;
    private readonly Action _save;

    /// <summary>Etiqueta para el header y los rótulos de sección: el scope bonito, o "Global".</summary>
    public string Label { get; }

    public ServicePool(List<ServiceEntry> entries, Action save, string label)
    {
        _entries = entries;
        _save = save;
        Label = label;
    }

    public IReadOnlyList<ServiceEntry> Entries => _entries;

    public void Add(string title, string command, string workDir, int port, bool? autoStart = null)
    {
        _entries.Add(new ServiceEntry
        {
            Title = title.Trim(),
            Command = command.Trim(),
            WorkDir = workDir.Trim(),
            Port = port,
            AutoStart = autoStart,
        });
        _save();
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries.RemoveAt(index);
        _save();
    }

    /// <summary>Reescribe una entrada completa (la edición es de los 4 campos a la vez, en un diálogo).</summary>
    public void Update(int index, string title, string command, string workDir, int port, bool? autoStart)
    {
        if (index < 0 || index >= _entries.Count) return;
        var e = _entries[index];
        e.Title = title.Trim();
        e.Command = command.Trim();
        e.WorkDir = workDir.Trim();
        e.Port = port;
        e.AutoStart = autoStart;
        _save();
    }
}
