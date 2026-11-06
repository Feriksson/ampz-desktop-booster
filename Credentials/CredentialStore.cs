using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Credentials;

/// <summary>
/// Credenciales por SCOPE, con la misma key que las variables (<see cref="ProjectStore.ScopeKey"/>:
/// "Espacio" o "Espacio/Contexto"; la global es <see cref="ProjectStore.GlobalScope"/>). La herencia
/// contexto → espacio → global NO vive acá: el store guarda listas planas por key y la ventana arma
/// la cadena, igual que con los pools de variables.
///
/// Persistencia — por qué así y no como los demás stores:
///   - El archivo entero es UN blob DPAPI (<see cref="DataProtectionScope.CurrentUser"/>): lo abre
///     sólo esta cuenta de Windows en esta máquina, sin contraseña maestra (decisión del usuario: el
///     desbloqueo ES la sesión de Windows). Cifrar el blob entero y no campo por campo hace que ni
///     los títulos ni la estructura (qué clientes tienen credenciales) queden legibles.
///   - Se cifra ANTES de tocar el disco: <see cref="SafeFile.WriteAllBytes"/> recibe el blob, así
///     que ni el <c>.tmp</c> ni el <c>.bak</c> llevan jamás texto plano.
///   - Un blob que no descifra cuenta como dañado: <see cref="SafeFile.LoadBytes{T}"/> lo APARTA
///     (nunca lo borra) y avisa con el nombre del archivo y nada más.
///
/// Ningún mensaje, toast ni excepción de acá lleva un secreto: los catch son silenciosos a propósito
/// (la persistencia nunca voltea la app, y un mensaje de error es el lugar más fácil para filtrar).
///
/// La vive <see cref="ProjectStore"/> (propiedad <c>Credentials</c>), igual que las notas sueltas:
/// las reorganizaciones del catálogo la arrastran en el mismo acto, sin que nadie se pueda olvidar.
/// </summary>
public sealed class CredentialStore
{
    // Entropía propia de la app: DPAPI con CurrentUser lo puede abrir CUALQUIER proceso de esta
    // cuenta. La entropía no es un secreto fuerte, pero obliga a que quien lo intente sepa que es
    // nuestro formato — cerrar la puerta a un "Unprotect a todo lo que encuentre" genérico.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AmpzDesktopBooster.Credentials.v1");

    // Case-INSENSITIVE a propósito: los nombres de espacio/contexto se comparan así en todo el
    // dominio (ProjectStore.Same). Con un dict sensible, "Synxs" y "synxs" serían dos bolsas de
    // credenciales y una reorganización movería una dejando la otra huérfana e invisible.
    private readonly Dictionary<string, List<Credential>> _byScope;

    private CredentialStore(Dictionary<string, List<Credential>> byScope) => _byScope = byScope;

    public static CredentialStore Load()
    {
        Dictionary<string, List<Credential>>? raw = null;
        try { raw = SafeFile.LoadBytes(AppPaths.CredentialsFile, Decrypt); }
        catch { /* nunca tumbamos la app por la persistencia */ }
        return new CredentialStore(Normalize(raw));
    }

