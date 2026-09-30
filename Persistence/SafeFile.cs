using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AmpzDesktopBooster.Persistence;

/// <summary>Un archivo de datos que se encontró ilegible al cargar. <see cref="MovedAsideTo"/> es el
/// nombre con el que quedó apartado (null si no se pudo mover o si sólo faltaba el principal).</summary>
public sealed record CorruptionIncident(string FileName, bool Recovered, string? MovedAsideTo);

/// <summary>
/// Escritura ATÓMICA + lectura a prueba de archivos rotos para TODO lo que persiste datos del usuario.
///
/// Por qué existe — la cadena de pérdida total que corta:
///   1. Antes cada store hacía <c>File.WriteAllText</c>: TRUNCA el archivo real y después escribe.
///      Si el proceso muere en el medio (crash, corte de luz, o nuestro propio <c>Kill()</c> al final
///      de <c>App.OnExit</c>), el JSON queda a medias.
///   2. El <c>Load</c> atrapaba el error de parseo y devolvía defaults EN SILENCIO ("la persistencia
///      nunca voltea la app").
///   3. El siguiente <c>Save</c> pisaba el archivo roto con esos defaults → se perdían, sin aviso,
///      todos los espacios/contextos/variables/notas. El archivo roto era la ÚNICA copia.
///
/// La cura, en tres partes:
///   - Escribir SIEMPRE a <c>&lt;archivo&gt;.tmp</c> (con flush a disco) y recién ahí reemplazar con
///     <c>File.Replace</c>, que además deja la versión anterior como <c>.bak</c>. El archivo real nunca
///     está a medio escribir: o es el viejo entero o el nuevo entero.
///   - Al cargar, si el principal no parsea, se prueba el <c>.bak</c>; y en TODO caso de corrupción el
///     ilegible se APARTA como <c>.corrupt-&lt;fecha&gt;</c> para que ningún Save pueda pisarlo.
///   - El incidente se registra y <c>App</c> lo avisa con un toast: degradar en silencio fue parte del bug.
///
/// Contrato: <see cref="WriteAllText"/> PUEDE tirar (el caller mantiene su try/catch → sigue en memoria);
/// <see cref="LoadJson{T}"/> NUNCA tira. Un <c>.tmp</c> sobrante de un crash se ignora y se pisa.
/// </summary>
public static class SafeFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Un solo candado: el volumen es chico, y así dos escrituras al MISMO archivo (p.ej. los tres
    // IniFile sobre settings.ini) nunca se pisan el mismo .tmp.
    private static readonly object WriteGate = new();

    private static readonly object IncidentGate = new();
    private static readonly List<CorruptionIncident> Pending = new();
    private static Action<CorruptionIncident>? _notify;

    /// <summary>Escribe <paramref name="text"/> (UTF-8 sin BOM, igual que File.WriteAllText) de forma atómica.</summary>
    public static void WriteAllText(string path, string text)
    {
        lock (WriteGate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Utf8NoBom.GetBytes(text));
                fs.Flush(flushToDisk: true); // sin esto el Replace puede "ganarle" a los datos en caché
            }

            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            try
            {
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            catch (IOException)
            {
                // Algunos volúmenes / filtros de antivirus rechazan Replace. Plan B: guardar el .bak a
                // mano y MoveFileEx con reemplazo — sigue sin truncar nunca el archivo real.
                File.Copy(path, path + ".bak", overwrite: true);
                File.Move(tmp, path, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Carga un JSON propio. Devuelve null si no hay datos (primer arranque) o si ni el principal ni
    /// el .bak se pudieron leer → el caller usa sus defaults. <paramref name="parse"/> puede tirar;
    /// tirar o devolver null cuenta como archivo dañado.
    /// </summary>
    public static T? LoadJson<T>(string path, Func<string, T?> parse) where T : class
    {
        try
        {
            string bak = path + ".bak";
            if (File.Exists(path))
            {
                if (TryParse(path, parse, out var value, retries: 3)) return value;

                // El principal está roto. Primero lo apartamos (así ningún Save lo pisa), después el .bak.
                string? aside = MoveAside(path);
                bool recovered = TryParse(bak, parse, out var fromBak, retries: 1);
                // Si no se pudo apartar (bloqueado), NO lo pisamos con el .bak: sigue siendo una copia.
                if (recovered && aside is not null) RestoreFromBak(bak, path);
                Report(new CorruptionIncident(Path.GetFileName(path), recovered, aside));
                return recovered ? fromBak : null;
            }

            // Falta el principal pero hay .bak: un crash justo dentro de File.Replace (que renombra en
            // dos pasos) puede dejar exactamente eso. Recuperamos la versión anterior.
            if (File.Exists(bak) && TryParse(bak, parse, out var onlyBak, retries: 1))
            {
                RestoreFromBak(bak, path);
                Report(new CorruptionIncident(Path.GetFileName(path), Recovered: true, MovedAsideTo: null));
                return onlyBak;
            }
        }
        catch { /* nunca tumbamos la app por la persistencia */ }
        return null;
    }

    /// <summary>
    /// Conecta el aviso a la UI: entrega los incidentes acumulados durante el arranque y todos los que
    /// lleguen después (stores que cargan tarde). Se llama UNA vez, con la app ya montada.
    /// </summary>
    public static void AttachNotifier(Action<CorruptionIncident> notify)
    {
        List<CorruptionIncident> drained;
        lock (IncidentGate)
        {
            _notify = notify;
            drained = new List<CorruptionIncident>(Pending);
            Pending.Clear();
        }
        foreach (var i in drained) notify(i);
    }

    private static void Report(CorruptionIncident incident)
    {
        Action<CorruptionIncident>? notify;
        lock (IncidentGate)
        {
            notify = _notify;
            if (notify is null) Pending.Add(incident);
        }
        try { notify?.Invoke(incident); } catch { /* el aviso nunca voltea una carga */ }
    }

    private static bool TryParse<T>(string path, Func<string, T?> parse, out T? value, int retries) where T : class
    {
        value = null;
        string text;
        // Un error de LECTURA (archivo tomado un instante por el antivirus / indexador) no es
        // corrupción: reintentamos corto antes de declararlo ilegible.
        for (int attempt = 1; ; attempt++)
        {
            try { text = File.ReadAllText(path); break; }
            catch (Exception) when (attempt < retries) { Thread.Sleep(50); }
            catch { return false; }
        }
        try { value = parse(text); }
        catch { value = null; }
        return value is not null;
    }

    /// <summary>Renombra el ilegible a .corrupt-&lt;fecha&gt;. Devuelve el nombre nuevo, o null si no se pudo.</summary>
    private static string? MoveAside(string path)
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        for (int n = 0; n < 10; n++)
        {
            string target = $"{path}.corrupt-{stamp}" + (n == 0 ? "" : $"-{n}");
            if (File.Exists(target)) continue;
            try { File.Move(path, target); return Path.GetFileName(target); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Vuelve a poner el .bak como principal (copia a .tmp + move: también atómico), así el
    /// próximo arranque no re-avisa. Si falla no importa: el valor ya se cargó en memoria.</summary>
    private static void RestoreFromBak(string bak, string path)
    {
        try
        {
            lock (WriteGate)
            {
                string tmp = path + ".tmp";
                File.Copy(bak, tmp, overwrite: true);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch { }
    }
}
