using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AmpzDesktopBooster.Knowledge;

/// <summary>
/// La solidez de UNA entrada vista desde UN lado, calculada a una fecha.
/// <see cref="Played"/> false es un estado PROPIO ("no jugada"), no un 0%: nunca haberla visto no es
/// lo mismo que haberla visto y no saberla, y la UI las muestra distinto.
/// </summary>
/// <param name="Played">¿Tiene al menos un repaso de ese lado (a la fecha del cálculo)?</param>
/// <param name="Value">Solidez de HOY, 0..100, ya con el desgaste del tiempo aplicado.</param>
/// <param name="Fresh">La que tenía recién terminado su último repaso (sin desgaste). Sirve para graficar.</param>
/// <param name="LastReviewedAt">Fecha (UTC) del último repaso; null si no se jugó.</param>
/// <param name="ReviewCount">Cuántos repasos entraron en el cálculo.</param>
/// <param name="Streak">Racha vigente de respuestas buenas (3 o 4) seguidas.</param>
public readonly record struct SolidityState(
    bool Played, double Value, double Fresh, DateTime? LastReviewedAt, int ReviewCount, int Streak)
{
    public static readonly SolidityState NotPlayed = new(false, 0, 0, null, 0, 0);
}

/// <summary>Promedio de solidez de un conjunto. Las no jugadas pesan 0 en <see cref="Value"/>, pero
/// <see cref="Played"/>/<see cref="Total"/> van aparte para que un 30% "porque recién empiezo" no se
/// lea igual que un 30% "porque me cuesta".</summary>
public readonly record struct SolidityAverage(double Value, int Played, int Total);

/// <summary>
/// Calculadora de SOLIDEZ: cuán firme está un conocimiento, de 0 a 100. Es PURA — no toca la base,
/// recibe los repasos y la fecha — y la solidez NO se guarda en ningún lado: se deriva del historial
/// cada vez. Así la fórmula se puede afinar mañana y todo el pasado se re-lee con la fórmula nueva.
///
/// Esto NO es Anki, y es una decisión explícita del usuario. Acá no hay fechas de vencimiento, ni
/// cola acumulada, ni contador de cosas "que te tocan hoy": ese modelo convierte estudiar en una deuda
/// que crece si faltás una semana, y se rechazó de plano. El modelo es otro: lo que sabés se DESGASTA
/// de a poco si no lo usás, nunca se borra sólo por el paso del tiempo, y el quiz simplemente te
/// muestra más seguido lo que tenés más flojo. Si no jugás un mes no te espera ningún castigo: los
/// números bajaron un poco y listo.
/// </summary>
public static class Solidity
{
    // ── Constantes de la fórmula ─────────────────────────────────────────────────────────────

    // Hacia dónde EMPUJA cada respuesta. No son "puntos que se suman": son el valor al que esa
    // respuesta dice que estás. "No lo sé" apunta a 0 y "Un espectáculo" a 100; las dos del medio
    // reparten el tramo en tercios (35 y 70) para que "tuve dudas" quede claramente bajo la mitad.
    private static readonly double[] GradeTarget = { 0, 0, 35, 70, 100 }; // índice = (int)Grade

    // Cada repaso recorre la MITAD del camino entre la solidez actual y el objetivo de la respuesta.
    // Con 1.0 un solo acierto te daría 100 (un golpe de suerte = "sabido") y un solo error borraría
    // meses de trabajo; con 0.5 hacen falta varias respuestas buenas seguidas para llegar arriba, y
    // un tropiezo duele (baja a la mitad) pero no te manda a cero.
    private const double LearningRate = 0.5;

    // Antes del primer repaso se parte de 0: lo que nunca demostraste, no lo tenés.
    private const double InitialSolidity = 0;

    // Desde qué respuesta se considera "buena" para la racha: "Bien pero mejorable" ya cuenta.
    private const int StreakMinGrade = (int)Grade.Improvable;

