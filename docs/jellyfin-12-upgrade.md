# Jellyfin 12 Upgrade Notes And Audit

Jellyfin 10.12 shipped as **Jellyfin 12.0** (September 2026; the "10." prefix was dropped) on .NET 10. 12.1, 12.2 and
12.3 followed. Plugins built for 10.x do not load on 12.x. This document records what the port changed, how it was
verified, and what the follow-up audit found.

## What Changed

| Area | Change | Why |
| --- | --- | --- |
| Server plugin | `net10.0`, `Jellyfin.Controller` / `Jellyfin.Model` `12.0.0`, `targetAbi` `12.0.0.0`; dropped explicit `System.Text.Json` / `System.Text.Encodings.Web` references | Jellyfin 12 runs on .NET 10; `targetAbi` is a minimum, so one build serves all 12.x servers; .NET 10 flags the redundant references (NU1510) |
| Web prompt + config UI | Send `Authorization: MediaBrowser Token="..."` instead of `X-Emby-Token` / `X-MediaBrowser-Token` | Jellyfin 12 disables legacy authorization by default; every web prompt API call returned 401 |
| User id resolution | Read Jellyfin's `Jellyfin-UserId` claim and normalize to `D`-format GUID; `Guid.Empty` (API key) means no user | Previously every plugin request fell through to a blocking `IAuthService.Authenticate(...).GetAwaiter().GetResult()` database lookup |
| Tests | Test project follows `JellyfinPackageVersion`; new `AuthenticatedUserIdResolverTests` | Overriding the version caused NU1605 downgrade errors; resolver had no coverage |
| CI / release | .NET 10 SDK, publish `-f net10.0`, default `TargetAbi` `12.0.0.0` | Match the new target |
| Harness | Jellyfin `12.3` / `12.0` images, plugin always built against `12.0.0` packages, File Transformation `3.0.2.0` per-minor zips, `Authorization` header auth, PowerShell 7 error-status handling, `/web/index.html` injection assertion | Legacy auth headers were rejected; PowerShell 7 threw on the expected 403s; registration logs alone did not prove injection works |
| Browser checks | New `tools/harness/browser/` Playwright scripts | Cover the web prompt, config page, and real remote commands that the HTTP smoke run cannot |

No C# Jellyfin API call needed changing: every member the plugin uses (`ISessionManager.Sessions`,
`SendPlaystateCommand`, `SendMessageCommand`, `SessionInfo`, `PlayerStateInfo`, `BaseItemDto`, `PlaystateRequest`,
`MessageCommand`, `IAuthService`, `BasePlugin<T>`, `IHasWebPages`, `IPluginServiceRegistrator`) is unchanged between
10.11.8 and 12.3. The 12.0 breaking interface changes (search, user manager, item repository, subtitles) do not touch
this plugin.

## Verification (Jellyfin 12.3 and 12.0)

| Check | 12.3 | 12.0 |
| --- | --- | --- |
| Unit tests (60) against `12.0.0` and `12.3.0` packages | pass | pass |
| Package validation (`Validate-PluginPackage.ps1`, `targetAbi` 12.0.0.0) | pass | n/a |
| Harness smoke (load, assets, auth, ownership 403s, developer fallback, injection + `index.html` loader, disable/uninstall) | pass | pass |
| Browser: web prompt shown, Continue acknowledged | pass | not run |
| Browser: web prompt Stop pauses and leaves the player view | pass | not run |
| Browser: config page load + save via `ApiClient.ajax` and via the `fetch()` fallback | pass | not run |
| Browser: native-client fallback sends real message, pause, and stop (after grace) | pass | not run |
| Legacy header control: `X-Emby-Token` -> 401, `Authorization: MediaBrowser` -> 200 | confirmed | not run |

File Transformation has no 12.3-specific build yet; its `Release-12.2.0` build loads and injects correctly on 12.3.

## Audit Findings

### Fixed

