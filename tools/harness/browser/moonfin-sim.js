// Scratch simulator: talks to Jellyfin the way Moonfin-Core (Android TV) does, per its source:
// - REST auth: Authorization: MediaBrowser Client="Moonfin for Android TV", Device, DeviceId, Version, Token
// - POST /Sessions/Capabilities/Full with SupportsMediaControl and DisplayMessage
// - WebSocket /socket?ApiKey=<token>, KeepAlive every 30s, honours Playstate Pause/Unpause/Stop
// - POST /Sessions/Playing, then /Sessions/Playing/Progress every 5s with PositionTicks + IsPaused
// Usage: node moonfin-sim.js <base-url> <item-id> <max-seconds>
const [base, itemId, maxSecondsArg] = process.argv.slice(2);
const maxSeconds = Number(maxSecondsArg || 120);
const client = 'MediaBrowser Client="Moonfin for Android TV", Device="SimTV", DeviceId="moonfin-sim-1", Version="1.0.0"';
const started = Date.now();
const t = () => Number(((Date.now() - started) / 1000).toFixed(1));
const result = { events: [], checks: {} };
const log = (event, extra = {}) => result.events.push({ t: t(), event, ...extra });

let token = "";
const auth = () => (token ? `${client}, Token="${token}"` : client);
async function call(method, path, body) {
  const res = await fetch(base + path, {
    method,
    headers: { Authorization: auth(), ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${text.slice(0, 120)}`);
  return text ? JSON.parse(text) : null;
}

(async () => {
  let socket;
  let progressTimer;
  let keepAliveTimer;
  try {
    const login = await call("POST", "/Users/AuthenticateByName", { Username: "harness", Pw: "harness" });
    token = login.AccessToken;
    await call("POST", "/Sessions/Capabilities/Full", {
      PlayableMediaTypes: ["Audio", "Video"],
      SupportsMediaControl: true,
      SupportedCommands: ["DisplayMessage", "SetVolume", "Mute", "Unmute", "ToggleMute", "GoHome", "SendString"],
    });

    await new Promise((resolve, reject) => {
      socket = new WebSocket(`${base.replace(/^http/, "ws")}/socket?ApiKey=${token}`);
      socket.onopen = () => { log("socket_open"); resolve(); };
      socket.onerror = (e) => reject(new Error("socket error " + (e.message || "")));
    });
    keepAliveTimer = setInterval(() => socket.send(JSON.stringify({ MessageType: "KeepAlive" })), 30000);

    const playSessionId = "sim-" + Date.now();
    let positionTicks = 0;
    let isPaused = false;
    let lastTick = Date.now();
    let stopped = false;

    const progress = async () => {
      const now = Date.now();
      if (!isPaused) positionTicks += (now - lastTick) * 10000;
      lastTick = now;
      await call("POST", "/Sessions/Playing/Progress", {
        ItemId: itemId, PlaySessionId: playSessionId, PositionTicks: positionTicks, IsPaused: isPaused, IsMuted: false,
      });
    };

    const done = new Promise((resolve) => {
      socket.onmessage = async (msg) => {
        const data = JSON.parse(msg.data);
        if (data.MessageType === "ForceKeepAlive" || data.MessageType === "KeepAlive") return;
        if (data.MessageType === "GeneralCommand" && data.Data?.Name === "DisplayMessage") {
          log("display_message", { text: data.Data.Arguments?.Text });
        } else if (data.MessageType === "Playstate") {
          const command = String(data.Data?.Command || "").toLowerCase();
          log("playstate_" + command);
          if (command === "pause") { await progress(); isPaused = true; await progress(); }
          if (command === "unpause" || command === "play") { isPaused = false; await progress(); }
          if (command === "stop") {
            stopped = true;
            await call("POST", "/Sessions/Playing/Stopped", { ItemId: itemId, PlaySessionId: playSessionId, PositionTicks: positionTicks });
            log("reported_stopped");
            resolve();
          }
        }
      };
      setTimeout(resolve, maxSeconds * 1000);
    });

    await call("POST", "/Sessions/Playing", {
      ItemId: itemId, PlaySessionId: playSessionId, PositionTicks: 0, IsPaused: false, CanSeek: true, PlayMethod: "DirectPlay",
    });
    log("playing_started");
    progressTimer = setInterval(() => progress().catch((e) => log("progress_error", { error: e.message })), 5000);

    const sessions = await call("GET", "/Sessions?deviceId=moonfin-sim-1");
    result.session = sessions[0] && {
      id: sessions[0].Id, client: sessions[0].Client, supportsMediaControl: sessions[0].SupportsMediaControl,
      supportsRemoteControl: sessions[0].SupportsRemoteControl,
    };

    await done;
    const names = result.events.map((e) => e.event);
    result.checks = {
      socketOpened: names.includes("socket_open"),
      sessionControllable: !!result.session?.supportsMediaControl,
      messageReceived: names.includes("display_message"),
      pauseReceived: names.includes("playstate_pause"),
      stopReceived: names.includes("playstate_stop"),
      stopAfterPause: names.indexOf("playstate_stop") > names.indexOf("playstate_pause"),
      stopped,
    };
  } catch (err) {
    result.error = String(err && err.message ? err.message : err);
  } finally {
    clearInterval(progressTimer);
    clearInterval(keepAliveTimer);
    try { socket && socket.close(); } catch (e) { /* ignore */ }
    console.log(JSON.stringify(result, null, 2));
  }
})();
