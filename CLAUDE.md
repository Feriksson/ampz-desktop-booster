# Ampz Desktop Booster

App de productividad para Windows que gestiona los **escritorios virtuales** del SO y los liga a
"proyectos" con variables (paths/URLs), notas, pins y restricciones de ventanas. Todo manejado
desde el teclado (numpad) para que un dev no toque el mouse.

Es la **versión moderna en WPF / .NET 10** de un script legacy de AutoHotkey v2
(`ampzWinTunner.ahk`) ubicado en `C:/Users/ampz/Desktop/Repos personales/desktop booster -dev`.
El código está lleno de comentarios que referencian ese legacy — cuando dudes de una decisión de
diseño, la respuesta casi siempre es "así lo hacía el .ahk". El comportamiento es la fuente de verdad.

---

## Stack & build

- **.NET 10** (`net10.0-windows`), **WPF** para toda la UI + **WinForms** SOLO para el tray icon
  (`NotifyIcon`). Los usings globales de `System.Windows.Forms` y `System.Drawing` están REMOVIDOS
  a propósito (chocan con `System.Windows.*` de WPF) — solo `TrayIconService` los importa explícito.
- **x64 únicamente** (`<Platforms>x64</Platforms>`). La DLL nativa es x64.
- `AllowUnsafeBlocks` está ON porque usamos `[LibraryImport]` (genera marshalling unsafe en compile-time).
- `app.manifest` declara **PerMonitorV2** — la AppBar trabaja en píxeles físicos.
- `Nullable` e `ImplicitUsings` habilitados. Namespace raíz: `AmpzDesktopBooster`.
- `VirtualDesktopAccessor.dll` (nativa, x64, **reutilizada tal cual del .ahk original**) se copia al
  output con `PreserveNewest`. Los `Providers/*.png` se embeben como `Resource`.

Build / run:
```powershell
dotnet build AmpzDesktopBooster.csproj    # x64 por config, no hace falta -p:Platform
dotnet run --project AmpzDesktopBooster.csproj
```
También se abre el `.slnx` en Visual Studio. Single-instance: si ya hay una corriendo, la segunda
avisa y sale (mutex global `Global\AmpzDesktopBooster_SingleInstance`) — matá la instancia previa
antes de relanzar o vas a tener dos hooks de teclado peleándose.

### Ciclo build OBLIGATORIO (responsabilidad del AGENTE, no del usuario)

Después de CADA cambio que recompiles, vos (el agente) dejás la app corriendo con el binario nuevo.
NO le pidas al usuario que la cierre/relance — hacelo vos, siempre, en este orden:

1. **Killear** si está corriendo (si no, `dotnet build` falla con `MSB3027`/`MSB3021` por file lock
   del `.exe` — ese error NO es de código, es la app abierta):
   `powershell.exe -Command "Stop-Process -Name AmpzDesktopBooster -Force -ErrorAction SilentlyContinue"`
2. **Rebuildear**: `dotnet build "AmpzDesktopBooster.csproj"`
3. **Relanzar** (detached, no bloquear el shell):
   `powershell.exe -Command "Start-Process 'bin\Debug\net10.0-windows\AmpzDesktopBooster.exe'"`
4. **Confirmar** que levantó (el PID cambia):
   `powershell.exe -Command "Get-Process -Name '*Ampz*' -ErrorAction SilentlyContinue | Select-Object Id,ProcessName"`

Recordá: el Bash tool corre por bash → toda invocación de PowerShell va envuelta en
`powershell.exe -Command "..."`, y NUNCA metas `$_` en ese string (bash lo expande antes que PowerShell;
filtrá por `-Name` en vez de `Where-Object { $_... }`).

---

## Arranque (`App.xaml.cs`) — leelo primero

`OnStartup` arma todo en este orden (el orden importa):

1. **Single-instance mutex** → si ya corre, sale sin montar nada.
2. Handlers de crash → escriben a `ampz-crash.log` (junto al exe). La app NUNCA crashea en silencio.
3. Servicios core: `DesktopService`, `ProjectStore`, `AppsConfig`, `PinStore`, `RestrictionStore`.
   Se inyecta `desktops.ProjectLookup = projects.GetDeskProject` (desacople: DesktopService no
   conoce la capa de persistencia).
4. **`DesktopBootstrapper.Ensure`** (si `AutoCreate`) — crea/renombra los escritorios gestionados.
   Corre ANTES de instalar hooks para no spamear el overlay.
5. **`BarWindow`** — la AppBar real (barra inferior) con tray + widget de sistema.
6. **`WindowGovernor`** — enforcement de pins y restricciones.
7. **`HotkeyService` + `HotkeyRouter`** — hook global de teclado y su ruteo a acciones.
8. **`OverlayWindow`** — feedback central, persistente y oculto, con **debounce de 40ms** (al saltar
   rápido entre desks la DLL postea un mensaje por salto; coalescemos y mostramos solo el final).
