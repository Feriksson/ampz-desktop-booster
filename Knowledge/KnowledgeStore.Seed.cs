using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace AmpzDesktopBooster.Knowledge;

// Biblioteca de EJEMPLO. Existe para que la primera vez que se abre el quiz haya algo que jugar: una
// app de estudio vacía no explica nada, y armar la primera biblioteca a ciegas (¿qué es un frente?,
// ¿para qué sirve la naturaleza?, ¿cómo se ve un término con traducciones?) es justo la fricción que
// hace que nadie arranque. El contenido es real y está revisado — un ejemplo con datos falsos se
// estudia igual que uno verdadero.
//
// Se siembra UNA sola vez: dentro de la migración 0→1 (ver Initialize). Si el usuario la borra, no
// vuelve. Muestra a propósito las tres cosas no obvias del modelo: bibliotecas anidadas, naturaleza
// heredada (de la biblioteca, y de la biblioteca madre) vs. fijada en la entrada, términos con
// ficha completa, y cards con GLOSARIO (la ayuda del quiz, tecla H).
public sealed partial class KnowledgeStore
{
    private static void Seed(SqliteConnection c, SqliteTransaction tx)
    {
        long Named(string table, string name)
        {
            Execute(c, tx, $"INSERT INTO {table}(name) VALUES ($n)", ("$n", name));
            return LastId(c, tx);
        }

        long technical = Named("natures", "Técnica");
        long legal = Named("natures", "Legal");
        long general = Named("natures", "General");
        long english = Named("languages", "Inglés");
        long french = Named("languages", "Francés");

        long example = InsertLibrary(c, tx, "Ejemplo", null, general);
        // La hija NO fija naturaleza: hereda "General" de "Ejemplo". Es el caso que el ejemplo tiene
        // que mostrar — la naturaleza se pone una vez arriba y baja sola por el árbol.
        long vocabulary = InsertLibrary(c, tx, "Vocabulario de consultoría", example, null);
        string now = UtcNow();

        // ── Tarjetas (frente = pregunta, dorso = respuesta). Sin naturaleza = heredan "General". ──
        void Card(string front, string back, long? nature = null, params GlossaryItem[] glossary)
        {
            long id = InsertEntry(c, tx, example, new EntryDraft(EntryKind.Card, front, back, nature), now);
            InsertGlossary(c, tx, id, Glossary.Normalize(glossary));
        }

        Card("¿Qué es una API?",
            "Interfaz de programación de aplicaciones: el contrato por el que un software expone sus " +
            "funciones a otro sin revelar cómo están hechas por dentro. Define qué se puede pedir, con " +
            "qué datos y qué se recibe a cambio.",
            technical);
        Card("¿Qué significa que una operación sea idempotente?",
            "Que ejecutarla una vez o muchas deja el sistema en el mismo estado. Un PUT que fija un " +
            "valor es idempotente; un POST que crea un registro por cada llamada no lo es. Es lo que " +
            "permite reintentar ante un error de red sin miedo a duplicar.",
            technical);
        Card("¿Qué es la deuda técnica?",
            "El costo futuro de haber elegido hoy una solución rápida en lugar de la correcta. Como una " +
            "deuda financiera, cobra intereses: cada cambio posterior cuesta más, hasta que se salda " +
            "rehaciendo esa parte. La metáfora es de Ward Cunningham (1992).",
            technical);
        Card("¿Qué diferencia hay entre SLI, SLO y SLA?",
            "SLI: el indicador que se mide (por ejemplo, el porcentaje de pedidos respondidos bien). " +
            "SLO: el objetivo interno sobre ese indicador (99,9 %). SLA: el compromiso firmado con el " +
            "cliente, con consecuencias si no se cumple. El SLO suele ser más exigente que el SLA, para " +
            "tener margen antes de incumplir el contrato.",
            technical);
        Card("¿Qué es un NDA?",
            "Un acuerdo de confidencialidad (non-disclosure agreement): contrato por el que las partes " +
            "se obligan a no divulgar la información que comparten. Define qué es confidencial, por " +
            "cuánto tiempo y qué pasa si se incumple. Suele firmarse antes de mostrar información " +
            "sensible en una preventa.",
            legal);
        Card("¿Qué es un MVP?",
            "Producto mínimo viable: la versión más chica de un producto que permite aprender de " +
            "usuarios reales con el menor esfuerzo. Su objetivo es validar una hipótesis, no lanzar " +
            "algo a medio hacer. Lo popularizó Eric Ries en «El método Lean Startup» (2011).");
        Card("¿Qué es un stakeholder?",
            "Toda persona o grupo que afecta a un proyecto o se ve afectado por él: cliente, usuarios, " +
            "patrocinador, equipo, áreas reguladoras. No todos deciden, pero ignorar a uno suele " +
            "salir caro más adelante.");
        Card("¿Qué es el «scope creep»?",
            "El crecimiento no controlado del alcance de un proyecto: pedidos que se van sumando sin " +
            "ajustar ni el plazo ni el presupuesto. Se previene con un alcance escrito y un proceso " +
            "explícito para aprobar cambios.");

        // Cards con glosario. Sus preguntas son de criterio y usan jerga AL PASAR: el glosario explica
        // esas palabras para poder leer la pregunta, y nunca la contesta — es la misma regla que se
        // le pide a quien genera un archivo (ver ImportPrompt.es.txt). Una card de «¿Qué es X?» no
        // lleva glosario: su única palabra difícil es justo lo que pregunta.
        Card("¿Por qué conviene que un webhook de pagos sea idempotente?",
            "Porque el proveedor de pagos reintenta el aviso cuando no recibe la confirmación a " +
            "tiempo, así que un mismo evento puede llegar más de una vez. Si procesarlo de nuevo no " +
            "cambia nada, un reintento nunca termina en un cobro o una entrega duplicados.",
            technical,
            new GlossaryItem("webhook",
                "Aviso automático que un sistema le envía a otro por internet cuando ocurre un evento."),
            new GlossaryItem("idempotente",
                "Dicho de una operación: que puede repetirse dejando el mismo resultado que la primera vez."));
        Card("¿Qué riesgo corre un proveedor de SaaS que firma un SLA sin haber medido antes sus SLO?",
            "Comprometerse por contrato a un nivel que no sabe si puede sostener. Sin objetivos " +
            "internos medidos no hay margen ni alerta temprana: el primer incumplimiento se descubre " +
            "cuando ya corresponde una penalidad.",
            technical,
            new GlossaryItem("SaaS",
                "Software como servicio: programa que se usa por internet pagando un abono, sin instalarlo."),
            new GlossaryItem("SLA",
                "Acuerdo de nivel de servicio: la calidad que el proveedor le garantiza al cliente por contrato."),
            new GlossaryItem("SLO",
                "Objetivo de nivel de servicio: la meta interna que un equipo se fija para un indicador."));
        Card("¿Qué problema resuelve el «rate limiting» en una API pública?",
            "Evita que un solo cliente —por error o por abuso— consuma la capacidad de todos. Con un " +
            "tope de pedidos por unidad de tiempo el servicio sigue respondiendo a los demás y el " +
            "costo de operarlo queda acotado.",
            technical,
            new GlossaryItem("API pública",
                "Interfaz de programación abierta a desarrolladores de fuera de la organización que la ofrece."));
        Card("¿Por qué el «scope creep» pesa más en un contrato de precio fijo que en uno por tiempo y materiales?",
            "Porque en precio fijo cada pedido extra lo absorbe el proveedor: el ingreso no cambia y " +
            "el trabajo sí. En tiempo y materiales el cliente paga las horas adicionales, así que el " +
            "desvío aparece en la factura y se discute antes.",
            null,
            new GlossaryItem("scope creep",
                "Expresión inglesa: crecimiento no controlado del alcance de un proyecto."),
            new GlossaryItem("precio fijo",
                "Modalidad de contrato en la que se acuerda un monto total por un resultado definido."),
            new GlossaryItem("tiempo y materiales",
                "Modalidad de contrato en la que se facturan las horas trabajadas y los gastos, sin un total cerrado."));

        // ── Términos (frente = palabra, dorso = definición) con su ficha y traducciones. ──
        void Term(string word, string wordType, string definition, string usage, string origin,
                  string inEnglish, string inFrench, long? nature = null)
        {
            long id = InsertEntry(c, tx, vocabulary,
                new EntryDraft(EntryKind.Term, word, definition, nature, wordType, usage, origin), now);
            foreach (var (language, text) in new Dictionary<long, string> { [english] = inEnglish, [french] = inFrench })
                Execute(c, tx, "INSERT INTO translations(entry_id, language_id, text) VALUES ($e, $l, $t)",
                    ("$e", id), ("$l", language), ("$t", text));
        }

        Term("Idempotencia", "sustantivo femenino",
            "Propiedad de una operación que produce el mismo resultado aunque se ejecute varias veces.",
            "Diseñamos el endpoint de pagos con idempotencia para poder reintentar sin cobrar dos veces.",
            "Del latín idem («lo mismo») y potens («que puede»). El adjetivo «idempotente» lo acuñó el " +
            "matemático Benjamin Peirce en 1870, en álgebra.",
            "Idempotence", "Idempotence", technical);
        Term("Deuda técnica", "locución nominal",
            "Costo acumulado de las soluciones rápidas o provisorias de un sistema, que encarece cada " +
            "cambio posterior hasta que se corrigen.",
            "Antes de sumar funcionalidades hay que pagar deuda técnica en el módulo de facturación.",
            "Calco del inglés technical debt, metáfora que propuso Ward Cunningham en 1992 para " +
            "explicarle a gente de finanzas el costo de postergar mejoras de diseño.",
            "Technical debt", "Dette technique", technical);
        Term("Escalabilidad", "sustantivo femenino",
            "Capacidad de un sistema para absorber un aumento de carga sumando recursos, sin perder " +
            "rendimiento ni tener que rediseñarse.",
            "La escalabilidad horizontal permite sumar servidores cuando crece el tráfico.",
            "Del inglés scalability, de scale («escala»), y este del latín scala («escalera»).",
            "Scalability", "Évolutivité (scalabilité)", technical);
        Term("Acuerdo de nivel de servicio", "locución nominal",
            "Compromiso contractual entre un proveedor y su cliente sobre la calidad medible de un " +
            "servicio (disponibilidad, tiempos de respuesta) y las consecuencias de incumplirlo.",
            "El acuerdo de nivel de servicio garantiza una disponibilidad mensual del 99,9 %.",
            "Traducción del inglés service level agreement (SLA); se generalizó en los contratos de " +
            "telecomunicaciones y de tercerización informática a fines de los años ochenta.",
            "Service level agreement (SLA)", "Accord de niveau de service", legal);
        Term("Parte interesada", "locución nominal",
            "Persona u organización que influye en un proyecto o se ve afectada por sus resultados.",
            "Hay que alinear a todas las partes interesadas antes de fijar el alcance.",
            "Equivalente del inglés stakeholder: originalmente, quien custodiaba lo apostado (stake) " +
            "por otros. En gestión lo popularizó R. Edward Freeman en 1984.",
            "Stakeholder", "Partie prenante");
        Term("Entregable", "sustantivo masculino",
            "Resultado concreto y verificable que un proyecto se compromete a producir: un documento, " +
            "un módulo de software, un informe.",
            "El entregable de esta etapa es el prototipo navegable.",
            "Derivado de entregar (del latín integrāre, «restituir a su estado íntegro») con el sufijo " +
            "-ble, por influencia del inglés deliverable.",
            "Deliverable", "Livrable");
    }
}
