using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AmpzDesktopBooster.Persistence;

/// <summary>
/// Lector/escritor de archivos INI — el equivalente de IniRead/IniWrite/IniDelete del legacy.
/// .NET no trae INI nativo, así que parseamos y reescribimos el archivo entero en cada op.
/// El volumen es bajo (config de usuario), así que la simplicidad gana sobre la performance.
///
/// Formato: secciones [Nombre], pares clave=valor, comentarios con ; (se descartan al reescribir).
/// </summary>
public sealed class IniFile
{
    private readonly string _path;

    public IniFile(string path) => _path = path;

    /// <summary>Valor de una clave, o <paramref name="fallback"/> si no existe la sección/clave.</summary>
    public string Read(string section, string key, string fallback = "")
    {
        var sec = ReadSection(section);
        return sec.TryGetValue(key, out var v) ? v : fallback;
    }

    /// <summary>Todos los pares clave=valor de una sección (vacío si no existe).</summary>
    public Dictionary<string, string> ReadSection(string section)
    {
        var model = Parse();
        var found = model.FirstOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        return found.Pairs is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : found.Pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public void Write(string section, string key, string value)
    {
        var model = Parse();
        var sec = model.FirstOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (sec.Pairs is null)
        {
            sec = (section, new List<KeyValuePair<string, string>>());
            model.Add(sec);
        }

        int i = sec.Pairs.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) sec.Pairs[i] = new(key, value);
        else sec.Pairs.Add(new(key, value));

        WriteAll(model);
    }

    public void Delete(string section, string key)
    {
        var model = Parse();
        var sec = model.FirstOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (sec.Pairs is null) return;
        sec.Pairs.RemoveAll(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        WriteAll(model);
    }

    // ── Parse / serialize ─────────────────────────────────────────────────────

    private List<(string Name, List<KeyValuePair<string, string>> Pairs)> Parse()
    {
        var model = new List<(string, List<KeyValuePair<string, string>>)>();
        if (!File.Exists(_path)) return model;

        List<KeyValuePair<string, string>>? current = null;
        foreach (var raw in File.ReadAllLines(_path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new List<KeyValuePair<string, string>>();
                model.Add((name, current));
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            current.Add(new(key, val));
        }
        return model;
    }

    private void WriteAll(List<(string Name, List<KeyValuePair<string, string>> Pairs)> model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var w = new StreamWriter(_path, append: false);
        foreach (var (name, pairs) in model)
        {
            w.WriteLine($"[{name}]");
            foreach (var p in pairs)
                w.WriteLine($"{p.Key}={p.Value}");
            w.WriteLine();
        }
    }
}
