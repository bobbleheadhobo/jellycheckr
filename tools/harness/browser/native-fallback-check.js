// Plays in Jellyfin Web with the Jellycheckr web runtime blocked, so the server treats the session like a
// native client and must send real DisplayMessage / Pause / Stop commands over the Jellyfin WebSocket.
// Usage: node native-fallback-check.js <base-url> <seed.json> <out-dir>
const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");

const [base, seedPath, outDir] = process.argv.slice(2);
const seed = JSON.parse(fs.readFileSync(seedPath, "utf8"));
fs.mkdirSync(outDir, { recursive: true });

const result = { base, checks: {}, timeline: [] };
const mark = (event) => result.timeline.push({ atMs: Date.now() - startedAt, event });
let startedAt = Date.now();

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  let blockedRuntimeRequests = 0;
  await page.route("**/Plugins/Aysw/web/jellycheckr-web.js*", (route) => {
    blockedRuntimeRequests += 1;
    return route.abort();
  });
  const shot = (name) => page.screenshot({ path: path.join(outDir, `${name}.png`) });

  try {
    await page.goto(`${base}/web/`, { waitUntil: "domcontentloaded" });
    const userField = page.getByLabel("User", { exact: true });
    await userField.waitFor({ state: "visible", timeout: 30000 });
    await userField.fill("harness");
    await page.getByLabel("Password", { exact: true }).fill("harness");
    await page.getByRole("button", { name: "Sign In" }).click();
    await page.waitForURL(/#\/home/, { timeout: 30000 });

    await page.goto(`${base}/web/#/details?id=${seed.itemId}`);
    await page.locator(".mainDetailButtons .btnPlay:visible, .detailButton.btnPlay:visible").first().click({ timeout: 30000 });
    await page.waitForFunction(() => {
      const video = document.querySelector("video");
      return video && !video.paused && video.currentTime > 1;
    }, null, { timeout: 60000 });
    startedAt = Date.now();
    mark("playing");
    result.checks.runtimeBlocked = blockedRuntimeRequests > 0;

    // Server pause: the <video> stays mounted but is paused by a remote Playstate command.
    await page.waitForFunction(() => {
      const video = document.querySelector("video");
      return video && video.paused && !video.ended;
    }, null, { timeout: 90000 });
    mark("paused_by_server");
    result.pausedAtSeconds = await page.evaluate(() => document.querySelector("video").currentTime);
    result.checks.pausedByServer = true;
    result.checks.messageToastSeen = await page
      .locator(".toast, .toastContainer")
      .filter({ hasText: "still watching" })
      .count()
      .then((count) => count > 0)
      .catch(() => false);
    await shot("01-paused");

    // Server stop after the pause grace period: playback view is torn down.
    await page.waitForFunction(() => !document.querySelector("video") || !location.hash.startsWith("#/video"), null, {
      timeout: 90000
    });
    mark("stopped_by_server");
    result.checks.stoppedByServer = true;
    await shot("02-stopped");
  } catch (err) {
    result.error = String(err && err.message ? err.message.split("\n")[0] : err);
    await shot("99-failure").catch(() => {});
  } finally {
    fs.writeFileSync(path.join(outDir, "result.json"), JSON.stringify(result, null, 2));
    await browser.close();
  }
})();
