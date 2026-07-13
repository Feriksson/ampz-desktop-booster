using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// IPv4 de la red LAN de esta máquina — la que sirve para que OTRO dispositivo (el celu, otra
/// notebook) entre a un servicio local reemplazando "localhost" por esta IP. Best-effort: si no
/// hay red o falla algo, devolvemos null (el caller degrada a "no disponible", no crashea).
///
/// Reusa el mismo recorrido de <see cref="NetworkInterface.GetAllNetworkInterfaces"/> que ya usa
/// <see cref="SystemMonitor"/>. Filtramos:
///   - interfaces caídas, loopback y túneles,
///   - adaptadores VIRTUALES (Hyper-V, WSL "vEthernet", VMware, VirtualBox) por descripción — sus
///     IPs no son la de tu LAN física y confundirían al escanear desde el celu,
///   - APIPA (169.254.x.x): significa "sin DHCP", no sirve para acceso real.
/// Preferimos una IP de rango PRIVADO (10.x / 172.16-31.x / 192.168.x) que es lo que da un router
/// hogareño; si no hay, caemos a la primera IPv4 no-loopback válida.
/// </summary>
public static class LocalIp
{
    public static string? Get()
    {
        try
        {
            IPAddress? fallback = null;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var desc = (ni.Description ?? "").ToLowerInvariant();
                if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vethernet")
                    || desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("loopback"))
                    continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch (NetworkInformationException) { continue; } // interfaz "Up" sin IPv4 → tira acá

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue; // solo IPv4
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // APIPA: sin uso real

                    fallback ??= ua.Address;
                    if (IsPrivate(b)) return ua.Address.ToString(); // la ideal: LAN privada
                }
            }

            return fallback?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPrivate(byte[] b) =>
        b[0] == 10 ||
        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
        (b[0] == 192 && b[1] == 168);
}
