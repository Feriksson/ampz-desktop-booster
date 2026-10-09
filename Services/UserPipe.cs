using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Fábrica de Named Pipes de escucha con DACL EXPLÍCITA: sólo el usuario actual (y SYSTEM).
///
/// ¿Por qué no el DACL default? Porque desde el arranque ELEVADO (ver AutoStartService: tarea
/// programada con RunLevel=Highest) el default de un token elevado da acceso sólo a Administradores y
/// SYSTEM. Los clientes reales corren SIN elevar (el hook de Claude Code, la 2da instancia que lanza
/// Windows como navegador) y en su token el grupo Administradores es deny-only → "Acceso denegado"
/// al conectar. Y como los clientes fallan en silencio a propósito (un hook nunca rompe a Claude), el
/// síntoma era sólo "dejó de sonar", sin un error en ningún lado.
///
/// Dar acceso al SID del usuario (no al grupo Administradores) funciona elevados o no, y además
/// cierra el pipe a cualquier otro principal — la ventaja de seguridad que justificaba el pipe.
/// </summary>
internal static class UserPipe
{
    public static NamedPipeServerStream CreateServer(string name, PipeDirection direction)
    {
        var security = new PipeSecurity();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(name, direction,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0,
            security);
    }
}
