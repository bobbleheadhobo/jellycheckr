// Drives Jellyfin Web against a seeded harness server and checks the Jellycheckr web prompt end to end.
// Usage: node web-prompt-check.js <base-url> <seed.json> <out-dir> [continue|stop]
const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");

const [base, seedPath, outDir, action = "continue"] = process.argv.slice(2);
const seed = JSON.parse(fs.readFileSync(seedPath, "utf8"));
fs.mkdirSync(outDir, { recursive: true });

const pluginCalls = [];
const consoleLines = [];
const result = { base, action, checks: {} };

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

  page.on("console", (msg) => {
    const text = msg.text();
    if (text.includes("jellycheckr")) consoleLines.push(`[${msg.type()}] ${text}`);
  });
  page.on("response", async (response) => {
    const url = response.url();
    if (!url.includes("/Plugins/Aysw/")) return;
    const headers = response.request().headers();
    pluginCalls.push({
      method: response.request().method(),
      path: new URL(url).pathname,
      status: response.status(),
      authScheme: (headers.authorization || "").split(" ")[0] || null,
      sentLegacyToken: Boolean(headers["x-emby-token"] || headers["x-mediabrowser-token"])
    });
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
    await shot("01-home");

    await page.waitForFunction(() => Boolean(window.JellycheckrAysw), null, { timeout: 30000 });
    result.checks.runtimeLoaded = true;

    await page.goto(`${base}/web/#/details?id=${seed.itemId}`);
    const playButton = page.locator(".mainDetailButtons .btnPlay:visible, .detailButton.btnPlay:visible").first();
    await playButton.click({ timeout: 30000 });
    await page.waitForFunction(() => {
      const video = document.querySelector("video");
      return video && !video.paused && video.currentTime > 1;
    }, null, { timeout: 60000 });
    result.checks.playbackStarted = true;
    await shot("02-playing");

    const continueButton = page.locator(".jellycheckr-continue");
    await continueButton.waitFor({ state: "visible", timeout: 60000 });
    result.checks.promptShown = true;
    await shot("03-prompt");

    if (action === "stop") {
      await page.locator(".jellycheckr-stop").click();
      await continueButton.waitFor({ state: "hidden", timeout: 15000 });
      await page.waitForFunction(() => {
        const video = document.querySelector("video");
        return !video || video.paused;
      }, null, { timeout: 15000 });
      result.checks.playbackStopped = true;
      await page.waitForTimeout(3000);
      result.checks.leftPlayerView = !page.url().includes("#/video");
      result.finalUrl = page.url();
      result.checks.stopAcknowledged = pluginCalls.some((c) => c.path.endsWith("/ack") && c.status === 200);
      await shot("04-after-stop");
    } else {
      await continueButton.click();
      await continueButton.waitFor({ state: "hidden", timeout: 15000 });
      await page.waitForTimeout(1500);
      result.checks.continueAcknowledged = pluginCalls.some((c) => c.path.endsWith("/ack") && c.status === 200);
      await shot("04-after-continue");
    }
  } catch (err) {
    result.error = String(err && err.message ? err.message.split("\n")[0] : err);
    await shot("99-failure").catch(() => {});
  } finally {
    result.pluginCalls = pluginCalls;
    result.consoleSample = consoleLines.slice(0, 40);
    fs.writeFileSync(path.join(outDir, "result.json"), JSON.stringify(result, null, 2));
    await browser.close();
  }
})();
