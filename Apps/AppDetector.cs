using System;
using System.IO;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Detecta ejecutables en el sistema: por PATH o en ubicaciones típicas (con expansión de
/// variables de entorno). Es el corazón del "mix" de Abrir con: sólo ofrecemos las apps que
/// REALMENTE están instaladas en esta máquina, en vez de hardcodear rutas como el legacy.
/// </summary>
public static class AppDetector
{
    /// <summary>Primera ruta candidata que exista en disco (expande %VARS%). null si ninguna.</summary>
    public static string? FirstExisting(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            try
            {
                var f = Environment.ExpandEnvironmentVariables(c);
                if (File.Exists(f)) return f;
            }
            catch { /* candidato inválido → siguiente */ }
        }
        return null;
    }

    /// <summary>Busca un ejecutable en el PATH del sistema. Devuelve la ruta completa o null.</summary>
    public static string? InPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var f = Path.Combine(dir.Trim(), exe);
                if (File.Exists(f)) return f;
            }
            catch { /* entrada de PATH inválida → siguiente */ }
        }
        return null;
    }
}
