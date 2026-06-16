using System.Collections.Generic;
using System.Linq;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Una "pool" de variables (paths o URLs) sobre una lista concreta de <see cref="PathEntry"/>.
/// Abstrae de DÓNDE viven los datos: la misma clase envuelve la pool de un proyecto
/// (<c>_data.Paths[proyecto]</c>) o la pool GLOBAL compartida (<c>_data.SharedPaths</c>).
/// El legacy resolvía esto con closures; acá es un objeto con un callback de guardado.
///
/// Toda mutación persiste de inmediato (llama a <c>save</c>). Hay como mucho UN predeterminado
/// por pool: marcar uno limpia el resto.
/// </summary>
public sealed class PathPool
{
    private readonly List<PathEntry> _entries;
    private readonly System.Action _save;

    /// <summary>Etiqueta para el header del diálogo: el nombre del proyecto o "Global".</summary>
    public string Label { get; }

    public PathPool(List<PathEntry> entries, System.Action save, string label)
    {
        _entries = entries;
        _save = save;
        Label = label;
    }

    public IReadOnlyList<PathEntry> Entries => _entries;

    public void Add(string title, string path)
    {
        _entries.Add(new PathEntry { Title = title.Trim(), Path = path.Trim() });
        _save();
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries.RemoveAt(index);
        _save();
    }

    /// <summary>
    /// Borra varias entries por índice en una sola pasada (un solo <c>save</c>). Lo usa el "purgar
    /// rotos" del Paths Manager: si borrásemos uno por uno con <see cref="Delete"/> reescribiríamos
    /// el JSON N veces. Se remueve de mayor a menor para que los índices no se corran al quitar.
    /// </summary>
    public void DeleteMany(IEnumerable<int> indices)
    {
        foreach (var i in indices.Distinct().OrderByDescending(i => i))
            if (i >= 0 && i < _entries.Count)
                _entries.RemoveAt(i);
        _save();
    }

    public void UpdateTitle(int index, string title)
    {
        if (index < 0 || index >= _entries.Count) return;
        title = title.Trim();
        // Si el título queda vacío, usamos el path como título (igual que el legacy).
        _entries[index].Title = title == "" ? _entries[index].Path : title;
        _save();
    }

    /// <summary>Marca/desmarca el predeterminado (toggle). Sólo 1 por pool: marcar uno limpia el resto.</summary>
    public void ToggleDefault(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        bool wasDefault = _entries[index].Default;
        foreach (var e in _entries) e.Default = false;
        _entries[index].Default = !wasDefault;
        _save();
    }

    /// <summary>Índice del predeterminado, o -1 si no hay.</summary>
    public int DefaultIndex => _entries.FindIndex(e => e.Default);
}
