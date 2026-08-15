using System;
using System.Collections.Generic;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Portapapeles INTERNO de la config: lo que Ctrl+C / Ctrl+X dejan cargado en las pestañas Variables
/// y Comandos para que Ctrl+V lo suelte en OTRO scope.
///
/// Por qué existe habiendo drag&amp;drop y el combo de destino: los dos ya cubrían el caso, pero
/// ninguno cubre el gesto que el usuario ya tiene en los dedos. Arrastrar es sólo-mouse y falla justo
/// cuando más se necesita (cinco filas hasta un scope que quedó fuera de la vista y hay que
/// auto-scrollear); el combo obliga a elegir el destino SIN verlo en el mapa. Copiar-navegar-pegar es
/// el único de los tres donde mirás el destino antes de soltar.
///
/// ⚠ GUARDA ÍNDICES, NO CLONES DE LAS ENTRADAS — y esto es deliberado. Pegar tiene que pasar por
/// <c>ProjectStore.MoveVariables</c> / <c>MoveServices</c>, que son las dueñas de las reglas del
/// dominio (variables: todo-o-nada si el path ya existe en el destino; comandos: la copia NO se lleva
/// el puerto y sale con el primero libre). Con clones haría falta una SEGUNDA ruta de escritura al
/// store, y dos rutas que hacen "lo mismo" es exactamente cómo dos superficies terminan
/// comportándose distinto ante el mismo gesto — el bug que esta pestaña vino a evitar, no a repetir.
///
/// El precio de guardar índices es que se desactualizan: entre el Ctrl+C y el Ctrl+V el usuario puede
/// borrar, editar o mover algo en el scope de ORIGEN, y entonces el índice 3 ya no es la entrada que
/// copió. Se paga con las <see cref="Fingerprints"/>: al pegar se revalida y, si no coincide, se
/// DESCARTA con aviso. Pegar la entrada equivocada en silencio sería peor que no pegar nada.
/// </summary>
public sealed class ScopeClipboard
{
    /// <summary>
    /// Separador de campos de la huella. U+0001 justamente porque NO puede aparecer en un título, un
    /// path ni un comando tipeado a mano: con un separador "normal" (| o /) dos entradas distintas
    /// podrían producir la misma huella acomodando los campos, y la revalidación dejaría pasar la
    /// equivocada. Va como CAST y no como carácter literal en el fuente: un carácter de control crudo
    /// en un .cs sobrevive mal a cualquier reencodeo del archivo (y es invisible al leerlo).
    /// </summary>
    private const char FieldSep = (char)1;

    /// <summary>Scope del que salieron las entradas ("" global, "Espacio", "Espacio/Contexto").</summary>
    public string SourceScope { get; }

    /// <summary>Índices en la pool de <see cref="SourceScope"/>, en el orden en que se copiaron.</summary>
    public IReadOnlyList<int> Indices { get; }

    /// <summary>Huella de cada entrada al momento de copiar. Paralela a <see cref="Indices"/>.</summary>
    public IReadOnlyList<string> Fingerprints { get; }

    /// <summary>Ctrl+X (mover) en vez de Ctrl+C (copiar).</summary>
    public bool IsCut { get; }

    public int Count => Indices.Count;

    public ScopeClipboard(string sourceScope, IReadOnlyList<int> indices,
                          IReadOnlyList<string> fingerprints, bool isCut)
    {
        SourceScope = sourceScope;
        Indices = indices;
        Fingerprints = fingerprints;
        IsCut = isCut;
    }

    /// <summary>
    /// ¿El origen dejó de coincidir con lo que se copió? Recibe las huellas ACTUALES de la pool de
    /// origen, en su orden actual: si algún índice se salió de rango o su huella cambió, el
    /// portapapeles ya no apunta a lo que el usuario eligió y hay que tirarlo.
    ///
    /// Alcanza con mirar las posiciones copiadas: un cambio en OTRA fila que no corra los índices no
    /// afecta a las nuestras, y uno que sí los corra (borrar, mover) cambia la huella de al menos una
    /// de las nuestras — que es justo lo que se chequea.
    /// </summary>
    public bool IsStale(IReadOnlyList<string> currentSourceFingerprints)
    {
        for (int k = 0; k < Indices.Count; k++)
        {
            int i = Indices[k];
            if (i < 0 || i >= currentSourceFingerprints.Count) return true;
            if (!string.Equals(currentSourceFingerprints[i], Fingerprints[k], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Une los campos de una entrada en una huella comparable (ver <see cref="FieldSep"/>).</summary>
    public static string Fingerprint(params string[] fields) => string.Join(FieldSep, fields);
}
