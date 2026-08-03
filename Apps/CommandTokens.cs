using System;
using System.Text.RegularExpressions;

namespace AmpzDesktopBooster.Apps;

/// <summary>Por qué un token no se pudo resolver — el caller lo traduce a un mensaje que orienta.</summary>
public enum TokenResult
{
    Ok,
    /// <summary>Se usó <c>{ip}</c> pero no hay IP de LAN (sin red / sólo adaptadores virtuales).</summary>
    NoNetwork,
    /// <summary>Se usó <c>{port}</c> pero el servicio no declara puerto.</summary>
    NoPort,
}

/// <summary>
/// Atajos que se expanden en el COMANDO de un servicio justo antes de lanzarlo:
///   · <c>{ip}</c>   → la IPv4 de LAN de ESTE momento (<see cref="Services.LocalIp"/>)
///   · <c>{port}</c> → el puerto que ya declara el propio servicio
///
/// Existe por un dolor concreto: un dev server que hay que exponer a la LAN
/// (<c>npm run dev -- --port 5175 --strictPort --host 192.168.0.15</c>) trae la IP HARDCODEADA, y esa
/// IP la rota el DHCP, o cambia sola al saltar de WiFi a cable. Un comando guardado con la IP de ayer
/// no falla ruidosamente: levanta escuchando en una interfaz que ya no existe, y recién te enterás
/// cuando el celu no entra. El token mata la clase entera de bug — se resuelve EN CADA LANZADA, nunca
/// se persiste el valor.
///
/// <c>{port}</c> va por el mismo motivo pero contra otra deriva: el puerto ya vive en el servicio
/// (es lo que le da el estado 🟢/⚪), así que repetirlo tipeado en el comando crea DOS fuentes de
/// verdad — cambiás una, la otra queda mintiendo y el puntito te marca apagado un server que sí corre.
///
/// Es deliberadamente CHICO y CERRADO (dos tokens, sin sintaxis de default ni condicionales): no es un
/// motor de templates. La sintaxis <c>{x}</c> copia a propósito la del <c>{path}</c> de los args de
/// apps.json — un solo vocabulario de sustitución en toda la app.
///
/// Es PURO: la IP se le PASA, no la busca. Así el mismo código sirve para lanzar y para el preview en
/// vivo del editor (que cachea la IP y no escanea las interfaces de red en cada tecla).
/// </summary>
public static partial class CommandTokens
{
    [GeneratedRegex(@"\{(ip|port)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRx();

    /// <summary>¿Vale la pena resolver la IP / mostrar el preview? Evita el escaneo de NICs al pedo.</summary>
    public static bool HasTokens(string command) => TokenRx().IsMatch(command);

    /// <summary>
    /// Reemplaza los tokens. Best-effort: si uno no se puede resolver, ese token queda TAL CUAL en el
    /// texto (no se borra ni se vacía) y el resultado dice por qué. Dejarlo visible es lo correcto —
    /// borrarlo produciría un comando que "casi anda" (<c>--host</c> sin valor se come el flag
    /// siguiente) y el error saldría a kilómetros de la causa.
    /// </summary>
    public static TokenResult Expand(string command, int port, string? ip, out string expanded)
    {
        expanded = command;
        if (!HasTokens(command)) return TokenResult.Ok;

        var status = TokenResult.Ok;
        expanded = TokenRx().Replace(command, m =>
        {
            if (m.Groups[1].Value.Equals("ip", StringComparison.OrdinalIgnoreCase))
            {
                if (ip is null) { status = TokenResult.NoNetwork; return m.Value; }
                return ip;
            }

            if (port <= 0) { status = TokenResult.NoPort; return m.Value; }
            return port.ToString();
        });
        return status;
    }
}
