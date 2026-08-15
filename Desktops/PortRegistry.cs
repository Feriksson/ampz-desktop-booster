using System;
using System.Collections.Generic;
using System.Linq;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Desktops;

/// <summary>Quién se quedó con un puerto: en qué scope vive y cómo se llama la entrada.</summary>
public readonly record struct PortOwner(string ScopeLabel, string Title, int Port);

/// <summary>
/// El REGISTRO DE PUERTOS del catálogo: la garantía de que un puerto tiene UN solo dueño en toda la
/// app. Nace de un choque real — dos servicios declarando 3000 en scopes distintos: el que arrancaba
/// segundo moría (o peor, se colgaba del server del primero) y el 🟢 de AMBAS filas se ponía verde,
/// porque el estado mira el puerto y no el proceso. O sea: el modelo te MENTÍA con cara de éxito.
///
/// ⚠ El alcance es TODO EL CATÁLOGO, no sólo el conjunto que convive (scope + padre + global).
/// Es una decisión explícita y más estricta de lo que el choque técnico exige: dos espacios que
/// nunca se levantan juntos podrían repetir el 3000 sin romperse nada. Se eligió igual el barrido
/// completo porque el costo de las dos opciones es asimétrico — repartir puertos una vez
/// (Geocontrol 3000, Synxs 3001, Ampz 3002) cuesta un minuto y se hace UNA vez; el choque cuesta
/// media hora de debuggear un server que "arrancó bien" y sirve la app equivocada. Además el
/// solapamiento entre espacios deja de ser hipotético en cuanto abrís dos desks a la vez, que es
/// justamente para lo que existe esta app.
///
/// No conoce <see cref="ProjectStore"/>: recibe un enumerador de (scope, entrada). Misma separación
/// que <c>DesktopService.ProjectLookup</c> — el que consulta el registro no arrastra la persistencia.
/// </summary>
public sealed class PortRegistry
{
    private readonly Func<IEnumerable<(string ScopeLabel, ServiceEntry Entry)>> _all;

    public PortRegistry(Func<IEnumerable<(string ScopeLabel, ServiceEntry Entry)>> all) => _all = all;

    /// <summary>
    /// Dueño actual de <paramref name="port"/>, o null si está libre.
    ///
    /// <paramref name="except"/> es la entrada que se está EDITANDO y se excluye por REFERENCIA, no
    /// por valor: al editar, la entry sigue viva dentro de su pool, así que sin esto guardar un
    /// servicio sin tocarle el puerto se chocaría CONSIGO MISMO. Por referencia y no por
    /// (título+puerto) porque dos entradas pueden ser idénticas campo a campo y seguir siendo dos.
    /// </summary>
    public PortOwner? FindOwner(int port, ServiceEntry? except = null)
    {
        if (port <= 0) return null; // 0 = "no escucha ninguno": no es un puerto, no entra al registro
        foreach (var (scope, e) in _all())
            if (e.Port == port && !ReferenceEquals(e, except))
                return new PortOwner(scope, e.Title, port);
        return null;
    }

    /// <summary>
    /// Puertos que HOY aparecen más de una vez en el catálogo. Existen porque el registro llegó
    /// después que los datos: bloquear el alta no arregla lo que ya estaba guardado, y un choque
    /// preexistente es invisible justamente porque vive en otro scope que no estás mirando. Con esto
    /// la ventana los puede marcar y los limpiás cuando los cruzás.
    /// </summary>
    public HashSet<int> Duplicates()
    {
        var seen = new HashSet<int>();
        var dupes = new HashSet<int>();
        foreach (var (_, e) in _all())
            if (e.Port > 0 && !seen.Add(e.Port))
                dupes.Add(e.Port);
        return dupes;
    }

    /// <summary>
    /// Primer puerto libre desde <paramref name="from"/> hacia arriba. Un bloqueo que sólo dice "no"
    /// te deja tanteando números a mano hasta pegarle; con la sugerencia, aceptar la regla cuesta un
    /// click. Con el alcance "todo el catálogo" esto no es un lujo: es lo que la hace vivible.
    /// </summary>
    public int SuggestFree(int from)
    {
        var taken = _all().Select(t => t.Entry.Port).Where(p => p > 0).ToHashSet();
        int start = from > 0 ? from : 3000;
        for (int p = start; p <= 65535; p++)
            if (!taken.Contains(p)) return p;
        return 0; // 65535 puertos ocupados es imposible en la práctica; devolvemos "sin sugerencia"
    }
}