    private static Dictionary<string, List<Credential>>? Decrypt(byte[] blob)
    {
        // Unprotect tira CryptographicException si el blob no es nuestro o está roto: SafeFile lo
        // toma como archivo dañado. El texto plano se pisa en cuanto deja de hacer falta.
        byte[] plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Dictionary<string, List<Credential>>>(plain); }
        finally { Array.Clear(plain); }
    }

    /// <summary>
    /// Pasa lo leído a un dict case-insensitive (fusionando keys que difieren sólo en mayúsculas) y
    /// repara lo que rompería la identidad de una fila: id vacío o repetido, listas/strings nulos.
    /// </summary>
    private static Dictionary<string, List<Credential>> Normalize(Dictionary<string, List<Credential>>? raw)
    {
        var result = new Dictionary<string, List<Credential>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Guid>();
        foreach (var (key, list) in raw ?? new())
        {
            if (key is null || list is null) continue;
            foreach (var c in list.Where(c => c is not null))
            {
                if (c.Id == Guid.Empty || !seen.Add(c.Id)) { c.Id = Guid.NewGuid(); seen.Add(c.Id); }
                c.Title ??= ""; c.User ??= ""; c.Password ??= ""; c.Url ??= ""; c.Note ??= "";
                c.Fields = (c.Fields ?? new()).Where(f => f is not null).ToList();
                foreach (var f in c.Fields) { f.Name ??= ""; f.Value ??= ""; }
                // Un "provider" desconocido NO se toca acá: se lee como Other (CredentialProviders.FromKey)
                // y recién se reescribe si el usuario edita esa credencial.
                c.NormalizeLinks();
                ListFor(result, key).Add(c);
            }
        }
        return result;
    }

    private void Save()
    {
        byte[]? plain = null;
        try
        {
            // Las listas vacías no se escriben: un scope que se quedó sin credenciales no tiene por
            // qué dejar su nombre adentro del archivo.
            var payload = _byScope.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            plain = JsonSerializer.SerializeToUtf8Bytes(payload);
            byte[] blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            SafeFile.WriteAllBytes(AppPaths.CredentialsFile, blob);
        }
        catch { /* disco/permisos/DPAPI → seguimos en memoria; sin mensaje: no hay nada seguro que decir */ }
        finally { if (plain is not null) Array.Clear(plain); }
    }

    private static List<Credential> ListFor(Dictionary<string, List<Credential>> dict, string scopeKey)
    {
        if (!dict.TryGetValue(scopeKey, out var list))
        {
            list = new List<Credential>();
            dict[scopeKey] = list;
        }
        return list;
    }

    // ── Lectura: siempre COPIAS (el store es el único que escribe, ver Credential.Clone) ──────

    /// <summary>Las credenciales PROPIAS de un scope (sin herencia). Lista vacía si no tiene.</summary>
    public IReadOnlyList<Credential> Get(string scopeKey) =>
        _byScope.TryGetValue(scopeKey ?? "", out var list) ? list.Select(c => c.Clone()).ToList() : new List<Credential>();

    /// <summary>Todo, por scope: para buscar en OTROS espacios desde la ventana.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Credential>> GetAll() =>
        _byScope.Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Credential>)kv.Value.Select(c => c.Clone()).ToList(),
                              StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Busca una credencial por id en TODOS los scopes (los vínculos "inicia sesión con" cruzan
    /// espacios). Devuelve dónde vive y una copia; null si no existe — un vínculo colgado, porque la
    /// cuenta se borró, se informa como tal en la UI en vez de reescribir a quienes la usaban.
    /// </summary>
    public (string ScopeKey, Credential Credential)? Find(Guid id)
    {
        foreach (var (key, list) in _byScope)
            if (list.FirstOrDefault(c => c.Id == id) is { } c) return (key, c.Clone());
        return null;
    }

    /// <summary>
    /// El vínculo AL REVÉS: quiénes inician sesión con <paramref name="id"/> ("La usan: Resend,
    /// Vercel…"), con su scope. Se deriva recorriendo todo y no se guarda en la cuenta de identidad:
    /// dos listas que dicen lo mismo desde dos lados se desincronizan en la primera edición.
    /// </summary>
    public IReadOnlyList<(string ScopeKey, Credential Credential)> UsedBy(Guid id) =>
        _byScope.SelectMany(kv => kv.Value.Where(c => c.SignInWith.Contains(id)).Select(c => (kv.Key, c.Clone())))
                .ToList();

    // ── Escritura: cada mutación se guarda en el acto (no hay "guardar al cerrar") ─────────────

    /// <summary>Alta. Devuelve la copia guardada (con su id y fechas definitivas).</summary>
    public Credential Add(string scopeKey, Credential credential)
    {
        var c = credential.Clone();
        // Un id repetido haría que Update/Delete tocaran la credencial equivocada.
        if (c.Id == Guid.Empty || FindById(c.Id) is not null) c.Id = Guid.NewGuid();
        c.CreatedAt = c.UpdatedAt = DateTime.UtcNow;
        c.NormalizeLinks(); // después de fijar el id: el "nunca a sí misma" compara contra el definitivo
        ListFor(_byScope, scopeKey ?? "").Add(c);
        Save();
        return c.Clone();
    }

    /// <summary>Reemplaza la credencial con ese id DENTRO del scope. false si no está ahí.</summary>
    public bool Update(string scopeKey, Credential credential)
    {
        if (!_byScope.TryGetValue(scopeKey ?? "", out var list)) return false;
        int at = list.FindIndex(c => c.Id == credential.Id);
        if (at < 0) return false;

        var c = credential.Clone();
        c.CreatedAt = list[at].CreatedAt; // el alta no la reescribe una edición
        c.UpdatedAt = DateTime.UtcNow;
        c.NormalizeLinks();
        list[at] = c;
        Save();
        return true;
    }

    /// <summary>
    /// Cambia una credencial de scope (el selector de scope del editor). No re-fecha: cambiarla de
    /// lugar no es editar su contenido.
    /// </summary>
    public bool Move(Guid id, string fromScope, string toScope)
    {
        fromScope ??= ""; toScope ??= "";
        if (!_byScope.TryGetValue(fromScope, out var src)) return false;
        var c = src.FirstOrDefault(x => x.Id == id);
        if (c is null) return false;
        if (string.Equals(fromScope, toScope, StringComparison.OrdinalIgnoreCase)) return true;

        src.Remove(c);
        ListFor(_byScope, toScope).Add(c);
        Save();
        return true;
    }

    /// <summary>
    /// Borra SÓLO esa credencial. Los <see cref="Credential.SignInWith"/> de otras que la apuntaban NO
    /// se reescriben: quedan colgados y la UI los muestra como "(cuenta eliminada)". Limpiarlos en
    /// silencio borraría el dato de que esa cuenta entraba con Google — justo lo que hace falta saber
    /// para volver a cargar la identidad; la confirmación de borrado ya avisa cuántas dependen.
    /// </summary>
    public bool Delete(string scopeKey, Guid id)
    {
        if (!_byScope.TryGetValue(scopeKey ?? "", out var list) || list.RemoveAll(c => c.Id == id) == 0) return false;
        Save();
        return true;
    }

    private Credential? FindById(Guid id) =>
        _byScope.Values.SelectMany(l => l).FirstOrDefault(c => c.Id == id);

    // ── Seguir las reorganizaciones del catálogo (las llama ProjectStore) ─────────────────────

    /// <summary>
    /// Aplica a cada key de scope el MISMO mapeo (espacio, contexto) → (espacio, contexto) que
    /// ProjectStore aplica a la sesión, las sugerencias, los desks dinámicos y las notas sueltas
    /// (contexto "" = sin contexto, como en <see cref="LooseNoteStore.RemapScope"/>). La global no
    /// pertenece a ningún espacio y ninguna reorganización la toca.
    ///
    /// Si dos keys caen en la misma (no debería: ProjectStore rechaza los choques de nombre antes),
    /// las listas se FUSIONAN en vez de pisarse — perder un secreto en silencio es lo único peor que
    /// tenerlo en el lugar equivocado.
    ///
    /// Los vínculos "inicia sesión con" no necesitan nada acá (ni en <see cref="LiftScope"/>): van
    /// por id, y mover listas entre keys no cambia ningún id.
    /// </summary>
    public void RemapScope(Func<string, string, (string Project, string Module)> map)
    {
        var next = new Dictionary<string, List<Credential>>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var (key, list) in _byScope)
        {
            string newKey = key;
            if (key != ProjectStore.GlobalScope)
            {
                int sep = key.IndexOf(ProjectStore.ScopeSeparator);
                string p = sep < 0 ? key : key[..sep];
                string m = sep < 0 ? "" : key[(sep + 1)..];
                var (np, nm) = map(p, m);
                newKey = ProjectStore.ScopeKey(np, nm);
                if (newKey != key) changed = true;
            }
            ListFor(next, newKey).AddRange(list);
        }
        if (!changed) return;

        _byScope.Clear();
        foreach (var (k, v) in next) _byScope[k] = v;
        Save();
    }

    /// <summary>
    /// Se borró un contexto (<paramref name="module"/> dado) o un espacio (<paramref name="module"/>
    /// null → el espacio Y todos sus contextos, "P" y "P/…"). A diferencia de las variables, que caen
    /// en la cascada de ProjectStore, las credenciales SUBEN un nivel: las de un contexto pasan a su
    /// espacio y las de un espacio borrado (con las de sus contextos) pasan a la global. Decisión del
    /// usuario: perder una contraseña por borrar un scope es irrecuperable, y que quede a la vista
    /// desde otros desks es el costo aceptado — se puede mover o borrar a mano después.
    /// </summary>
    public void LiftScope(string project, string? module)
    {
        // Sin espacio no hay nada que subir: un "" acá sería la global, que nunca se toca por acá.
        if (string.IsNullOrEmpty(project)) return;

        string prefix = project + ProjectStore.ScopeSeparator;
        string target = module is null ? ProjectStore.GlobalScope : project;
        var lifted = _byScope.Keys.Where(k => module is null
                ? string.Equals(k, project, StringComparison.OrdinalIgnoreCase)
                  || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                : string.Equals(k, ProjectStore.ScopeKey(project, module), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lifted.Count == 0) return;

        // Se anexan al final del destino, en el orden en que estaban: nada se pisa ni se fusiona por
        // título — dos "Jira" con distinto dueño siguen siendo dos.
        foreach (var k in lifted)
        {
            var list = _byScope[k];
            _byScope.Remove(k);
            ListFor(_byScope, target).AddRange(list);
        }
        Save();
    }
}
