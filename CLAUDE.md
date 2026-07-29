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
`Win+Numpad1` va SIEMPRE a "MAIN" aunque lo muevas de lugar. Set gestionado por defecto:
`MAIN`, `CONSOLES`, `MISCS`, `DESK +1` … `DESK +6` (ver `DesktopConfig.DefaultManaged`).
`DesktopService` es la capa alta sobre la DLL — **nadie más toca P/Invoke de desktops directo**.

### 2. Las TRES capas de "proyecto por desk" (clave — `ProjectStore`)
Esto confunde si no lo tenés claro:

1. **Sesión** (`_session`, en memoria) — qué proyecto está en qué desk HOY. **Efímero**, se pierde al cerrar.
2. **Sugerencias** (`settings.ini` `[Projects]` `desk_N=...`) — última asignación por desk; solo
   sirve para **pre-llenar** el textbox del setter el próximo día.
3. **Catálogo** (`desk_project_data.json`) — `history` + `paths` + `notes` durables.

**Regla de oro (del legacy): la sesión NUNCA se rellena del INI al arrancar.** Ver proyectos de ayer
sin confirmar sería confuso. El INI solo alimenta el setter, no la sesión activa.

### 3. MÓDULOS: sub-scopes de un proyecto (`ModuleEntry` + `ModulePalette`)
Un mismo cliente puede ocupar varios desks (Geocontrol → "Plataforma" y "App Mobile"). Un módulo
**NO es un proyecto hermano**: sus variables/notas viven bajo la key compuesta `"Proyecto/Módulo"`
(`ProjectStore.ScopeKey`), lo que deja el shape del JSON intacto y hace que TODO lo que ya operaba
sobre una key de proyecto funcione igual sobre una de módulo, sin ramas nuevas. El `/` está
prohibido en los nombres (`ProjectStore.Sanitize`) para que la key nunca sea ambigua al partirla.

La sesión pasó de `desk → string` a `desk → DeskAssignment(Project, Module)`. Cambiar de proyecto
LIMPIA el módulo (arrastrar "Plataforma" al cliente siguiente sería la confusión que vinimos a matar);
re-confirmar el MISMO proyecto lo conserva. El INI suma la sugerencia `desk_N_module`.

**Cada módulo lleva un COLOR propio** (auto-asignado de `ModulePalette`, ciclable con F3 en el
picker) y se pinta en overlay, barra y DeskPicker. Esto no es cosmética: la feature nació porque el
usuario le ERRABA de módulo al cambiar de pantalla — el texto obliga a leer, el color se percibe de
reflejo. La paleta esquiva a propósito el dorado de `DESK +N` y el verde de MAIN.

Gestión por dos caminos, a propósito: **2do paso del setter** (`Win+NumpadEnter` → confirmás
proyecto → aparece el picker) lo hace DESCUBRIBLE, y **`Win+NumpadDot`** cambia sólo el módulo sin
re-elegir proyecto (el uso frecuente). Sólo el atajo dedicado sería invisible; sólo el 2do paso te
obligaría a re-tipear el proyecto cada vez que rotás.

### 4. Scope de variables y notas — herencia de TRES niveles
`ProjectStore.ResolvePoolWithGlobal` / `GetNotes`: si el desk es un `DESK +N` **con proyecto activo
en la sesión** → scope de proyecto. Cualquier otro caso (MAIN/CONSOLES/MISCS, o DESK+ sin proyecto)
→ pool/notas **GLOBAL compartido**. Criterio único en `UseProjectScope`.

Dentro del scope de proyecto, las **variables heredan**: pool primaria = módulo (si hay), y se
anexan de SOLO-LECTURA el proyecto padre y la global, en ese orden (el orden de la lista ES el orden
de cercanía). Así el repo raíz y el Jira del cliente se cargan UNA vez en el proyecto y se ven desde
todos sus módulos. `FireDefault` respeta lo mismo: gana el predeterminado del módulo, y si no tiene
uno propio cae al del proyecto.

