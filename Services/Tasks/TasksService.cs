namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Punto único que resuelve QUÉ ITaskProvider usar según la config. El factory crece acá (un case
/// por id) — mismo criterio que el switch de UsageService.
///
/// Por ahora NO hace polling: la integración arranca por la pantalla de Config ("Probar conexión").
/// El cableado al arranque core (polling + feedback en la barra al "setear proyecto") llega en una
/// tanda futura; cuando llegue, este service crecerá a una instancia con DispatcherTimer como
/// UsageService. Devuelve null si la integración está apagada (Provider="none").
/// </summary>
public static class TasksService
{
    public static ITaskProvider? CreateProvider(TasksSettings settings) => settings.Provider switch
    {
        "vikunja" => new VikunjaTaskProvider(settings.Vikunja),
        "jira"    => new JiraTaskProvider(settings.Jira),
        "trello"  => new TrelloTaskProvider(settings.Trello),
        _         => null, // "none" o id desconocido → integración apagada
    };
}