    // Vida media del desgaste: cuántos días tarda la parte "volátil" en caer a la mitad. Sin racha
    // (o con una sola buena) son 14 días; cada respuesta buena CONSECUTIVA la duplica (14, 28, 56,
    // 112…): lo que venís respondiendo bien varias veces seguidas se asienta y aguanta más sin uso.
    private const double BaseHalfLifeDays = 14;

    // Techo de la vida media. Sin tope, una racha larga la llevaría a décadas y el número quedaría
    // clavado para siempre; un año es "esto ya lo sé de verdad" sin volverlo inmortal.
    private const double MaxHalfLifeDays = 365;

    // El PISO: por más tiempo que pase, la solidez mostrada nunca baja del 40% de la que alcanzaste.
    // Es el corazón de la decisión: el tiempo solo NUNCA lleva algo a cero. Lo aprendido deja huella
    // (re-aprenderlo cuesta menos que la primera vez); lo único que la baja de ahí es responder mal.
    private const double FloorRatio = 0.4;

    // Sesgo del orden del quiz: cada entrada pesa (100 - solidez) + este mínimo. El mínimo evita que
    // lo que está en 100 tenga peso 0 y NO SALGA NUNCA MÁS — hasta lo más firme vuelve a aparecer de
    // vez en cuando, que es justamente lo que lo mantiene firme.
    private const double QuizBaseWeight = 10;

    public const double Max = 100;

    // ── Cálculo ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Solidez de UNA entrada+lado a la fecha <paramref name="now"/> (UTC). <paramref name="reviews"/>
    /// deben ser ya los de esa entrada y ese lado, en cualquier orden. Los posteriores a
    /// <paramref name="now"/> se ignoran: pasarle una fecha vieja devuelve la solidez que había ESE
    /// día, y así se grafica la evolución sin otra fórmula.
    /// </summary>
    public static SolidityState Compute(IEnumerable<Review> reviews, DateTime now) =>
        ComputeAt(reviews, new[] { now })[0];

    /// <summary>
    /// La solidez de UNA entrada+lado en VARIAS fechas a la vez, que tienen que venir de la más vieja
    /// a la más nueva: el resultado <c>[i]</c> es lo que daría <see cref="Compute"/> con la fecha
    /// <c>[i]</c>. Existe para graficar la evolución — una muestra por semana durante medio año —
    /// sin releer y reordenar el historial entero por cada muestra: se recorre UNA vez, y en cada
    /// fecha se anota cómo venía. Es la ÚNICA implementación de la fórmula (<see cref="Compute"/> es
    /// este mismo recorrido con una sola fecha), así que el gráfico y los números de hoy no pueden
    /// contar dos historias distintas.
    /// </summary>
    public static SolidityState[] ComputeAt(IEnumerable<Review> reviews, IReadOnlyList<DateTime> ascendingDates)
    {
        // El historial casi siempre llega YA ordenado (el store lo entrega por fecha): se comprueba en
        // una pasada y sólo se ordena si hace falta. Con miles de grupos entrada+lado —las
        // estadísticas calculan uno por uno— ordenar siempre era la mayor parte del costo.
        var ordered = reviews.ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            var a = ordered[i - 1];
            var b = ordered[i];
            int byDate = Utc(a.ReviewedAt).CompareTo(Utc(b.ReviewedAt));
            if (byDate < 0 || (byDate == 0 && a.Id <= b.Id)) continue;
            ordered = ordered.OrderBy(r => Utc(r.ReviewedAt)).ThenBy(r => r.Id).ToList();
            break;
        }
        var result = new SolidityState[ascendingDates.Count];

