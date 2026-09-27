using System;
using System.Collections.Generic;
using System.Threading;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Asegura que existan los escritorios FIJOS del catálogo (rol Main/Fixed), con sus nombres, al
/// arrancar Y en caliente (ver <see cref="Reconcile"/>, que usa el mismo camino en runtime).
///
/// ⚠ NUNCA renombra por POSICIÓN/ÍNDICE — sólo por NOMBRE. Ésta es la corrección de un bug real:
/// la versión vieja hacía <c>for (i in 0..wanted.Count) desktops.SetName(i, wanted[i].Name)</c>, es
/// decir "el desktop N-ésimo QUE SEA pasa a llamarse el N-ésimo del catálogo". Eso asume que el
/// orden de los escritorios vivos coincide con el orden del catálogo — una asunción que la reforma
/// de escritorios DINÁMICOS rompió: los dinámicos se crean y borran en caliente, corriendo los
/// índices de todo lo que esté después. Caso real reproducido: catálogo MAIN(0) / NOTES(1) / MISCS(2);
/// el usuario cerró NOTES a mano; tras reiniciar Windows, los desktops vivos quedaron
/// [MAIN, <dinámico "Geocontrol/Plataforma">, <dinámico "Geocontrol/Plataforma Develop">] — el
/// bootstrapper viejo, renombrando por índice, le puso "NOTES" al desk dinámico del índice 1 (que
/// NO era NOTES, era un espacio real registrado en DynamicDeskStore). Resultado: Win+Numpad2 (NOTES
/// por nombre) y la tecla dinámica de ese espacio apuntaban AL MISMO escritorio, indistinguibles.
///
/// La cura: por cada entrada del catálogo, buscamos un desktop que YA se llame así
/// (<see cref="DesktopService.FindExact"/> — el mismo criterio de nombre que usa toda la app, no
/// uno inventado acá). Si no existe, PRIMERO tratamos de adoptar un desktop "virgen" (sin nombre
/// propio — el default de un Windows recién instalado, o uno creado por el SO/por nosotros que
/// todavía no recibió un SetDesktopName) antes de crear uno de más; si tampoco hay virgen, recién
/// ahí CreateDesktop. El orden en que terminan viviendo los escritorios NO importa: todo en la app
/// navega por nombre/tecla, nunca por índice fijo — así que crear al final es tan válido como
/// insertar en el medio (la DLL no expone un "mover", así que ni se plantea).
/// </summary>
public static class DesktopBootstrapper
{
    /// <summary>
    /// Corre el reconcile completo (arranque o self-heal en caliente): por cada fijo del catálogo,
    /// resuelve conflicto con un dinámico homónimo, adopta un virgen o crea uno nuevo.
    /// </summary>
    /// <returns>Cantidad de escritorios CREADOS (0 si sólo renombró/adoptó, o no hizo falta nada).</returns>
    public static int Ensure(DesktopConfig config, DesktopService desktops, DynamicDeskStore dynamicDesks)
    {
        var wanted = config.Managed;
        if (wanted.Count == 0) return 0;

        int created = 0;
        var claimedThisPass = new HashSet<int>(); // vírgenes ya adoptados en esta corrida

        foreach (var entry in wanted)
        {
            // 1) ¿Ya existe, con ese nombre exacto? No-op — el caso sano de todos los días.
            if (desktops.FindExact(entry.Name) >= 0)
                continue;

            // 2) Conflicto: un desk DINÁMICO vivo terminó con el MISMO nombre que este fijo (el bug
            //    de arriba, ya materializado en disco). El FIJO gana: es lo que el usuario configuró
            //    a propósito en Config → Escritorios; el dinámico se re-crea solo la próxima vez que
            //    el launcher confirme ese espacio+contexto. Sólo soltamos el registro — el desktop
            //    real YA se llama como el fijo, así que no hace falta tocarlo ni renombrarlo.
            var conflict = dynamicDesks.FindLiveByDeskName(entry.Name);
            if (conflict is not null)
            {
                dynamicDesks.Unregister(conflict.Id); // persiste; libera su tecla del numpad
                continue;
            }

            // 3) Adoptar un desktop VIRGEN (sin nombre propio) antes de crear uno de más. Nunca uno
            //    ya registrado como dinámico — eso sería robarle el escritorio a un espacio real.
            int adopt = FindAdoptableUnnamed(desktops, dynamicDesks, claimedThisPass);
            if (adopt >= 0)
            {
                desktops.SetName(adopt, entry.Name);
                claimedThisPass.Add(adopt);
                continue;
            }

            // 4) Nada que adoptar → CREAR. Crea al final, sin cambiar el foco (bootstrap silencioso).
            VirtualDesktopAccessor.CreateDesktop();
            Thread.Sleep(60); // dar tiempo a que el shell lo registre antes de nombrarlo
            int newIndex = desktops.Count - 1;
            desktops.SetName(newIndex, entry.Name);
            claimedThisPass.Add(newIndex);
            created++;
        }

        return created;
    }

    /// <summary>
    /// Primer desktop sin nombre propio que no sea dinámico ni ya se haya adoptado en esta misma
    /// corrida. Recorre en orden de índice sólo para tener un criterio determinístico — el índice
    /// resultante no significa nada para el resto de la app.
    /// </summary>
    private static int FindAdoptableUnnamed(DesktopService desktops, DynamicDeskStore dynamicDesks, HashSet<int> claimed)
    {
        int count = desktops.Count;
        for (int i = 0; i < count; i++)
        {
            if (claimed.Contains(i)) continue;
            if (dynamicDesks.IsDynamicIndex(i)) continue; // jamás adoptar un espacio real
            if (desktops.IsUnnamedDefault(i)) return i;
        }
        return -1;
    }
}
