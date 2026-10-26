# Developer Notes

## Local Development

1. Build the plugin project (includes bundled web client and config UI assets):
   - `dotnet build apps/server-plugin/src/Jellycheckr.Server/Jellycheckr.Server.csproj`
2. Publish the plugin and create a zip artifact (`net10.0`):
   - `dotnet publish apps/server-plugin/src/Jellycheckr.Server/Jellycheckr.Server.csproj -c Release -f net10.0`
   - Zip output: `apps/server-plugin/artifacts/jellycheckr-server-plugin-<version>.zip`
   - The zip now includes `meta.json` (Jellyfin plugin metadata) required for reliable disable/uninstall actions in the Jellyfin UI.
3. Install the plugin zip into your local Jellyfin plugins path:
   - `pwsh ./tools/scripts/dev-install-plugin.ps1 ...`

Optional backend-only fast loop (skip frontend rebuilds):
- `dotnet build apps/server-plugin/src/Jellycheckr.Server/Jellycheckr.Server.csproj -p:JellycheckrBuildEmbeddedAssets=false`

## Release Automation

Stable releases publish from merges to `main` when the resolved Conventional Commit type indicates a version bump:
- `feat` => minor
- `fix`, `perf`, `refactor`, `revert`, `security`, `sec` => patch
- any type with `!` or `BREAKING CHANGE:` => major

Non-shipping commit types such as `build`, `chore`, `ci`, `docs`, `style`, and `test` stay opt-in. Use one of these when you need to force a release anyway:
- add `release:patch`, `release:minor`, or `release:major` to the PR
- run `Enterprise Release - Stage A` manually with `ref=<branch-or-sha>` and `bump_override=<patch|minor|major>`

## Repeatable Docker Harness (Pre-release Validation)

Use the harness to build plugin source against a pinned Jellyfin release image, run the container, and execute smoke checks.

Primary script:
- `pwsh ./tools/harness/scripts/Invoke-Harness.ps1`

Quick commands:
- Build only:
  - `pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode build -Version 12.3`
- Start container (build + up):
  - `pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode up -Version 12.3`
- One-command smoke run (build + up + checks + artifacts):
  - `pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode smoke -Version 12.3`
- Matrix run from `tools/harness/versions.json`:
  - `pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode matrix`
- Stop container:
  - `pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode down -Version 12.3`

Smoke checks include:
- Jellyfin readiness check (`/System/Info/Public`)
- Plugin web asset checks (`/Plugins/Aysw/web/*`)
  - `/Plugins/Aysw/web/jellycheckr-web.js`
  - `/Plugins/Aysw/web/jellycheckr-config-ui.js`
  - `/Plugins/Aysw/web/jellycheckr-config-ui.css`
  - `/Plugins/Aysw/web/jellycheckr-config-ui-host.html`
  - verifies `X-Content-Type-Options: nosniff` on each asset response
- Authenticated plugin config request (`/Plugins/Aysw/config`)
- Authenticated register sanity request (`/Plugins/Aysw/web-client/register`) expecting unresolved response with `reason=session_unresolved` when no active playback session exists
- Authenticated heartbeat sanity request (`/Plugins/Aysw/web-client/heartbeat`) expecting unresolved response with `reason=session_unresolved` when no active playback session exists
- Ownership enforcement checks expecting `403`:
  - `POST /Plugins/Aysw/sessions/<foreign>/ack`
  - `POST /Plugins/Aysw/sessions/<foreign>/interaction`
  - `POST /Plugins/Aysw/sessions/<foreign>/prompt-shown`
  - `POST /Plugins/Aysw/web-client/unregister` with a foreign session id
- Developer fallback playback check:
  - copies `tools/harness/test-video.mp4` into the harness container at `/media/harness/test-video.mp4`
  - creates/refreshes a Jellyfin `HarnessMedia` library from `/media/harness`
  - enables admin `DeveloperMode=true`, `DeveloperPromptAfterSeconds=15`, `ServerFallbackDryRun=true`
  - publishes `Sessions/Playing` + `Sessions/Playing/Progress` updates and verifies the log trigger reason `developer_mode_after_15s`
- Web UI injection registration check:
  - verifies `Registered Jellyfin Web index.html transformation for web client injection` is present in logs for `12.x`
  - verifies `/web/index.html` is served with the `jellycheckr-web-loader` script injected
  - records `skipped_unsupported_jellyfin_version` for any version without a pinned File Transformation package
- Plugin lifecycle regression check:
  - fetches `/Plugins`
  - resolves Jellycheckr by plugin id and uses the returned version for `POST /Plugins/<id>/<version>/Disable`
  - verifies `DELETE /Plugins/<id>/<version>` returns HTTP `204`

Artifacts:
- Written under `tools/harness/artifacts/`
- Includes smoke summary, API response payloads, and docker compose logs per run

Browser checks (`tools/harness/browser/README.md`) drive real Jellyfin Web against a harness container to cover the
web prompt, the dashboard config page, and real remote pause/stop commands, which the HTTP smoke run cannot.

### Version/Framework Mapping

`tools/harness/versions.json` lists Jellyfin Docker image tags. Jellyfin 12 publishes minor-only tags (`12.0`, `12.3`), not `12.3.0`.

The harness enforces the plugin framework and Jellyfin package version:
- `12.x` => `net10.0`, built against Jellyfin `12.0.0` packages

Every `12.x` run builds against `12.0.0`, the same packages (and `targetAbi`) the release ships with, so newer servers exercise the published binary.

