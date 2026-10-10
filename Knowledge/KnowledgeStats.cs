using System;
using System.Collections.Generic;
using System.Linq;

namespace AmpzDesktopBooster.Knowledge;

/// <summary>
/// Una muestra de la evolución: cómo estaba TODO lo cargado ese día. <see cref="Value"/> es el
/// promedio de solidez (0..100) de las entradas que ya existían en <see cref="Date"/>, con las no
/// jugadas pesando 0 — el mismo criterio que <see cref="SolidityAverage"/>.
/// </summary>
public readonly record struct WeekSample(DateTime Date, double Value, int Played, int Total);

/// <summary>
/// La foto que muestran las estadísticas del playbooker, calculada entera en memoria a partir de lo
/// que ya se leyó de la base (las entradas y UNA lectura del historial).
/// </summary>
/// <param name="Overall">Solidez de todo lo cargado, hoy.</param>
/// <param name="TotalReviews">Repasos en todo el historial.</param>
/// <param name="ReviewsLast7">Repasos de los últimos 7 días.</param>
/// <param name="ReviewsLast30">Repasos de los últimos 30 días.</param>
/// <param name="SpectacleCount">Entradas que hoy están a nivel "espectáculo" (ver <see cref="KnowledgeStats.SpectacleThreshold"/>).</param>
/// <param name="GradesLast30">Cuántas veces se dio cada respuesta en los últimos 30 días; índice = <c>(int)Grade</c> (el 0 no se usa).</param>
/// <param name="FirstReviewAt">El repaso más viejo (UTC); null si nunca se jugó nada.</param>
/// <param name="Weekly">Una muestra por semana, de la más vieja a hoy (la última ES hoy).</param>
public sealed record KnowledgeSnapshot(
    SolidityAverage Overall, int TotalReviews, int ReviewsLast7, int ReviewsLast30, int SpectacleCount,
    IReadOnlyList<int> GradesLast30, DateTime? FirstReviewAt, IReadOnlyList<WeekSample> Weekly)
{
    /// <summary>
    /// ¿Hay historia suficiente para dibujar una evolución? Con menos de dos semanas entre el primer
    /// repaso y hoy la curva sería un punto o una raya: se explica eso en vez de dibujarla.
    /// </summary>
    public bool HasHistory(DateTime now) =>
        FirstReviewAt is { } first && (now - first).TotalDays >= KnowledgeStats.MinHistoryDays;

    /// <summary>Las muestras desde que hay algo jugado: antes de eso no hay nada que mostrar.</summary>
    public IReadOnlyList<WeekSample> PlayedWeeks()
    {
        int start = 0;
        while (start < Weekly.Count && Weekly[start].Played == 0) start++;
        return Weekly.Skip(start).ToList();
    }
}

/// <summary>
/// Las ESTADÍSTICAS de la biblioteca de conocimiento. Es pura, como <see cref="Solidity"/>: recibe
/// las entradas, el historial y la fecha, y no toca la base — así se puede probar sola y medir.
///
/// Son un ESPEJO, no una lista de tareas. Dicen cómo está hoy lo que se cargó y cómo vino cambiando;
/// no hay metas, ni cuentas de lo que "toca", ni nada que crezca por no jugar. Es la misma decisión
/// que explica <see cref="Solidity"/>, y vale igual para cualquier número que se agregue acá.
/// </summary>
public static class KnowledgeStats
{
    /// <summary>
    /// Desde qué solidez una entrada cuenta como "a nivel espectáculo". 85 y no 100: con la fórmula
    /// actual hacen falta tres «Un espectáculo» seguidos para pasarlo (50 → 75 → 87,5), y el desgaste
    /// lo va bajando — pedir 100 dejaría el contador clavado en cero aunque todo esté sabido.
    /// </summary>
    public const double SpectacleThreshold = 85;

    /// <summary>Cuántas semanas hacia atrás cubre la evolución (medio año).</summary>
    public const int Weeks = 26;

    /// <summary>Historia mínima para dibujar la evolución (ver <see cref="KnowledgeSnapshot.HasHistory"/>).</summary>
    public const int MinHistoryDays = 14;

    public static KnowledgeSnapshot Compute(IReadOnlyCollection<Entry> entries, IReadOnlyList<Review> reviews, DateTime now)
    {
        now = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : DateTime.SpecifyKind(now, DateTimeKind.Utc);

        // Las fechas de muestra, de la más vieja a hoy. La última es `now` exacto: el final de la
        // curva y el número grande de arriba salen del mismo cálculo y no pueden diferir.
        var dates = new DateTime[Weeks];
        for (int i = 0; i < Weeks; i++) dates[i] = now.AddDays(-7 * (Weeks - 1 - i));

        // Por entrada y por semana: la suma de la solidez de sus lados jugados y cuántos lados eran.
        var createdAt = entries.ToDictionary(e => e.Id, e => e.CreatedAt);
        var sums = new Dictionary<long, double[]>();
        var sides = new Dictionary<long, int[]>();
        foreach (var group in reviews.GroupBy(r => (r.EntryId, r.Side)))
        {
            long id = group.Key.EntryId;
            if (!createdAt.ContainsKey(id)) continue; // repaso de una entrada que ya no está
            if (!sums.TryGetValue(id, out var sum))
            {
                sums[id] = sum = new double[Weeks];
                sides[id] = new int[Weeks];
            }
            var count = sides[id];
            var states = Solidity.ComputeAt(group, dates);
            for (int i = 0; i < Weeks; i++)
            {
                if (!states[i].Played) continue;
                sum[i] += states[i].Value;
                count[i]++;
            }
        }

        var weekly = new WeekSample[Weeks];
        int spectacle = 0;
        for (int i = 0; i < Weeks; i++)
        {
            double total = 0;
            int played = 0, existing = 0;
            foreach (var entry in entries)
            {
                // Una entrada cargada después de esa fecha todavía no existía: no puede bajar el
                // promedio de un día en el que no estaba. Si ya tenía un repaso, existía (una fecha
                // de alta posterior a su primer repaso sólo aparece en una base tocada a mano).
                bool playedThen = sides.TryGetValue(entry.Id, out var count) && count[i] > 0;
                if (!playedThen && entry.CreatedAt > dates[i]) continue;
                existing++;
                if (!playedThen) continue;
                played++;
                double value = sums[entry.Id][i] / count![i];
                total += value;
                if (i == Weeks - 1 && value >= SpectacleThreshold) spectacle++;
            }
            weekly[i] = new WeekSample(dates[i], existing == 0 ? 0 : total / existing, played, existing);
        }

        var grades = new int[5];
        int last7 = 0, last30 = 0;
        DateTime? first = null;
        foreach (var review in reviews)
        {
            var at = DateTime.SpecifyKind(review.ReviewedAt, DateTimeKind.Utc);
            if (first is null || at < first) first = at;
            if (at > now) continue; // un reloj corrido hacia adelante no cuenta como "reciente"
            double age = (now - at).TotalDays;
            if (age <= 7) last7++;
            if (age <= 30)
            {
                last30++;
                grades[Math.Clamp((int)review.Grade, 1, 4)]++;
            }
        }

        var today = weekly[Weeks - 1];
        return new KnowledgeSnapshot(
            new SolidityAverage(today.Value, today.Played, today.Total),
            reviews.Count, last7, last30, spectacle, grades, first, weekly);
    }
}
