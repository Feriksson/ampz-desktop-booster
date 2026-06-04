using System.Threading;
using System.Threading.Tasks;

namespace AmpzDesktopBooster.Services.Tasks;

/// <summary>
/// Adapter de JIRA — STUB por ahora. La firma ya está lista (enchufada al mismo puerto que Vikunja);
/// el fetch real se implementa en la próxima tanda:
///   GET {BaseUrl}/rest/api/3/search?jql=assignee = currentUser() AND statusCategory != Done
///   auth Basic email:token (Cloud) o Bearer PAT (Server/DC).
/// Hoy devuelve un Failed CLARO para que la UI no prometa lo que todavía no hay.
/// </summary>
public sealed class JiraTaskProvider : ITaskProvider
{
    public string Id => "jira";
    public string DisplayName => "JIRA";

    private readonly JiraSettings _settings;

    public JiraTaskProvider(JiraSettings settings) => _settings = settings;

    public Task<TaskFetchResult> GetOpenTasksAsync(CancellationToken ct = default)
        => Task.FromResult(TaskFetchResult.Failed(Id,
            "El proveedor JIRA todavía no está implementado (próxima tanda). El andamiaje ya está listo."));
}