9. **`DesktopChangeListener`** — única fuente de verdad del feedback: cualquier cambio de desktop
   (venga de donde venga) actualiza el widget de la barra y dispara overlay + governor.

El hook de teclado se instala **en el thread de UI a propósito**: WPF bombea mensajes ahí, que es lo
que `WH_KEYBOARD_LL` y el `PostMessage` de la DLL necesitan.

---

## Estructura de carpetas

| Carpeta | Qué vive ahí |
|---|---|
| `Desktops/` | Núcleo: escritorios virtuales, proyectos, pins, restricciones, gobierno de ventanas. |
| `Hotkeys/` | Captura de teclado de bajo nivel y ruteo de atajos a acciones. |
| `Interop/` | Todo el P/Invoke nativo: la DLL, hooks (`WH_KEYBOARD_LL`, `WinEvent`), métodos de ventana. |
| `Persistence/` | Rutas de datos, lector INI custom, modelo del catálogo de proyectos. |
| `Apps/` | Apps externas: detección, "Abrir con", Docker, quick actions, contexto de Explorer. |
| `Services/` | AppBar, tray, autostart, monitor de sistema, toasts, extracción de íconos. |
| `Providers/` | Logos PNG de proveedores de IA (embebidos como Resource). |
| raíz | Las ventanas WPF (`*.xaml` + `.xaml.cs`) y el `App`. |

---

## Conceptos de dominio (lo que NO es obvio del código)

### 1. Navegación por NOMBRE, no por índice
Los desks se identifican por **fragmento de nombre** (case-insensitive), no por posición.
`Win+Numpad7` va SIEMPRE a "MAIN" aunque lo muevas de lugar. Set gestionado por defecto:
`MAIN`, `MAILS`, `MISCS`, `DESK +1` … `DESK +6` (ver `DesktopConfig.DefaultManaged`).
`DesktopService` es la capa alta sobre la DLL — **nadie más toca P/Invoke de desktops directo**.

### 2. Las TRES capas de "proyecto por desk" (clave — `ProjectStore`)
Esto confunde si no lo tenés claro:

1. **Sesión** (`_session`, en memoria) — qué proyecto está en qué desk HOY. **Efímero**, se pierde al cerrar.
2. **Sugerencias** (`settings.ini` `[Projects]` `desk_N=...`) — última asignación por desk; solo
   sirve para **pre-llenar** el textbox del setter el próximo día.
3. **Catálogo** (`desk_project_data.json`) — `history` + `paths` + `notes` durables.

**Regla de oro (del legacy): la sesión NUNCA se rellena del INI al arrancar.** Ver proyectos de ayer
sin confirmar sería confuso. El INI solo alimenta el setter, no la sesión activa.

### 3. Dual-scope de variables y notas
`ProjectStore.ResolvePool` / `GetNotes`: si el desk es un `DESK +N` **con proyecto activo en la
sesión** → usa el pool/notas DE ESE PROYECTO. Cualquier otro caso (MAIN/MAILS/MISCS, o DESK+ sin
proyecto) → usa el pool/notas **GLOBAL compartido**. Mismo criterio en `UseProjectScope`.

### 4. Gobierno de ventanas (`WindowGovernor`)
Motor de enforcement que escucha `EVENT_OBJECT_SHOW` (vía `WinEventHook`) + el cambio de desk:
- **Pin**: proceso anclado que aparece fuera de su desk → se mueve ahí y se maximiza.
- **Restricción**: desk restringido solo admite apps de su whitelist; el resto va a MAIN.
  Al ENTRAR a un desk restringido, escanea y limpia (cubre apps Electron que no disparan SHOW fiable).
- Un desk es "restringible" solo si **no** es MAIN ni un `DESK +N` (ver `RestrictionStore.IsRestrictable`).
- Procesos del sistema y la propia app están en blocklist/exempt — nunca se anclan ni se mueven.

---

## Persistencia — dónde vive cada cosa

