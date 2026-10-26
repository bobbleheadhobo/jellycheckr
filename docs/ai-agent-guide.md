# AI Agent Guide

Orientation for AI coding agents (and humans) working on Jellycheckr. Read this before changing code.
`AGENTS.md` holds the coding rules; this file holds the project knowledge.

## What This Project Is

Jellycheckr is an "Are You Still Watching?" plugin for Jellyfin **12.x** (built for .NET 10, `targetAbi` `12.0.0.0`).

- Jellyfin Web gets an injected script that shows a blocking prompt during playback.
- Native clients (Android TV, Fire TV, ...) are handled server-side: the plugin watches Jellyfin sessions and sends
  message / pause / stop remote commands.
- An admin config page is embedded in the Jellyfin dashboard.

The project is pre-release: no backwards compatibility is required, but do not break working behavior.

## Repository Map

| Path | What lives there |
| --- | --- |
| `apps/server-plugin/src/Jellycheckr.Server` | The Jellyfin plugin (C#). `Plugin.cs` entry, `PluginServiceRegistrator.cs` DI, `Controllers/` REST API under `/Plugins/Aysw` (alias `/Plugins/jellycheckr`), `Services/` session tracking and fallback enforcement, `Infrastructure/` logging, web asset serving, index.html injection |
| `apps/server-plugin/tests/Jellycheckr.Server.Tests` | xUnit tests for the plugin |
| `apps/web-client` | TypeScript bundle injected into Jellyfin Web (`jellycheckr-web.js`): prompt UI, state machine, player hooks, API client |
| `apps/config-ui` | Preact + Tailwind admin page bundle (`jellycheckr-config-ui.js/.css`), hosted in an iframe from `Configuration/configPage.html` |
| `packages/contracts` | Shared request/response contracts (C# file is compiled into the plugin; TS mirror; JSON schema; OpenAPI fragment) |
| `tools/harness` | Docker harness: builds the plugin into a Jellyfin image and runs HTTP smoke checks (`scripts/Invoke-Harness.ps1`) |
| `tools/harness/browser` | Playwright checks that drive real Jellyfin Web (see its `README.md`) |
| `.github/workflows`, `.github/scripts` | PR validation, prerelease, two-stage release automation driven by Conventional Commits |
| `docs/` | `architecture.md`, `api.md`, `config.md`, `dev-notes.md` (build/harness/release details), `jellyfin-12-upgrade.md` (upgrade notes + audit) |

The plugin build compiles both frontends (npm) and copies them into `web/` next to the DLL; the plugin serves them
from `/Plugins/Aysw/web/*`. Only `configPage.html` is an embedded resource.

## Toolchain And Commands

Requirements: .NET 10 SDK, Node.js 24 with npm (CI and the harness build use 24), PowerShell 7 (`pwsh`) for harness and release scripts, Docker for
the harness, Python 3 + Playwright for browser checks.

```bash
# Server tests (skip the npm frontend build for speed)
dotnet test apps/server-plugin/tests/Jellycheckr.Server.Tests/Jellycheckr.Server.Tests.csproj -c Release /p:JellycheckrBuildEmbeddedAssets=false

# Same tests compiled against a newer Jellyfin package (forward-compat check; applies to plugin + tests)
dotnet test apps/server-plugin/tests/Jellycheckr.Server.Tests/Jellycheckr.Server.Tests.csproj -c Release /p:JellycheckrBuildEmbeddedAssets=false /p:JellyfinPackageVersion=12.3.0

# Frontends
(cd apps/web-client && npm ci && npm test && npx tsc -p tsconfig.json --noEmit)
(cd apps/config-ui && npm ci && npm run build:bundle)

# Release-style package (zip + meta.json) and the CI validator
dotnet publish apps/server-plugin/src/Jellycheckr.Server/Jellycheckr.Server.csproj -c Release -f net10.0 -o /tmp/pub /p:Version=1.0.0 /p:JellycheckrArtifactsDir=/tmp/art
pwsh .github/scripts/Validate-PluginPackage.ps1 -ZipPath /tmp/art/Jellycheckr_1.0.0.zip -PublishDirectory /tmp/pub \
  -ExpectedPackageVersion 1.0.0 -ExpectedManifestVersion 1.0.0.0 -ExpectedAssemblyVersion 1.0.0.0 \
  -ExpectedTargetAbi 12.0.0.0 -ExpectedZipFileName Jellycheckr_1.0.0.zip

# Docker harness smoke run against a real Jellyfin (tags are minor-only: 12.0, 12.3, ...)
pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode smoke -Version 12.3
pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode down -Version 12.3
```

Known noise: `CS1591` (missing XML docs) and `CS0108` (`Plugin.ApplicationPaths` hides the base member) warnings are
pre-existing. `npx tsc --noEmit` in `apps/config-ui` reports an error inside `@preact/signals-core` typings
(`Disposable`); the bundle build does not type-check and is unaffected.

## Jellyfin Compatibility Contract

- **Single version knob:** `JellyfinPackageVersion` (default `12.0.0`) in `Jellycheckr.Server.csproj`, mirrored in the
  test project. `targetAbi` in `meta.json` is derived from it (`12.0.0.0`), and `.github/scripts/Get-ReleaseContext.ps1`
  reads it from the csproj. `targetAbi` is a *minimum* server version, so building against `12.0.0` produces one zip
  that loads on every 12.x server. Do not raise it unless you need an API that only exists in a later 12.x release.
- **Framework:** `net10.0` only. Jellyfin 12 does not load 10.x plugins and vice versa.
- **Web prompt dependency:** the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
  plugin (`3.x` for Jellyfin 12) rewrites `index.html`. Without it only native-client fallback works.

### Upgrading To A New Jellyfin Release (checklist)

1. Read the server release notes' plugin / API sections (`https://github.com/jellyfin/jellyfin/releases`).
2. Diff the Jellyfin source files listed in "Integration Points" below between the old and new tags
   (`https://raw.githubusercontent.com/jellyfin/jellyfin/<tag>/<path>`; tags look like `v12.3`). Reflection-based
   reads will not fail to compile when a member is renamed - they silently return `null`.
3. Run the tests with `/p:JellyfinPackageVersion=<new>`.
4. Add the Docker tag to `tools/harness/versions.json`; extend `Resolve-TargetFramework` /
   `Resolve-JellyfinPackageVersion` (new major only) and the File Transformation asset mapping in
   `tools/harness/docker/Dockerfile` (new minor). Run the smoke harness.
5. Run the browser checks in `tools/harness/browser/` - jellyfin-web login and player DOM change between releases.
6. Only for a new major: bump the TFM, `JellyfinPackageVersion`, workflow `dotnet-version` / `-f`, and the
   `TargetAbi` fallback in `Get-ReleaseContext.ps1`, then update README compatibility text.

## Integration Points (where Jellyfin upgrades break things)

### Authentication

- Jellyfin 12 disables legacy auth by default (`EnableLegacyAuthorization=false`): the `X-Emby-Token`,
  `X-MediaBrowser-Token`, `X-Emby-Authorization` headers, the `Emby` scheme, and the `api_key` query parameter are
  ignored (401). Always send `Authorization: MediaBrowser Token="<token>"` (optionally with
  `Client=`, `Device=`, `DeviceId=`, `Version=` parameters) or the `ApiKey=` query parameter.
  - `apps/web-client/src/api/client.ts` and `apps/config-ui/src/host.ts` build this header from `window.ApiClient.accessToken()`.
  - The harness builds it in `New-AuthorizationHeaders`.
- **User id format:** `AuthenticatedUserIdResolver` reads Jellyfin's `Jellyfin-UserId` claim (an `N`-format GUID) and
  normalizes it to the `D` format (`xxxxxxxx-xxxx-...`) used everywhere else in the plugin (session snapshots, session
  state, ownership checks). API keys carry `Guid.Empty` and resolve to "no user" (web-client endpoints return 403).
  Keep every user id comparison in `D` format. If the claim is ever missing, the resolver awaits
  `IAuthService.Authenticate` instead (`ResolveAsync`; never block on it).
- Admin endpoints check the `Administrator` role claim (`AyswAdminController.IsAdmin`).

### Session Data (read via reflection)

`Services/ServerFallbackSessionSnapshotProvider.cs` reads Jellyfin types by property name so it tolerates version
drift. Members it relies on (verify on upgrade):

- `MediaBrowser.Controller/Session/SessionInfo.cs`: `Id`, `UserId` (Guid), `UserName`, `Client`, `DeviceName`,
  `DeviceId`, `IsActive`, `NowPlayingItem`, `PlayState`, `LastActivityDate`, `LastPlaybackCheckIn`, `LastPausedDate`
- `MediaBrowser.Model/Session/PlayerStateInfo.cs`: `IsPaused`, `PositionTicks`
- `MediaBrowser.Model/Dto/BaseItemDto.cs`: `Id` (Guid), `Name`, `Type` (`BaseItemKind`, compared as `"Episode"`),
  `MediaType`, `SeriesId` (Guid?)

Guids are stringified with `ToString()` (`D` format) - comparisons are only valid against other `D`-format values.

### Remote Commands

`Services/JellyfinSessionCommandDispatcher.cs` calls `ISessionManager.SendPlaystateCommand` /
`SendMessageCommand` with the target session id as *both* controlling and target session. Since Jellyfin 12 these
calls run `AssertCanControl(session, controllingSession)` and overwrite `PlaystateRequest.ControllingUserId`; a
session controlling itself always passes. Commands are delivered through the session's controllers (the client's
WebSocket). For a session without one (REST-only, like the harness's simulated playback) Jellyfin returns without
error and sends nothing, so the dispatcher checks `SessionInfo.SessionControllers` for a controller with
`SupportsMediaControl` (`CanReceiveCommands`) and reports `false` instead of a false success. The fallback skips such
sessions with decision reason `no_remote_control` (re-checked every tick); dry run still logs the trigger, with
`remoteControl=False`. The harness smoke run keeps `ServerFallbackDryRun=true` for that reason, and the real-command
path is covered by `tools/harness/browser/native-fallback-check.js`.

Relevant Jellyfin source: `Emby.Server.Implementations/Session/SessionManager.cs`,
`MediaBrowser.Model/Session/PlaystateRequest.cs`, `MediaBrowser.Model/Session/MessageCommand.cs`.

### Web Injection

- `Services/WebUiInjectionRegistrationService.cs` finds `Jellyfin.Plugin.FileTransformation.PluginInterface` by
  reflection and calls `RegisterTransformation(JObject)` (payload built with `JObject.Parse(string)`), retrying every
  15 s until File Transformation loads. The callback is `JellycheckrWebIndexTransform.Transform`, which inserts a
  loader `<script id="jellycheckr-web-loader">` before `</body>`. The loader resolves the server base path from the
  page URL, so it works behind a reverse-proxy base URL.
- The harness asserts both the registration log line and that `/web/index.html` actually contains the loader.

### jellyfin-web Surface Used By The Frontends

- Globals: `window.ApiClient` (`accessToken()`, `deviceId()`, `getUrl()`, `ajax()`, `serverAddress()`; from
  `jellyfin-apiclient` 1.11.0, set in jellyfin-web `src/lib/jellyfin-apiclient/ServerConnections.js`) and
  `window.Dashboard` (`src/utils/dashboard.js`, kept for plugins). Both are present in jellyfin-web 12.3.
- DOM: the playing `<video>` element; exit-player fallbacks (`.btnBack`, then `history.back()`).
- Dashboard plugin pages: legacy route `#/configurationpage?name=<plugin name>` loading `configPage.html`
  (a `data-role="page"` div with an iframe whose `src` the page script sets from
  `ApiClient.getUrl('Plugins/Aysw/web/jellycheckr-config-ui-host.html')`, so a server base URL is included). jellyfin-web
  runs the page's inline script; without it the iframe stays blank.

## Verification Layers

| Layer | Command | Covers | Does not cover |
| --- | --- | --- | --- |
| Unit tests | `dotnet test ...`, `npm test` (web-client) | Decision engine, session observer, controllers, config, manifest self-heal, auth user id resolution | Real Jellyfin types/behavior |
| Package | `dotnet publish` + `Validate-PluginPackage.ps1` | Zip layout, `meta.json` versions and `targetAbi` | Runtime loading |
| Harness smoke | `Invoke-Harness.ps1 -Mode smoke` | Plugin loads in real Jellyfin, assets, auth, ownership 403s, developer fallback trigger (dry run), injection, disable/uninstall | Browser UI, real remote commands |
| Browser checks | `tools/harness/browser/*` | Web prompt Continue/Stop, config page load/save (both request paths), real message/pause/stop | Mobile/TV clients |

Run the harness and browser checks for any change touching auth, Jellyfin APIs, frontends, or the Jellyfin version.

## Running In A Restricted Cloud Sandbox

Cloud agent containers may lack `dotnet`, `pwsh`, and a running Docker daemon, and may route HTTPS through an
intercepting proxy. What worked:

- .NET: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir <dir>`, then set
  `DOTNET_ROOT=<dir>` and add it to `PATH`.
- PowerShell: `dotnet tool install --tool-path <dir> PowerShell` (needs `DOTNET_ROOT` set to run).
- Docker: start the daemon with `dockerd &` if `/var/run/docker.sock` is missing.
- Proxy + TLS interception: containers cannot reach the internet directly. Build images with `--network host` and pass
  `--build-arg HTTPS_PROXY/https_proxy/NO_PROXY/no_proxy`. Base images must trust the proxy CA: build local wrappers
  of `mcr.microsoft.com/dotnet/sdk:10.0` and `jellyfin/jellyfin:<tag>` that add the CA to the system store, switch apt
  sources to `https://`, and set `NODE_EXTRA_CA_CERTS` (Node ignores the system store, so `npm ci` hangs without it),
  then tag them with the original names so the harness Dockerfile picks them up. Build the harness image directly with
  `docker build -f tools/harness/docker/Dockerfile -t jellycheckr/harness:<tag> .` (pass the same build args the
  harness passes) and run the harness with `-SkipBuild`. Keep these workarounds out of the repo.
- GitHub web pages and API may be blocked while `raw.githubusercontent.com` and `git clone` over HTTPS work; use them
  to read Jellyfin / jellyfin-web / File Transformation sources at a tag.

## Conventions

- Commit and PR titles use Conventional Commits; they drive release versioning (`feat` minor, `fix` patch, `!` or
  `BREAKING CHANGE:` major; see `docs/dev-notes.md`).
- Files under `tools/harness` (`*.ps1`, `Dockerfile`, `versions.json`) are UTF-8 **with BOM**; preserve it when editing
  with scripts. Everything uses LF line endings.
- Harness `Invoke-WebRequestAllowError` must work on PowerShell 7 (uses `-SkipHttpErrorCheck`) and Windows
  PowerShell 5.1 (catches `WebException`).
- Follow `AGENTS.md` for C# style.

## Known Issues And Follow-ups

See the audit section of `docs/jellyfin-12-upgrade.md` for the current list (pre-existing issues that were found but
intentionally not changed during the Jellyfin 12 upgrade).
