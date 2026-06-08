# Cliente de prueba del widget de atención por desk.
#
# Conecta al Named Pipe de Ampz Desktop Booster y postea UNA señal de atención, exactamente como
# lo hará el hook de Claude Code (o cualquier integrador futuro). El contrato es el JSON del borde:
#   { pid, source, level, message, ts }
# El "level" es vocabulario NEUTRO del core: "action-needed" | "completed".
#
# El PID que se manda es, por default, el de ESTE proceso de PowerShell ($PID): la app sube el árbol
# de procesos desde ahí hasta la ventana que lo hospeda (la terminal / VS Code) y resuelve su desk.
# Así, corriendo este script desde una terminal en cualquier desk, el toast debe nombrar ESE desk.
#
# Uso:
#   pwsh tools/send-attention.ps1                          # action-needed, PID propio
#   pwsh tools/send-attention.ps1 -Level completed
#   pwsh tools/send-attention.ps1 -TargetPid 12345 -Message "Build roto"

param(
    [int]$TargetPid = $PID,
    [string]$Source = "claude-code",
    [ValidateSet("action-needed", "completed")]
    [string]$Level = "action-needed",
    [string]$Message = "Necesita permiso para continuar"
)

$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$payload = [ordered]@{
    pid     = $TargetPid
    source  = $Source
    level   = $Level
    message = $Message
    ts      = $ts
}
$json = $payload | ConvertTo-Json -Compress

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'AmpzDesktopBooster.attention', [System.IO.Pipes.PipeDirection]::Out)
try {
    $client.Connect(2000)   # 2s de timeout: si la app no está corriendo, fallamos claro
    $writer = New-Object System.IO.StreamWriter($client)
    $writer.Write($json)
    $writer.Flush()
    $writer.Dispose()
    Write-Host "Señal enviada (pid=$TargetPid, level=$Level): $json"
}
catch {
    Write-Error "No se pudo enviar al pipe (¿está corriendo Ampz Desktop Booster?): $_"
}
finally {
    $client.Dispose()
}
