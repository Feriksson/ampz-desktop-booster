# Adaptador del hook de Claude Code → señal de atención por desk de Ampz Desktop Booster.
#
# ESTE ES EL BORDE. Acá muere el vocabulario de Claude: traducimos el evento del hook
# (Notification / Stop) al vocabulario NEUTRO del core (action-needed / completed) y lo posteamos
# al Named Pipe. El core nunca sabe que existió "Claude" — el día que sumes otro integrador,
# escribís otro adaptador como este y el core no se toca.
#
# El PID que viaja es el de ESTE script: corre como descendiente del proceso de Claude Code, así que
# la app sube el árbol de procesos desde acá hasta la ventana del host (VS Code / Windows Terminal) y
# resuelve en qué escritorio virtual está. No hace falta que el payload traiga PID.
#
# REGLA DE ORO: un hook NUNCA debe romper ni demorar el flujo de Claude. Si la app no está corriendo,
# fallamos en silencio y salimos 0. Pase lo que pase, exit 0.
#
# Configuración (en ~/.claude/settings.json o el .claude/settings.json del proyecto):
#   "Notification": [{ "hooks": [{ "type": "command",
#       "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<ruta>\\claude-attention-hook.ps1' -HookEvent Notification" }]}],
#   "Stop":         [{ "hooks": [{ "type": "command",
#       "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<ruta>\\claude-attention-hook.ps1' -HookEvent Stop" }]}]

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Notification', 'Stop')]
    [string]$HookEvent
)

# El payload del evento llega por stdin (JSON). Solo lo leemos si está redirigido — así una prueba
# manual sin pipe no se cuelga esperando EOF.
$message = ''
$notificationType = ''
$cwd = ''
if ([Console]::IsInputRedirected) {
    try {
        $raw = [Console]::In.ReadToEnd()
        if ($raw) {
            $payload = $raw | ConvertFrom-Json
            $message = [string]$payload.message
            $notificationType = [string]$payload.notification_type
            $cwd = [string]$payload.cwd   # folder del proyecto → la app desambigua la ventana de VS Code
        }
    }
    catch { } # payload ilegible → seguimos con defaults; el aviso vale igual
}

# ── Traducción del BORDE: evento de Claude → nivel NEUTRO del core ──
$level = if ($HookEvent -eq 'Notification') { 'action-needed' } else { 'completed' }

# Texto para el toast. Notification suele traer message; Stop no → ponemos algo claro.
if (-not $message) {
    $message = if ($HookEvent -eq 'Stop') { 'Claude terminó' } else { 'Claude necesita tu atención' }
}
# Si Notification trae un tipo, lo anexamos (permission_prompt / idle_prompt / etc.) para más contexto.
if ($notificationType) { $message = "$message ($notificationType)" }

# ── Armar y postear la señal ──
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$out = [ordered]@{
    pid     = $PID            # descendiente del árbol de Claude Code → la app resuelve el desk del host
    source  = 'claude-code'
    level   = $level
    message = $message
    cwd     = $cwd           # desambigua qué ventana de VS Code es (varias comparten el PID del main)
    ts      = $ts
}
$json = $out | ConvertTo-Json -Compress

try {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'AmpzDesktopBooster.attention', [System.IO.Pipes.PipeDirection]::Out)
    $client.Connect(1000)   # 1s: si la app no corre, fallamos rápido y salimos sin molestar a Claude
    $writer = New-Object System.IO.StreamWriter($client)
    $writer.Write($json)
    $writer.Flush()
    $writer.Dispose()
    $client.Dispose()
}
catch {
    # App cerrada o pipe no disponible → silencio total. El hook jamás rompe el flujo de Claude.
}

exit 0
