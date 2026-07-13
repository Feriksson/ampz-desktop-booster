using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Inspección de puertos TCP locales — responde las dos preguntas que hace el popup de servicios:
///   1) ¿Este puerto está ESCUCHANDO ahora mismo? (el 🟢/⚪ de cada fila) → <see cref="ListeningPorts"/>.
///   2) ¿Qué PROCESO lo tiene abierto? (auto-título al agregar) → <see cref="ProcessNameForPort"/>.
///
/// La (1) sale barata y sin P/Invoke con <see cref="IPGlobalProperties.GetActiveTcpListeners"/>.
/// La (2) necesita el mapa puerto→PID, que SOLO expone la API nativa GetExtendedTcpTable (iphlpapi):
/// las clases managed no dan el dueño del socket. Todo best-effort: cualquier fallo → conjunto/mapa
/// vacío (nunca tiramos la app por no poder leer la tabla TCP).
/// </summary>
internal static partial class TcpPortInfo
{
    /// <summary>Puertos TCP en estado LISTEN (IPv4 + IPv6). Para pintar el estado vivo de cada fila.</summary>
    public static HashSet<int> ListeningPorts()
    {
        var set = new HashSet<int>();
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                set.Add(ep.Port);
        }
        catch
        {
            // sin permisos / API caprichosa: devolvemos lo que haya (posiblemente vacío).
        }
        return set;
    }

    /// <summary>
    /// Nombre del proceso que escucha en <paramref name="port"/> (IPv4), o null si nadie escucha
    /// o no pudimos resolverlo. Se usa para autocompletar el título de una entrada nueva: si el
    /// usuario deja el título vacío, ponemos el proceso dueño (ej. "node", "dotnet", "Code").
    /// </summary>
    public static string? ProcessNameForPort(int port)
    {
        try
        {
            if (!ListenerPortToPid().TryGetValue(port, out int pid) || pid <= 0) return null;
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null; // el proceso pudo morir entre la lectura de la tabla y el GetProcessById.
        }
    }

    // ── Nativo: tabla TCP con PID dueño (solo listeners, IPv4) ─────────────────

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;  // puerto en los 2 bytes bajos, en ORDEN DE RED (big-endian)
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion, int tblClass, int reserved);

    /// <summary>Mapa puerto→PID de todos los sockets IPv4 en LISTEN. Vacío ante cualquier error.</summary>
    private static Dictionary<int, int> ListenerPortToPid()
    {
        var map = new Dictionary<int, int>();

        int bufLen = 0;
        // 1ra llamada con buffer 0: nos dice cuánto hace falta (devuelve ERROR_INSUFFICIENT_BUFFER).
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref bufLen, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (res != ERROR_INSUFFICIENT_BUFFER || bufLen <= 0) return map;

        IntPtr buf = Marshal.AllocHGlobal(bufLen);
        try
        {
            res = GetExtendedTcpTable(buf, ref bufLen, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (res != NO_ERROR) return map;

            // Layout: DWORD dwNumEntries; seguido de dwNumEntries filas MIB_TCPROW_OWNER_PID.
            int num = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + sizeof(int);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < num; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                // localPort viene en orden de red en los 2 bytes bajos → ntohs manual.
                int port = ((int)(row.localPort & 0xFF) << 8) | (int)((row.localPort >> 8) & 0xFF);
                map[port] = (int)row.owningPid; // el último gana si hay dup (ej. 0.0.0.0 y ::) — da igual, mismo pid
                rowPtr += rowSize;
            }
        }
        catch
        {
            return map;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return map;
    }
}