Todo en **`%APPDATA%\AmpzDesktopBooster\`** (ver `Persistence/AppPaths.cs`). NUNCA junto al exe (el
legacy guardaba en `A_ScriptDir`; esto se modernizó para que la app sea compartible y el exe inmutable).

| Archivo | Formato | Contenido |
|---|---|---|
| `desk_project_data.json` | JSON | Catálogo durable: `history`, `notes`, `paths` (por proyecto), `shared_notes`, `shared_paths`. |
| `settings.ini` | INI custom | `[Projects]` sugerencias, `[Pins]` `proc.exe=idx`, `[Restricted]` `idx=1`, `[Whitelist_IDX]` `proc.exe=1`. |
| `desktops.json` | JSON | `DesktopConfig`: lista `managed` + flag `autoCreate`. |
| `apps.json` | JSON | `AppsConfig`: apps de usuario (`name`, `exePath`, `args` con `{path}`). |
| `widgets.json` | JSON | `WidgetSettings`: qué widgets de la barra están activos (defaults: Clock + Ram). |
| `ampz-crash.log` | texto | Junto al **exe** (no en APPDATA). Log de excepciones no manejadas. |

`IniFile` es un parser INI propio (.NET no trae uno): reescribe el archivo entero en cada op
(el volumen es bajo, gana la simplicidad). Secciones `[X]`, pares `k=v`, comentarios `;` se descartan.

**Patrón de configs**: `static Load()` (try/catch → defaults si corrupto) + `Save()` (try/catch
silencioso → si falla el disco, seguimos en memoria). **La persistencia nunca voltea la app.**

---

## Tabla de hotkeys (`HotkeyRouter`)

**Captura** (`LowLevelKeyboardHook` + `HotkeyService`): hook `WH_KEYBOARD_LL`. NumLock SIEMPRE
suprimido → queda clavado en OFF sin polling. El numpad se decodifica por **scancode** (a prueba de
NumLock); las F-keys por **vkCode** (estable). `HotkeyService` trackea Win/Shift, suprime el combo y
enmascara la tecla Win; `HotkeyRouter` mapea a acciones (todo diferido al `Dispatcher`, el callback
del hook no se puede bloquear).

| Atajo | Acción |
|---|---|
| `Win+Numpad 7/8/9` | Ir a MAIN / MAILS / MISCS |
| `Win+Numpad 1..6` | Ir a DESK +1 … +6 |
| `Win+Numpad + / −` | Ciclar entre los DESK+ **que tienen proyecto activo** (wrap-around) |
| `Win+Shift+`(navegación) | Enviar la ventana activa a ese desk **y seguirla** |
| `Win+NumpadEnter` | Setear el proyecto del desk actual (solo en `DESK +N`) |
| `NumpadClear` (Numpad5, **sin Win**) | Abrir el **DeskPicker** (saltar a un proyecto de la sesión) |
| `Win+Numpad *` | **Variables** del proyecto/global (Paths Manager); re-press dispara el predeterminado |
| `Win+Numpad /` | **Notas** del proyecto/global |
| `Win+Numpad .` (Del) | **Send-window picker** (mandar la ventana activa a un desk elegido) |
| `Win+F2` | "Abrir con" (sobre el path activo del Explorer) |
| `Win+F3` | Variables de entorno |
| `Win+F5` | Panel Docker |
| `Win+F6` | Toggle pin de la ventana activa |
| `Win+F7` | Pin Manager |
| `Win+F8` | Restricciones del desk |
| `Win+F9` | Whitelist: permitir la app activa en el desk actual |
| `Win+F11` | Abrir Descargas |
| `Win+F12` | Panel de refresh rate (Hz) |
| `` Win+` `` (backtick) | Abrir terminal en el path actual del Explorer |

Las ventanas de Variables y Notas son de **instancia única**: re-presionar el atajo con la ventana
abierta NO abre otra (Variables dispara el path predeterminado; Notas la trae al frente).

---

## Servicios y ventanas

**Servicios** (`Services/`):
- `AppBarManager` — registra la ventana como **AppBar real** vía `SHAppBarMessage`: Windows EMPUJA
  las demás ventanas (maximizar ya no la tapa). Clava su rect y revierte cualquier Aero Snap/drag.
- `TrayIconService` — el `NotifyIcon` (único consumidor de WinForms).
- `AutoStartService` — arranque con Windows.
- `SystemMonitor` — snapshot inmutable de CPU/RAM/batería/red vía `NativeMethods`
  (`GetSystemTimes`, `GlobalMemoryStatusEx`, `GetSystemPowerStatus`, `NetworkInterface`).
  No sabe NADA de UI; la barra lo consume. La 1ra muestra de CPU/red da 0 (necesita delta).
- `Toasts` — notificaciones propias (movido por pin/restricción, pin/unpin, etc.).
- `AppIcon` — extracción de íconos de exes.

**Ventanas WPF** (raíz): `BarWindow` (la AppBar + widgets), `OverlayWindow` (feedback central de
cambio de desk), `ConfigWindow` (config, instancia única), `ProjectSetterWindow`, `ProjectPathsWindow`
(variables), `ProjectNotesWindow`, `EnvVarsWindow`, `DockerWindow`, `HzWindow` (refresh rate),
`PinManagerWindow`, `DeskRestrictionsWindow`, `WhitelistPickerWindow`, `SendWindowPickerWindow`,
`AbrirConWindow` ("Abrir con"), `DeskPickerWindow`, `PromptDialog`, `ToastWindow`.

---

## Interop nativo — qué saber antes de tocar

