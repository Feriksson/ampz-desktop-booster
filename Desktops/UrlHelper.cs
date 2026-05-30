using System.Text.RegularExpressions;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Detecta y normaliza URLs. Port del <c>_IsUrl()</c>/<c>_NormalizeUrl()</c> del legacy:
/// una variable puede ser un path del filesystem o una URL, y hay que distinguirlas SIN
/// exigir que la URL traiga esquema (la gente escribe "google.com", no "https://google.com").
/// </summary>
public static partial class UrlHelper
{
    [GeneratedRegex(@"^(https?|ftp)://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRx();

    [GeneratedRegex(@"^localhost(:\d+)?(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalhostRx();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}(:\d+)?(/.*)?$")]
    private static partial Regex Ipv4Rx();

    // dominio pelado: token(.token)+ con path opcional — google.com, foo.io/bar, sub.dominio.com.ar
    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareDomainRx();

    /// <summary>true si parece una URL. Descarta lo que sea claramente filesystem.</summary>
    public static bool IsUrl(string s)
    {
        s = s.Trim();
        if (s == "") return false;

        // Marcadores de filesystem → NO es URL.
        if (s.Contains('\\')) return false;                              // backslash (Windows paths, UNC)
        if (s.StartsWith("//")) return false;                            // UNC con forward slashes
        if (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':') return false; // C:\ o C:/

        if (SchemeRx().IsMatch(s)) return true;
        if (s.StartsWith("www.", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (LocalhostRx().IsMatch(s)) return true;
        if (Ipv4Rx().IsMatch(s)) return true;
        if (BareDomainRx().IsMatch(s)) return true;
        return false;
    }

    /// <summary>Antepone http:// si la URL no trae esquema. Asume que ya pasó <see cref="IsUrl"/>.</summary>
    public static string Normalize(string s)
    {
        s = s.Trim();
        return SchemeRx().IsMatch(s) ? s : "http://" + s;
    }
}
