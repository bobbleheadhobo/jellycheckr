# Browser Verification (Jellyfin Web)

The Docker harness smoke run (`tools/harness/scripts/Invoke-Harness.ps1`) exercises the plugin over HTTP only.
These scripts drive a real Chromium through Jellyfin Web against a harness container to cover what the smoke run cannot:
the injected web prompt, the dashboard config page, and real remote pause/stop commands.

Run them whenever you change `apps/web-client`, `apps/config-ui`, auth headers, or move to a new Jellyfin release
(jellyfin-web's login form and player DOM change between releases).

## Requirements

- A running harness container with the plugin installed (see below)
- Python 3, Node.js, and Playwright with Chromium
  - If `playwright` is installed globally, export `NODE_PATH=$(npm root -g)` before running the scripts
  - Otherwise install it outside the repo (`npm install playwright && npx playwright install chromium` in a scratch
    directory) and export `NODE_PATH=<scratch>/node_modules`

## Steps

```bash
# 1. Start a fresh container. Do not reuse a smoke-run container: the smoke run ends by disabling and
#    uninstalling Jellycheckr (lifecycle check), so the plugin's assets return 404 afterwards. One `up`
#    container serves every step below, including a base URL test (change BaseUrl, `docker restart`).
pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode up -Version 12.3
C=$(docker ps -q --filter name=jellycheckrharness123)
OUT=tools/harness/artifacts/browser   # git-ignored

# 2. Seed: admin user harness/harness, HarnessMedia library, developer mode (prompt after 15s, fallback dry run)
mkdir -p $OUT
python3 -I tools/harness/browser/seed.py http://127.0.0.1:58096 $C tools/harness/test-video.mp4 > $OUT/seed.json

# 3. Web prompt: plays the video, waits for the modal, clicks Continue (or Stop)
node tools/harness/browser/web-prompt-check.js http://127.0.0.1:58096 $OUT/seed.json $OUT/prompt-continue continue
node tools/harness/browser/web-prompt-check.js http://127.0.0.1:58096 $OUT/seed.json $OUT/prompt-stop stop

# 4. Dashboard config page: load + save, then again forcing the fetch() fallback in apps/config-ui/src/host.ts
node tools/harness/browser/config-page-check.js http://127.0.0.1:58096 $OUT/config
node tools/harness/browser/config-page-check.js http://127.0.0.1:58096 $OUT/config-fetch --force-fetch-fallback

# 5. Native-client fallback with real commands: re-seed with dry run off, then play with the web runtime blocked
python3 -I tools/harness/browser/seed.py http://127.0.0.1:58096 $C tools/harness/test-video.mp4 \
  '{"EnableServerFallback": true, "ServerFallbackDryRun": false, "ServerFallbackPauseBeforeStop": true, "ServerFallbackPauseGraceSeconds": 10, "ServerFallbackSendMessageBeforePause": true, "DeveloperPromptAfterSeconds": 5}' \
  > $OUT/seed-native.json
node tools/harness/browser/native-fallback-check.js http://127.0.0.1:58096 $OUT/seed-native.json $OUT/native

# 6. Tear down
pwsh ./tools/harness/scripts/Invoke-Harness.ps1 -Mode down -Version 12.3
```

Each script writes `result.json` and screenshots into its output directory. A passing run has every entry in
`checks` set to `true`, no `error`, and (for the prompt/config checks) no plugin call with an HTTP error status or
`sentLegacyToken: true`.

## What Each Script Proves

| Script | Proves |
| --- | --- |
| `web-prompt-check.js` | Loader injected into `index.html`, `window.JellycheckrAysw` mounts, register/interaction/prompt-shown/ack calls succeed with `Authorization: MediaBrowser`, modal appears, Continue acks / Stop pauses and leaves the player view |
| `config-page-check.js` | Dashboard route `#/configurationpage?name=Jellycheckr AYSW` renders the iframe, admin config GET/PUT succeed via `ApiClient.ajax` and via the `fetch()` fallback |
| `native-fallback-check.js` | With the web runtime blocked, the server sends DisplayMessage, Pause, then Stop after the grace period over Jellyfin's WebSocket (`ISessionManager` remote control path) |

## Notes

- `seed.py` must run against a container whose startup wizard has not been completed by another client; if login
  fails, recreate the container (`-Mode down` then `-Mode up`).
- The test video is about 30 seconds long; keep developer-mode thresholds well below that.
- Jellyfin 12 sign-in uses labelled `User` / `Password` fields and a `Sign In` button; update the selectors if a
  future jellyfin-web release changes the login form.