- **`VirtualDesktopAccessor.dll`**: DLL de terceros (x64) que expone el manejo de escritorios
  virtuales (no documentado por MS). Se la consume vía `Interop/VirtualDesktopAccessor.cs`
  (`[LibraryImport]`). Funciones clave: `GetCurrentDesktopNumber`, `GetDesktopCount`,
  `GetDesktopName`/`SetDesktopName` (UTF-8, **no PWSTR** — buffer de bytes null-terminado),
  `GoToDesktopNumber`, `CreateDesktop` (crea al final sin cambiar foco), `MoveWindowToDesktopNumber`,
  `GetWindowDesktopNumber`, `IsWindowOnDesktopNumber`.
- `GetName` cae a `"Desktop N"` si la DLL devuelve basura/vacío (mismo fallback que el legacy).
- Los **callbacks de hooks NO se pueden bloquear**: todo el trabajo real se difiere con
  `Dispatcher.BeginInvoke` (hotkeys) o `DispatcherTimer` one-shot (`WindowGovernor.Defer`).
- Servicios con hooks implementan `IDisposable` y se liberan en `App.OnExit`.

### ⚠ El z-order de la barra ROMPE el hook de teclado — y cómo se arregla (no lo deshagas)

Bug que costó MUCHÍSIMO encontrar (se cazó con `git worktree` del commit pre-feature + instrumentación
a archivo). Síntoma: tras salir de un **video en pantalla completa** (YouTube con `F`), las hotkeys
globales dejaban de responder hasta hacer **click en el escritorio**; el overlay central también
desaparecía. Lo que pasa, confirmado por **bisect** (no teoría):

- **Causa raíz**: CUALQUIER manipulación del z-order de la `BarWindow` (`SetWindowPos` **y/o**
  `_window.Topmost` de WPF), hecha para ocultar/mostrar la barra al entrar/salir de fullscreen,
  **corrompe la entrega de teclas del hook `WH_KEYBOARD_LL`** (que vive en el thread de UI). Windows
  deja de mandarle teclas al hook **hasta el próximo cambio de foco** — ese click que "lo arreglaba"
  ERA un cambio de foco. **Diferir el `SetWindowPos` 750ms NO lo evita** → no es una carrera de
  timing, es el acto mismo de tocar el z-order.
- **Fix (WATCHDOG — auto-curar, no evitar)**: como no se puede impedir que se rompa por esta vía, se
  **reinstala el hook** = el "click automático". `LowLevelKeyboardHook.Reinstall()` (unhook+hook) →
  `HotkeyService.ReinstallHook()` (reinstala + resetea `_winDown`/`_shiftDown`/`_winComboConsumed`) →
  lo dispara `AppBarManager.ZOrderChanged`, cableado en `App.xaml.cs`
  (`bar.OnBarZOrderChanged = () => _hotkeys?.ReinstallHook()`). El z-order va **diferido 750ms** y
  maneja `WS_EX_TOPMOST` por `SetWindowLongPtr`, **nunca** por `_window.Topmost` (su setter cambia la
  activación y agrava el problema).
- **Red DOBLE** (el reinstall es idempotente, por eso se puede llamar dos veces): `ZOrderChanged` se
  dispara (1) **al principio** de `SetFullscreenSuppressed` → al SALIR del fullscreen recuperás el
  teclado al instante, sin esperar los 750ms; y (2) **después** del `ApplyZOrder` diferido → re-cura
  por si ese cambio de z-order lo volvió a romper. Ambas corren en el thread de UI (el watcher
  borderless marshalea su evento con `_uiDispatcher.BeginInvoke`; el `ABN_FULLSCREENAPP` llega por el
  `WndProc`, que ya está en UI) — `ReinstallHook` DEBE correr ahí.
- **NO lo "simplifiques" deshaciendo esto**: el hook **debe** quedarse en el thread de UI (moverlo a
  un thread propio rompe el masking de Win en `SendMaskedWinUp`); y tocar el z-order **siempre** va a
  romper el hook → el watchdog es la cura, no un parche opcional.

---

## Convenciones del repo

- **Comentarios en español**, densos y orientados al *por qué* (no al *qué*). Mantené ese estilo:
  cuando agregues lógica no trivial, explicá la razón, idealmente con la referencia al legacy si aplica.
- Separación de capas estricta: `DesktopService` no toca P/Invoke de bajo nivel directo desde fuera,
  `SystemMonitor` no conoce WPF, `DesktopService` no conoce persistencia (se inyecta el lookup).
- Configs: `Load()`/`Save()` estáticos con try/catch silencioso. **Nunca** dejes que un fallo de
  disco/JSON corrupto tumbe la app — degradá a defaults o a memoria.
- `record struct` para snapshots inmutables que cruzan a la UI.
- Toda data de usuario va a `AppPaths.DataDir` (`%APPDATA%`), jamás junto al exe.
