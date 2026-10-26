// Opens the Jellycheckr dashboard config page, loads and saves the admin config.
// Usage: node config-page-check.js <base-url> <out-dir> [--force-fetch-fallback]
const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");

const [base, outDir, mode] = process.argv.slice(2);
const forceFallback = mode === "--force-fetch-fallback";
fs.mkdirSync(outDir, { recursive: true });
const result = { base, forceFallback, checks: {}, adminCalls: [] };

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.on("response", (response) => {
    const url = response.url();
    if (!/\/Plugins\/(Aysw|jellycheckr)\/admin\/config/i.test(url)) return;
    const headers = response.request().headers();
    result.adminCalls.push({
      method: response.request().method(),
      status: response.status(),
      authScheme: (headers.authorization || "").split(" ")[0] || null,
      sentLegacyToken: Boolean(headers["x-emby-token"] || headers["x-mediabrowser-token"])
    });
  });
  const shot = (name) => page.screenshot({ path: path.join(outDir, `${name}.png`), fullPage: true });

  try {
    await page.goto(`${base}/web/`, { waitUntil: "domcontentloaded" });
    const userField = page.getByLabel("User", { exact: true });
    await userField.waitFor({ state: "visible", timeout: 30000 });
    await userField.fill("harness");
    await page.getByLabel("Password", { exact: true }).fill("harness");
    await page.getByRole("button", { name: "Sign In" }).click();
    await page.waitForURL(/#\/home/, { timeout: 30000 });

    if (forceFallback) {
      // Give only the config iframe an ApiClient without ajax(), so host.ts must use its fetch fallback.
      await page.route("**/Plugins/Aysw/web/jellycheckr-config-ui-host.html*", async (route) => {
        const response = await route.fetch();
        const shim = "<script>window.ApiClient={accessToken:function(){return parent.ApiClient.accessToken();},"
          + "serverAddress:function(){return parent.ApiClient.serverAddress();}};</script>";
        const body = (await response.text()).replace("<head>", "<head>" + shim);
        await route.fulfill({ response, body });
      });
    }

    await page.goto(`${base}/web/#/configurationpage?name=${encodeURIComponent("Jellycheckr AYSW")}`);
    const frame = page.frameLocator("#jellycheckrConfigFrame");
    const saveButton = frame.locator("form button[type=submit]");
    await saveButton.waitFor({ state: "visible", timeout: 45000 });
    await page.waitForTimeout(1500);
    result.checks.loaded = result.adminCalls.some((c) => c.method === "GET" && c.status === 200);
    await shot("01-loaded");

    const toggle = frame.locator("form label[for]").first();
    await toggle.click();
    await saveButton.click();
    await page.waitForTimeout(2500);
    result.checks.saved = result.adminCalls.some((c) => c.method === "PUT" && c.status === 200);
    await shot("02-saved");
  } catch (err) {
    result.error = String(err && err.message ? err.message.split("\n")[0] : err);
    await shot("99-failure").catch(() => {});
  } finally {
    fs.writeFileSync(path.join(outDir, "result.json"), JSON.stringify(result, null, 2));
    await browser.close();
  }
})();
