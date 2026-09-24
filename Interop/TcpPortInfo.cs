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
            int pid = PidForPort(port);
            if (pid <= 0) return null;
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null; // el proceso pudo morir entre la lectura de la tabla y el GetProcessById.
        }
    }

    /// <summary>
    /// PID del proceso que tiene el puerto en LISTEN, o 0 si nadie lo tiene (o no pudimos leerlo).
    /// Lo necesita "Matar" del popup de servicios: cerrar la terminal NO siempre mata al server —
    /// `artisan serve` y `npm run dev` lanzan un NIETO a través de un cmd.exe, y ese nieto puede
    /// quedar huérfano con el puerto tomado. Sin el dueño no hay a quién matar.
    /// </summary>
    public static int PidForPort(int port) =>
        ListenerPortToPid().TryGetValue(port, out int pid) && pid > 0 ? pid : 0;

    // ── Nativo: tabla TCP con PID dueño (solo listeners, IPv4 + IPv6) ──────────

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
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

    /// <summary>
    /// Fila IPv6. NO es la misma estructura que la v4 (la dirección son 16 bytes + scope id), así que
    /// no se puede leer la tabla v6 con el struct de v4: los campos saldrían corridos y el puerto
    /// leería basura. Las direcciones no se usan —sólo queremos puerto y dueño—, pero tienen que
    /// estar declaradas para que el offset del resto caiga donde corresponde.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        public uint localAddr0, localAddr1, localAddr2, localAddr3;
        public uint localScopeId;
        public uint localPort;   // igual que en v4: ORDEN DE RED en los 2 bytes bajos
        public uint remoteAddr0, remoteAddr1, remoteAddr2, remoteAddr3;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion, int tblClass, int reserved);

    /// <summary>
    /// Mapa puerto→PID de todos los sockets en LISTEN. Vacío ante cualquier error.
    ///
    /// Barre las DOS familias, y eso NO es completismo: <see cref="ListeningPorts"/> (la que pinta el
    /// puntito) es managed y ve v4 y v6 por igual, así que un dev server que bindea sólo en <c>::</c>
    /// —Vite y Node lo hacen seguido— se veía verde pero quedaba SIN dueño acá. El botón de matar
    /// habría dicho "nadie escucha ese puerto" con el punto verde al lado: la peor combinación
    /// posible, porque el usuario deja de creerle a las dos superficies.
    /// </summary>
    private static Dictionary<int, int> ListenerPortToPid()
    {
        var map = new Dictionary<int, int>();
        AddListeners(map, AF_INET);
        AddListeners(map, AF_INET6);
        return map;
    }

    private static void AddListeners(Dictionary<int, int> map, int family)
    {
        int bufLen = 0;
        // 1ra llamada con buffer 0: nos dice cuánto hace falta (devuelve ERROR_INSUFFICIENT_BUFFER).
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref bufLen, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (res != ERROR_INSUFFICIENT_BUFFER || bufLen <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(bufLen);
        try
        {
            res = GetExtendedTcpTable(buf, ref bufLen, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (res != NO_ERROR) return;

            // Layout: DWORD dwNumEntries; seguido de dwNumEntries filas del struct de esa familia.
            int num = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + sizeof(int);
            bool v6 = family == AF_INET6;
            int rowSize = v6 ? Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>() : Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < num; i++)
            {
                uint rawPort, pid;
                if (v6)
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    rawPort = row.localPort; pid = row.owningPid;
                }
                else
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rawPort = row.localPort; pid = row.owningPid;
                }

                // localPort viene en orden de red en los 2 bytes bajos → ntohs manual.
                int port = ((int)(rawPort & 0xFF) << 8) | (int)((rawPort >> 8) & 0xFF);
                // El primero gana: si el mismo proceso bindeó 0.0.0.0 y ::, da igual cuál anotemos,
                // pero si son procesos DISTINTOS (dual-stack a medias), nos quedamos con el IPv4 —
                // que es el que casi siempre atiende al browser en localhost.
                if (port > 0 && pid > 0) map.TryAdd(port, (int)pid);
                rowPtr += rowSize;
            }
        }
        catch
        {
            // tabla ilegible: devolvemos lo que hayamos juntado hasta acá.
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