Las **notas NO heredan**: con módulo activo ves las del módulo y punto. Es deliberado — una nota es
una pizarra de trabajo; mezclarle la del proyecto la volvería un cajón de sastre.

### 5. Gobierno de ventanas (`WindowGovernor`)
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
| `desk_project_data.json` | JSON | Catálogo durable: `history`, `notes`, `paths` (key = proyecto **o** `"Proyecto/Módulo"`), `modules` (sub-scopes + color, por proyecto), `shared_notes`, `shared_paths`, `folder_notes`. |
| `settings.ini` | INI custom | `[Projects]` sugerencias (`desk_N` y `desk_N_module`), `[Pins]` `proc.exe=idx`, `[Restricted]` `idx=1`, `[Whitelist_IDX]` `proc.exe=1`. |
| `desktops.json` | JSON | `DesktopConfig`: lista `managed` + flag `autoCreate`. |
| `apps.json` | JSON | `AppsConfig`: apps de usuario (`name`, `exePath`, `args` con `{path}`). |
| `widgets.json` | JSON | `WidgetSettings`: qué widgets de la barra están activos (defaults: Clock + Ram + Ip). |
| `ports.json` | JSON | `PortStore`: catálogo GLOBAL de puertos/servicios locales (`title` + `port`). Estado/URL/proceso NO se persisten — se derivan en vivo. |
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
| `Win+Numpad 1/2/3` | Ir a MAIN / CONSOLES / MISCS (fila inferior, la más cómoda) |
| `Win+Numpad 4..9` | Ir a DESK +1 … +6 |
| `Win+Shift+`(navegación) | Enviar la ventana activa a ese desk **y seguirla** |
| `Win+NumpadEnter` | Setear el proyecto del desk actual (solo en `DESK +N`) → encadena el picker de **módulo** |
| `Win+Numpad .` (Del) | **Módulo** del desk: cambia sólo el sub-scope sin re-elegir proyecto (re-press → sin módulo) |
| `NumpadClear` (Numpad5, **sin Win**) | Abrir el **DeskPicker** (saltar a un proyecto de la sesión) |
| `Win+Numpad *` | **Variables** del proyecto/global (Paths Manager); re-press dispara el predeterminado |
| `Win+Numpad /` | **Notas** del proyecto/global |
| `Win+Numpad −` (Sub) | **Send-window picker** (mandar la ventana activa a un desk elegido) |
| `Win+Numpad +` (Add) | **Puertos / Servicios locales** (lista global de apps web por puerto; estado vivo 🟢/⚪, copiar localhost / IP de red, QR) |
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
| `Win+D` | **Interceptado**: reemplaza el "Mostrar escritorio" nativo por uno propio que minimiza todo **menos la barra** (ver ⚠ abajo) |

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
- `IpMonitor` — IP de LAN (`LocalIp`, local y sincrónica) + IP PÚBLICA (GET a un servicio de eco de
  IP: `api.ipify.org` con dos fallbacks). Dispara `Changed(prev, next)` sólo cuando cambia de verdad.
  **La cadencia es lo importante**: la pública NO se pollea seguido (sería maleducado con un tercero
  y te gana un rate-limit; además casi nunca cambia sola). El disparador principal es
  `NetworkChange.NetworkAddressChanged` — VPN arriba/abajo, cambio de red — **debounceado 4s** porque
  el SO manda varias notificaciones seguidas y el direccionamiento tarda en asentar (consultar en la
  primera devuelve la IP VIEJA). El timer de 15min queda sólo de RED, para el caso en que el ISP
  rote la IP sin que la interfaz local se entere. Valida que la respuesta PARSEE como IP: un portal
  cautivo de WiFi público devuelve HTML con 200 OK y sin ese chequeo lo pintaríamos en la barra.
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

### ⚠ Corolarios del mismo bug: el ESTADO de modificadores y el FOCO HUÉRFANO (no los deshagas)

La sección de arriba dice "tocar el z-order/foreground rompe el hook → el watchdog (`ReinstallHook`)
lo cura". Dos casos más, cazados con **instrumentación a archivo** (`Stopwatch` + `AppendAllText`,
filtrando a Win+numpad) + el usuario observando el PATRÓN, que son COROLARIOS de eso:

