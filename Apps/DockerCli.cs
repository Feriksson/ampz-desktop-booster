using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AmpzDesktopBooster.Apps;

/// <summary>Snapshot de un contenedor para la grilla del panel Docker.</summary>
public sealed class DockerContainer
{
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public string Status { get; init; } = "";
    public string InternalPorts { get; init; } = "";
    public string ExposedPorts { get; init; } = "";
    public string Note { get; set; } = "";

    /// <summary>true si está "Up …" (corriendo) — lo usa la grilla para pintar la fila en verde.</summary>
    public bool IsRunning => Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Envoltura del CLI de docker (Win+F5). Corre `docker` por proceso y parsea la salida.
/// No depende de ninguna lib: docker tiene que estar en el PATH. Si no, IsAvailable=false.
/// </summary>
public static class DockerCli
{
    public static bool IsAvailable => AppDetector.InPath("docker.exe") is not null;

    /// <summary>`docker ps -a` parseado. Lista vacía si docker no está o falla.</summary>
    public static List<DockerContainer> List()
    {
        var result = new List<DockerContainer>();
        // Formato con tabs: Names \t Image \t Status \t Ports
        string raw = Run("ps -a --format \"{{.Names}}\\t{{.Image}}\\t{{.Status}}\\t{{.Ports}}\"");
        foreach (var line in raw.Split('\n'))
        {
            var l = line.Trim('\r', '\n', ' ');
            if (l == "") continue;
            var parts = l.Split('\t');
            string ports = parts.Length >= 4 ? parts[3] : "";
            var (intP, expP) = ParsePorts(ports);
            result.Add(new DockerContainer
            {
                Name = parts.Length >= 1 ? parts[0] : "",
                Image = parts.Length >= 2 ? parts[1] : "",
                Status = parts.Length >= 3 ? parts[2] : "",
                InternalPorts = intP,
                ExposedPorts = expP,
            });
        }
        return result;
    }

    public static void Start(IEnumerable<string> names) => RunAction("start", names);
    public static void Stop(IEnumerable<string> names) => RunAction("stop", names);

    private static void RunAction(string action, IEnumerable<string> names)
    {
        var quoted = string.Join(" ", names.Select(n => $"\"{n}\""));
        if (quoted == "") return;
        Run($"{action} {quoted}");
    }

    /// <summary>Separa "0.0.0.0:5432->5432/tcp, ..." en (internos, expuestos).</summary>
    private static (string Internal, string Exposed) ParsePorts(string portsStr)
    {
        if (string.IsNullOrWhiteSpace(portsStr)) return ("", "");
        var inner = new SortedSet<string>();
        var exposed = new SortedSet<string>();
        foreach (var mapping in portsStr.Split(','))
        {
            var m = mapping.Trim();
            int arrow = m.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                inner.Add(m[(arrow + 2)..]);
                var host = m[..arrow];
                int colon = host.LastIndexOf(':');
                if (colon >= 0 && colon < host.Length - 1)
                    exposed.Add(host[(colon + 1)..]);
            }
            else if (m != "")
            {
                inner.Add(m);
            }
        }
        return (string.Join("  ", inner), string.Join("  ", exposed));
    }

    private static string Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