        double s = InitialSolidity;
        int streak = 0;
        int applied = 0;
        DateTime? previous = null;
        for (int d = 0; d < ascendingDates.Count; d++)
        {
            var date = Utc(ascendingDates[d]);

            // Entran los repasos hechos hasta esta fecha (los posteriores todavía "no pasaron").
            for (; applied < ordered.Count && Utc(ordered[applied].ReviewedAt) <= date; applied++)
            {
                var r = ordered[applied];
                var at = Utc(r.ReviewedAt);

                // Cada repaso parte de lo que REALMENTE quedaba ese día, no de lo que había al terminar
                // el repaso anterior. Sin esto, algo que no tocaste en dos años retomaba desde su valor
                // viejo intacto y un solo acierto lo dejaba más alto que nunca: el tiempo sin uso no
                // habría costado nada. El desgaste del tramo usa la racha que había DURANTE ese tramo
                // (la de antes de esta respuesta), que es la que decidió cuánto aguantaba.
                if (previous is { } before)
                    s = Decay(s, streak, Math.Max(0, (at - before).TotalDays));

                int grade = Math.Clamp((int)r.Grade, 1, 4);
                s += LearningRate * (GradeTarget[grade] - s);
                streak = grade >= StreakMinGrade ? streak + 1 : 0; // una respuesta floja corta la racha
                previous = at;
            }

            result[d] = previous is { } last
                ? new SolidityState(true, Decay(s, streak, Math.Max(0, (date - last).TotalDays)), s, last, applied, streak)
                : SolidityState.NotPlayed;
        }
        return result;
    }

    /// <summary>
    /// Lo mismo para muchos repasos mezclados (lo que devuelve <c>KnowledgeStore.GetReviews</c>):
    /// agrupa por entrada+lado y calcula cada uno. Lo que no aparece en el resultado no se jugó —
    /// consultalo con <see cref="StateOf"/>, que ya devuelve "no jugada" en ese caso.
    /// </summary>
    public static IReadOnlyDictionary<(long EntryId, string Side), SolidityState> ComputeAll(
        IEnumerable<Review> reviews, DateTime now)
    {
        var map = new Dictionary<(long, string), SolidityState>();
        foreach (var g in reviews.GroupBy(r => (r.EntryId, r.Side)))
        {
            var state = Compute(g, now);
            if (state.Played) map[g.Key] = state;
        }
        return map;
    }

    /// <summary>El estado de una entrada+lado dentro de un mapa de <see cref="ComputeAll"/>.</summary>
    public static SolidityState StateOf(
        IReadOnlyDictionary<(long EntryId, string Side), SolidityState> states, long entryId, string side) =>
        states.TryGetValue((entryId, side), out var s) ? s : SolidityState.NotPlayed;

    /// <summary>
    /// La solidez de una entrada como UN número: el promedio de sus lados jugados — frente, dorso y
    /// los de idioma («se mostró la traducción»). null = no se jugó de ningún lado. Cada lado lleva su
    /// propia solidez (saber una palabra viéndola no es evocarla desde la definición ni desde el
    /// inglés), pero una fila de una lista necesita un solo valor.
    /// Vive acá porque lo muestran varias pantallas —las listas del playbooker, sus estadísticas y la
    /// config— y tienen que dar EL MISMO número para la misma entrada.
    ///
    /// Los lados de idioma cuentan desde que el playbooker deja jugar «Inglés → término»: sin ellos,
    /// un término practicado sólo desde su traducción seguía figurando "sin jugar" para siempre.
    /// </summary>
    public static double? EntryValue(
        IReadOnlyDictionary<(long EntryId, string Side), SolidityState> states, long entryId) =>
        EntryValues(states).TryGetValue(entryId, out double value) ? value : null;

    // Los lados de idioma de una entrada no se pueden preguntar uno por uno (habría que conocer la
    // lista de idiomas), así que el promedio por entrada se arma recorriendo el mapa UNA vez y se
    // guarda pegado a ESE mapa: una lista de mil filas pregunta mil veces y el recorrido es uno solo.
    // La tabla es débil — cuando el mapa se suelta, su índice se va con él.
    //
    // Supone lo que ya es cierto en toda la app: un mapa de ComputeAll no se modifica después de
    // armado (cada pantalla pide uno nuevo cuando hay repasos nuevos).
    private static readonly ConditionalWeakTable<object, Dictionary<long, double>> EntryValueIndex = new();

    private static Dictionary<long, double> EntryValues(
        IReadOnlyDictionary<(long EntryId, string Side), SolidityState> states) =>
        EntryValueIndex.GetValue(states, _ =>
        {
            var sums = new Dictionary<long, (double Sum, int Sides)>();
            foreach (var (key, state) in states)
            {
                if (!state.Played) continue;
                sums.TryGetValue(key.EntryId, out var acc);
                sums[key.EntryId] = (acc.Sum + state.Value, acc.Sides + 1);
            }
            return sums.ToDictionary(p => p.Key, p => p.Value.Sum / p.Value.Sides);
        });

    /// <summary>
    /// Promedio de un conjunto de entradas visto desde un lado. Las no jugadas cuentan como 0 en el
    /// promedio (una biblioteca recién creada arranca en 0 y SUBE a medida que la jugás), y el par
    /// jugadas/total va aparte.
    /// </summary>
    public static SolidityAverage Average(
        IEnumerable<long> entryIds, string side,
        IReadOnlyDictionary<(long EntryId, string Side), SolidityState> states)
    {
        double sum = 0;
        int played = 0, total = 0;
        foreach (long id in entryIds)
        {
            total++;
            var st = StateOf(states, id, side);
            if (!st.Played) continue;
            played++;
            sum += st.Value;
        }
        return new SolidityAverage(total == 0 ? 0 : sum / total, played, total);
    }

    /// <summary>
    /// Orden del quiz: un mezclado al azar SESGADO a lo más flojo. Peso = (100 − solidez) + 10, con
    /// las no jugadas tratadas como solidez 0 (lo nuevo tiende a salir primero). Sigue siendo azar —
    /// no una lista ordenada de peor a mejor — porque repetir siempre el mismo orden enseña el orden,
    /// no el contenido. Recibe el <see cref="Random"/> para poder ser determinístico con una semilla.
    /// </summary>
    public static List<long> WeightedOrder(
        IEnumerable<long> entryIds, string side,
        IReadOnlyDictionary<(long EntryId, string Side), SolidityState> states, Random random)
    {
        // Muestreo ponderado sin reposición (Efraimidis–Spirakis): a cada entrada se le sortea una
        // clave u^(1/peso) y se ordena de mayor a menor. Equivale a ir sacando de una urna donde cada
        // una tiene tantas bolillas como peso, pero en una sola pasada.
        return entryIds
            .Select(id =>
            {
                var st = StateOf(states, id, side);
                double weight = (Max - (st.Played ? st.Value : 0)) + QuizBaseWeight;
                double u = 1.0 - random.NextDouble(); // (0, 1]: evita log(0)
                return (Id: id, Key: Math.Log(u) / weight);
            })
            .OrderByDescending(x => x.Key)
            .Select(x => x.Id)
            .ToList();
    }

    /// <summary>Vida media (días) que da una racha de <paramref name="streak"/> buenas seguidas.</summary>
    public static double HalfLifeDays(int streak) =>
        Math.Min(MaxHalfLifeDays, BaseHalfLifeDays * Math.Pow(2, Math.Max(streak, 1) - 1));

    /// <summary>
    /// Desgaste: el valor baja desde <paramref name="fresh"/> HACIA su piso (40%), acercándose cada
    /// vez más despacio, sin cruzarlo nunca. Al principio se nota; después de mucho tiempo ya casi
    /// no se mueve. Se aplica dos veces con la misma regla: entre un repaso y el siguiente (para
    /// saber desde dónde parte la respuesta nueva) y desde el último repaso hasta hoy (lo que se ve).
    /// El piso es siempre el 40% de lo que había al terminar el repaso que abre el tramo.
    /// </summary>
    private static double Decay(double fresh, int streak, double days)
    {
        double floor = FloorRatio * fresh;
        double remaining = Math.Pow(0.5, days / HalfLifeDays(streak));
        return Math.Clamp(floor + (fresh - floor) * remaining, 0, Max);
    }

    // Las fechas del dominio son UTC. Una Local se convierte; una sin Kind se toma como UTC (así
    // salen de la base) en vez de dejar que .NET la suponga local y corra todo unas horas.
    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Local
        ? d.ToUniversalTime()
        : DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