1. **Web prompt and config UI used disabled legacy auth headers** (`apps/web-client/src/api/client.ts`,
   `apps/config-ui/src/host.ts`). Switched to the `Authorization` header; verified in a real browser.
2. **User id resolution never matched Jellyfin's claim** (`AuthenticatedUserIdResolver`). Every request did a
   synchronous auth/database lookup on the request thread, and API-key callers resolved to user
   `00000000-0000-0000-0000-000000000000`. Now uses the `Jellyfin-UserId` claim, normalized to the `D` format the
   rest of the plugin compares against; the old lookup remains as a fallback.
3. **Test project pinned Jellyfin packages independently** of the plugin, so
   `/p:JellyfinPackageVersion=<newer>` failed restore with NU1605.
4. **Harness `Invoke-WebRequestAllowError` only worked on Windows PowerShell 5.1.** PowerShell 7 throws
   `HttpResponseException` for 4xx, so the expected-403 ownership checks crashed the run.
5. **Harness injection check only read a log line.** It now also asserts the served `/web/index.html` contains the
   loader, which catches a File Transformation build that registers but does not apply on a newer Jellyfin.

### Verified Safe

- Jellyfin 12 added `AssertCanControl` and `ControllingUserId` overwriting to `SendPlaystateCommand` /
  `SendGeneralCommand`. The dispatcher passes the target session as its own controller, which always passes; real
  message/pause/stop were confirmed in a browser session.
- jellyfin-web 12.3 still exposes `window.ApiClient` (same `jellyfin-apiclient` 1.11.0 as 10.11.8) and an identical
  `window.Dashboard`, and still serves legacy plugin configuration pages.
- `.headerButton-left` no longer exists in jellyfin-web 12.3; the Stop path falls back to `history.back()` and was
  verified to leave the player view.

### Open (pre-existing, not changed in this upgrade)

1. **Config page breaks under a Jellyfin base URL.** `Configuration/configPage.html` points the iframe at the absolute
   path `/Plugins/Aysw/web/jellycheckr-config-ui-host.html`. With `BaseUrl` set (for example `/jellyfin`), Jellyfin's
   `BaseUrlRedirectionMiddleware` redirects un-prefixed paths to the web app, so the iframe shows Jellyfin instead of
   the config UI. Fix: set the iframe `src` from `ApiClient.getUrl('Plugins/Aysw/web/jellycheckr-config-ui-host.html')`
   in the page script, keeping the absolute path as fallback. (The injected web loader already handles base URLs.)
2. **Synchronous auth fallback remains.** When the `Jellyfin-UserId` claim is missing (not observed on 12.x),
   `AuthenticatedUserIdResolver` still blocks on `IAuthService.Authenticate`. Making the resolver async would remove it.
3. **Remote commands report success even when nothing is delivered.** `ISessionManager.SendPlaystateCommand` /
   `SendMessageCommand` send through the session's WebSocket controllers; for a session without any (REST-only
   clients) Jellyfin returns without error, so `JellyfinSessionCommandDispatcher` logs `pauseSent=True` /
   `result=sent` and the fallback moves on to stop as if the pause landed. Checking
   `SessionInfo.SupportsMediaControl` (or `SessionControllers`) before dispatching would make the logs truthful and
   let the fallback skip sessions it cannot control.
4. **Harness compose file requires the harness-set environment.** Running `docker compose -f
   tools/harness/docker/compose.harness.yml build` directly passes empty build args (an empty
   `JellyfinPackageVersion` global property overrides the csproj default). Always go through `Invoke-Harness.ps1`.
5. **Node 20 is past end of life** (April 2026) but is still used by CI and the harness build stage. jellyfin-web 12
   itself builds with Node 24.
6. **Warnings:** `CS0108` for `Plugin.ApplicationPaths` (hides `BasePlugin.ApplicationPaths`), `CS1591` missing XML
   docs, and a `tsc --noEmit` error inside `@preact/signals-core` typings in `apps/config-ui`.