If you add a new Jellyfin major line, update `Resolve-TargetFramework` and `Resolve-JellyfinPackageVersion` in `tools/harness/scripts/Invoke-Harness.ps1`. When File Transformation publishes a build for a newer Jellyfin minor, add it to the asset mapping in `tools/harness/docker/Dockerfile`.

### Harness Determinism Notes

- `up` runs with `--renew-anon-volumes` so each run starts from a clean Jellyfin `/config` state.
- `down` removes anonymous volumes to prevent stale plugin binaries/config from leaking into later runs.
- Harness sets deterministic server identity per Jellyfin version:
  - fixed `ServerName` via compose hostname (`jellycheckr-harness-<version>`)
  - fixed `ServerId` via `/config/data/device.txt` (version-derived MD5 in `Invoke-Harness.ps1`)
- On some `10.11.x` starts, Jellyfin reports `StartupWizardCompleted=true` with no users. Harness auth bootstrap now forces startup wizard mode and seeds the harness credentials automatically.
- Harness image auto-installs File Transformation `3.0.2.0` for `12.x`. It uses the `Release-12.0.0`, `Release-12.1.0`, or `Release-12.2.0` zip matching the Jellyfin minor; `12.3` and newer use the `12.2.0` build until a newer one is published.
- Harness requests authenticate with `Authorization: MediaBrowser ..., Token="<token>"`. Jellyfin 12 disables the legacy `X-Emby-Token` / `X-Emby-Authorization` headers and the `api_key` query parameter by default (`EnableLegacyAuthorization=false`).

## Testing When Server Is On TrueNAS SCALE

- Keep your TrueNAS Jellyfin as the playback target.
- Enable `developerMode=true` and set `developerPromptAfterSeconds` low (for example `5`) so prompt cycles happen quickly.
- Use web browser devtools console to verify:
  - module mounted
  - config fetch succeeded
  - ack calls return success
- Validate plugin-served web assets return HTTP 200:
  - `/Plugins/Aysw/web/jellycheckr-web.js`
  - `/Plugins/Aysw/web/jellycheckr-config-ui.js`
  - `/Plugins/Aysw/web/jellycheckr-config-ui.css`
  - `/Plugins/Aysw/web/jellycheckr-config-ui-host.html`
- If deployment to TrueNAS is cumbersome, validate behavior first in local docker Jellyfin, then deploy the same plugin zip to TrueNAS.

## Fast Prompt Toggle

Use helper script to set short-cycle test config on a reachable server:
- `pwsh ./tools/scripts/dev-enable-fast-mode.ps1 -BaseUrl http://127.0.0.1:5127 -Token dev-token -Seconds 5`

## Manual Plugin Install / "Repository" Message

If you install the server plugin manually (copy into `plugins/JellycheckrAysw`), the dashboard may show **"An error occurred while getting the plugin details from the repository."** when you open the plugin or its configure screen. That is expected: the plugin is not in the Jellyfin plugin catalog, so the dashboard's repo lookup fails. The plugin still works. Use **Configure** to open the built-in config page and change settings; nothing in the server logs is required for this.

If the Jellyfin UI shows 404s when disabling or uninstalling the plugin (`/Plugins/<id>/<version>/Disable` or `/Plugins/<id>/<version>`), verify the installed plugin folder includes `meta.json` and that its `version` matches the plugin DLL file version (for example `0.1.0.0`, not `0.1.0`).

Release note for the next build:
- The uninstall regression on Jellyfin `10.11.8` is fixed by aligning `meta.json.version` with the plugin DLL version.
- If you already installed a broken build, the plugin now repairs `meta.json` on startup, but Jellyfin still needs one additional restart before disable/uninstall actions use the corrected version.

## Plugin Debugging

- Use Jellyfin logs and filter for `Jellycheckr`.
- Enable `debugLogging` in admin config for detailed lifecycle traces.
- When calling plugin endpoints by hand on Jellyfin 12, send `Authorization: MediaBrowser Token="<token>"` (or the `ApiKey=<token>` query parameter). `X-Emby-Token` is ignored unless the server re-enables legacy authorization.
- Standalone dev harness auth:
  - Header: `X-Emby-Token: <token>`
  - Env var: `JELLYCHECKR_DEV_TOKEN` (default `dev-token`)
  - Optional role header for admin endpoints: `X-User-Role: Administrator`

## Web Module Debugging

- Module logs are prefixed with `[jellycheckr-web]`.
- Ensure `/Plugins/Aysw/web/jellycheckr-web.js` returns HTTP 200.
- Verify prompt behavior in autoplay series playback.

## Server Fallback (Native Clients) Debugging

- Ensure `enableServerFallback=true` for stock Android TV / Firestick testing.
- Keep at least one of `enableEpisodeCheck` or `enableTimerCheck` enabled.
- Start with `serverFallbackDryRun=true` to confirm triggers before sending pause/stop commands.
- Watch Jellyfin logs for `Server fallback trigger`, `pause initiated`, and `grace expired` messages.
- `reason=no_remote_control` (or `remoteControl=False` in dry run) means the client has no WebSocket connection, so Jellyfin cannot deliver pause/stop to it.
- Native clients do not show the Jellycheckr modal; fallback uses Jellyfin session telemetry and remote commands.

## Tested Compatibility

Track tested Jellyfin versions in release notes. Current target:
- Jellyfin server 12.x (plugin built against `12.0.0`, `targetAbi` `12.0.0.0`, .NET 10)
- jellyfin-web matching server release
- File Transformation `3.0.2.0` for the web prompt

Validated with the Docker harness smoke run on Jellyfin `12.3` and `12.0`.