**1. El watchdog NO debe resetear `_winDown` a ciegas — descolgaba `Win+Numpad`.**
Síntoma: con Win MANTENIDA, rafagueando `Win+Numpad`, el combo se descolgaba tras unos saltos
(NumpadClear pasaba a abrir el DeskPicker PELADO). Patrón clave (lo encontró el usuario): se rafaguea
INFINITO entre desks VACÍOS, pero el **primer desk CON ventana** lo mata.
- **Causa**: `bar.OnBarZOrderChanged → ReinstallHook` NO sólo salta al salir de fullscreen — también
  salta al navegar a un desk CON ventana (la ventana toma foreground → la barra re-evalúa z-order →
  `ZOrderChanged`). El viejo `ReinstallHook` hacía `_winDown=false` CIEGO, descolgando el combo en
  pleno uso con la tecla físicamente apretada. Desks vacíos no mueven el foreground → no disparan el
  watchdog → por eso rafagueaban infinito.
- **Fix**: `HotkeyService.ReinstallHook` re-sincroniza los modificadores con su estado FÍSICO real
  (`WindowMethods.IsKeyPhysicallyDown` vía `GetAsyncKeyState`), NUNCA los fuerza a `false`. El log
  probó que `_winDown` caía a false SIN ningún Win-up en el hook (el hook seguía VIVO entregando
  teclas — por eso el NumpadClear abría el DeskPicker pelado: hook vivo + flag corrupto).
- **Teorías DESCARTADAS por el log (no repetir)**: NO hay Win-up sintético/inyectado del sistema; la
  tecla Win NO auto-repite en este teclado; NO era el "hook muerto" (entregaba todas las teclas).
  Filtrar `e.IsInjected` en el branch de Win EMPEORÓ las cosas y se revirtió.

**2. El FOCO HUÉRFANO cuelga el hook — al cerrar una ventana en un desk VACÍO.**
Síntoma: cerrás una ventana de la app (Esc) en un desk SIN otras ventanas → las hotkeys dejan de
responder. Si hay al menos otra app → anda (Windows le da el foreground a esa app). Las versiones
viejas mandaban el foco al ESCRITORIO al cerrar la última ventana; eso ERA un cambio de foco que
mantenía vivo el hook.
- **Causa**: sin una ventana real que tome el foreground, el foco queda HUÉRFANO
  (`GetForegroundWindow()` → 0) → mismo mecanismo: el hook deja de recibir teclas. Reinstalar el hook
  NO alcanza si el foco sigue en el aire.
- **Fix**: `WindowActivation.OnUtilityWindowClosed` (cableado en `App`; corre al cerrar CUALQUIER
  ventana abierta con `ShowFocused`) → DIFIERE ~80ms (para que el cierre se asiente) →
  `WindowMethods.RestoreForegroundOrDesktop(...)` (foco a la top window real del desk, o al ESCRITORIO
  si está vacío) → `ReinstallHook` de RED.
- **OJO**: el `SetForegroundWindow` al escritorio NO siempre prende (Windows es caprichoso con darle
  foco al escritorio por API) — el foreground puede quedar en 0 igual. La red real es el
  **`ReinstallHook` DIFERIDO**: reinstalar DESPUÉS de que el cierre se asentó restaura la entrega
  aunque el foco quede huérfano. Reinstalar INMEDIATO en el `Closed` (sin diferir) NO alcanza — fue el
  primer intento fallido.

**3. La red anti-foco-huérfano le ROBABA el foco a la ventana ENCADENADA.**
Síntoma: el setter de proyecto (`Win+NumpadEnter`) abre el picker de módulo y se cierra; el picker
aparecía al frente pero SIN foco de teclado — había que clickearlo.
- **Causa**: `RestoreForegroundOrDesktop` considera inválido CUALQUIER foreground de nuestro propio
  proceso (así cubre el caso de que sólo quede la barra) → 80ms después del cierre del setter le
  arrancaba el foreground al picker recién abierto y se lo daba a otra app. La ventana seguía al
  frente por ser `Topmost`, pero muerta de teclado. **Bug LATENTE desde siempre**: no se veía porque
  ninguna ventana utilitaria encadenaba a otra — apareció con la primera que lo hizo.
