# VirtualDesktopAccessor.dll — patched build

The app ships a **patched** build of [Ciantic/VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor)
(Rust, branch `rust`, commit `bbed676`), not the published release.

## Why

Every virtual desktop **rename** (our `SetDesktopName`, or anyone renaming from Task View) corrupted the
heap of any process with `RegisterPostMessageHook` active:

- The shell calls `IVirtualDesktopNotification::VirtualDesktopNameChanged(desktop, HSTRING name)` into our
  process over RPC.
- Upstream declares `name: HSTRING` **by value**. In `windows-rs` an owned `HSTRING` is freed on drop, so the
  handler calls `WindowsDeleteString` on a string it does not own. The RPC stub then frees it again
  (`HSTRING_UserFree64`) → **double free**.
- Effect: silent heap corruption → later crash `0xc0000374` (heap corruption), and the shell stops
  delivering desktop-change notifications to the process. `VirtualDesktopWallpaperChanged` had the same bug.

The dynamic-desk launcher renames every desktop it creates, which is what exposed it.

Evidence (full PageHeap + cdb logging first-chance AVs, controlled create → rename ×2 → remove):

| DLL | AVs after 2 renames |
|---|---|
| Release 2024-12-16 | +2 |
| Upstream `bbed676`, unpatched | +2 |
| Upstream `bbed676` + patch | **0** |

## The patch

`fix-hstring-double-free.patch`: `name: HSTRING` → `name: ManuallyDrop<HSTRING>` in both notification
methods (`src/interfaces.rs` declaration + `src/listener.rs` impl). Same ABI (one pointer); Rust just stops
freeing the borrowed string. Not reported upstream as of 2026-09.

Building from `bbed676` also brings unreleased upstream fixes, notably #110 (crash during TLS teardown on
process exit) and #115 (memory leak in `GetAppUserModelId`).

## Rebuild

Requires Rust (`rustup`, `x86_64-pc-windows-msvc`) and the MSVC C++ linker.

```bash
bash native/VirtualDesktopAccessor/build.sh   # clones, checks out the commit, applies the patch, copies the DLL to the repo root
```

Rust builds are not bit-reproducible (they embed the build path), so re-run the rename check above after
rebuilding.