- **Fix**: guard en `App.HasFocusedUtilityWindow()` — si el foreground YA es una ventana utilitaria
  nuestra, el foco no está huérfano y la restauración NO corre. Excluye barra y overlay a propósito
  (ninguna toma foco de teclado: la AppBar no es activable y el overlay es `WS_EX_NOACTIVATE`), así
  que si el foreground fuera una de ellas el foco sí estaría perdido y la red debe disparar igual.
  El `ReinstallHook` se mantiene SIEMPRE (es idempotente y cubre el caso del foco realmente en el aire).
- **NO lo "simplifiques"**: cualquier ventana futura que abra a otra y se cierre depende de este guard.

**Regla transversal**: estos bugs de hook/foco se cazan SIEMPRE con instrumentación a archivo + el
usuario reproduciendo y observando el PATRÓN, NUNCA con teoría. Las teorías "lindas" fallaron varias
veces; el log y el patrón observado siempre ganaron.

### ⚠ "Mostrar escritorio" (Win+D / Win+M / gesto de 3 dedos) escondía la barra — DOS causas distintas

Síntoma: la barra desaparecía al apretar `Win+D` o al hacer el gesto del touchpad "minimizar todo"
(3 dedos hacia abajo). Resultó ser DOS problemas con DOS causas distintas, ambos cazados con
instrumentación a archivo (la regla transversal de arriba). NO los deshagas:

**Causa 1 — `Win+D` empuja el z-order, y esa guerra NO se puede ganar.**
Instrumentado: `Win+D` NO minimiza ni oculta la barra (cero `WM_SIZE`/`WM_SHOWWINDOW`/`StateChanged`);
le manda un `WM_WINDOWPOSCHANGING` con `hwndInsertAfter=HWND_BOTTOM` → la hunde detrás del escritorio.
Probado y DESCARTADO (no repetir): vetar el reorden con `SWP_NOZORDER`, redirigir a `HWND_TOPMOST`, y
re-subir a topmost diferido — Windows re-hunde la barra al instante (gana la guerra de z-order). La
taskbar del shell sobrevive porque está exenta; una AppBar de terceros NO (confirmado por Microsoft Q&A).
- **Fix**: interceptar `Win+D` en el hook de teclado (`HotkeyService`: `VK_D` con `_winDown` →
  `e.Suppress=true` + `_winComboConsumed=true` para enmascarar el Win-up → evento `ShowDesktopRequested`).
  En vez del Show Desktop nativo, hacemos el NUESTRO: `WindowMethods.MinimizeForeignTopLevel` minimiza
  todas las top-level reales del desk actual **salteando nuestro proceso** (la barra/overlay) y el
  escritorio/taskbar por clase. Cableado en `App` con la MISMA red anti-foco-huérfano que el cierre de
  ventanas (diferir 80ms → `RestoreForegroundOrDesktop` + `ReinstallHook`, porque al minimizar todo el
  foco queda huérfano y cuelga el hook). `Win+M` sobrevive solo (no toca tool windows/appbars).

**Causa 2 — el gesto de 3 dedos disparaba NUESTRA propia supresión de fullscreen (el bug real, no obvio).**
El gesto NO es teclado → el hook no lo puede interceptar. Pero el log reveló algo inesperado: la barra
NO se escondía por el z-order del gesto, sino porque **Windows manda `ABN_FULLSCREENAPP(true)` al
"mostrar escritorio"**, y nuestro handler de AppBar lo obedecía a ciegas (igual que con un juego real)
→ `SetFullscreenSuppressed(true)` → 750ms después NOSOTROS MISMOS bajábamos la barra con `ApplyZOrder`.
Clave del diagnóstico: aparecía `SetFullscreenSuppressed(True)` SIN el log de `IsForegroundFullscreenOnPrimary`
→ el suppress venía de `ABN_FULLSCREENAPP`, no del `FullscreenWatcher` (que ya excluye el escritorio por
geometría).
- **Fix**: en el handler de `ABN_FULLSCREENAPP` (`AppBarManager.WndProc`), **cross-check por geometría**:
  solo suprimimos si `IsForegroundFullscreenOnPrimary` confirma que hay de verdad una ventana tapando el
  monitor primario. En "mostrar escritorio" el foreground es el escritorio (excluido) → geometría false →
  NO suprimimos. En un fullscreen real la ventana cubre el monitor → geometría true → suprime igual. NO
  saques este cross-check: sin él, cualquier "mostrar escritorio" (gesto, botón de la taskbar) vuelve a
  esconder la barra. El fullscreen REAL (YouTube con F, juego) sigue ocultándola y reapareciendo al salir.

**Regla transversal (otra vez)**: la Causa 2 parecía ser "el gesto empuja el z-order" (teoría linda) y
era NUESTRO propio `ABN_FULLSCREENAPP` mal interpretado. Solo el log lo reveló. Instrumentá, no teorices.

### ⚠ Spawneo de Electron apps (VS Code) — env vars heredadas CONTAMINAN al child

Si esta app es spawneada desde un proceso DESCENDIENTE de VS Code (el shell del agente Claude Code,
terminal integrado, debugger F5 lanzado desde VS Code, etc.), HEREDA env vars que VS Code inyecta en
TODOS sus child processes. Las críticas, confirmadas con instrumentación en `LaunchApp.cs`:

- `ELECTRON_RUN_AS_NODE=1` → convierte a Code.exe en intérprete Node.js sin UI. **Esta es la asesina**.
- `VSCODE_IPC_HOOK=\\.\pipe\<uuid>-main-sock` → apunta al pipe interno del VS Code PADRE → handshake roto.
- `VSCODE_PID`, `VSCODE_NLS_CONFIG`, `VSCODE_ESM_ENTRYPOINT`, `VSCODE_CRASH_REPORTER_PROCESS_TYPE`,
  `VSCODE_CODE_CACHE_PATH`, `VSCODE_CWD`, `VSCODE_L10N_BUNDLE_LOCATION`, etc. (11 en total).

Síntoma: `Process.Start` devuelve PID válido, el stub Code.exe sale con **exit code 9**, no aparece
ninguna ventana, no queda nada en Task Manager. Manual desde cmd y el shortcut del Start Menu funcionan
porque su parent es `explorer.exe` → env limpia.

- **Fix obligatorio en CUALQUIER spawn de Electron app**: antes de `Process.Start`, iterar
  `ProcessStartInfo.Environment.Keys` y eliminar TODA key que empiece con `VSCODE_` o `ELECTRON_`
  (case-insensitive). Sumar `UseShellExecute=false` y `WorkingDirectory` en el home del user
  (`Environment.SpecialFolder.UserProfile`), NO en `bin\Debug` donde vive el exe. Replica el env
  "limpio" del shortcut del Start Menu.
- **Aplica a TODA app Electron** que agregues a "Abrir con" en el futuro (Discord, Slack, Postman,
  Obsidian, GitHub Desktop, etc.) — todas mueren igual con el env contaminado de VS Code.

**Teorías descartadas (NO repetir, las probamos las cuatro y todas fallaron)**:
1. Named pipe IPC stale del main zombie → se intentó matar procesos Code.exe vivos sin ventana. No
   ayudó: el problema NO eran los procesos, era el env.
2. Job Object del padre matando grandchildren → se intentó `cmd /c start ""` para forzar
   `CREATE_BREAKAWAY_FROM_JOB`. Sigue fallando porque el `cmd` también hereda el env.
3. MIC/integrity level mismatch → el manifest no eleva, ambos procesos corren a Medium.
4. Falta de console parent para el stub → Code.exe es GUI, no necesita console.

Solo el log mostró el env contaminado de un saque. Confirmación de la regla transversal: **bugs de
spawn que no tiran excepción → log a archivo + patrón observado, NUNCA teoría**.

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
