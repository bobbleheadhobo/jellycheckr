"""Seed a fresh harness Jellyfin: admin user, media library, Jellycheckr developer mode.

Usage: python3 -I seed.py <base-url> <container-id> <video-path> [config-overrides-json]
Prints a JSON line with token, userId, itemId, serverId on stdout; progress goes to stderr.
Uses the harness credentials (harness / harness) and the Jellyfin 12 Authorization header.
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

BASE, CONTAINER, VIDEO = sys.argv[1], sys.argv[2], sys.argv[3]
OVERRIDES = json.loads(sys.argv[4]) if len(sys.argv) > 4 else {}
CLIENT = 'MediaBrowser Client="BrowserProbe", Device="Probe", DeviceId="browser-probe-seed", Version="1.0.0"'


def call(method, path, body=None, token=None):
    auth = CLIENT + (f', Token="{token}"' if token else "")
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method, headers={"Authorization": auth})
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            raw = resp.read().decode("utf-8-sig")
            return resp.status, (json.loads(raw) if raw.strip() else None)
    except urllib.error.HTTPError as err:
        return err.code, err.read().decode("utf-8-sig", "replace")[:200]


def log(msg):
    print(msg, file=sys.stderr, flush=True)


for _ in range(90):
    try:
        status, info = call("GET", "/System/Info/Public")
        if status == 200 and isinstance(info, dict) and info.get("Version"):
            break
    except OSError:
        pass
    time.sleep(2)
else:
    sys.exit("Jellyfin did not become ready")
log(f"public info: wizardCompleted={info.get('StartupWizardCompleted')} version={info.get('Version')}")

if not info.get("StartupWizardCompleted"):
    log(f"GET /Startup/User -> {call('GET', '/Startup/User')[0]}")
    log(f"POST /Startup/User -> {call('POST', '/Startup/User', {'Name': 'harness', 'Password': 'harness'})[0]}")
    log(f"POST /Startup/Complete -> {call('POST', '/Startup/Complete')[0]}")

status, auth = call("POST", "/Users/AuthenticateByName", {"Username": "harness", "Pw": "harness"})
if status != 200:
    sys.exit(f"login failed: {status} {auth}")
token, user_id = auth["AccessToken"], auth["User"]["Id"]

subprocess.run(["docker", "exec", CONTAINER, "mkdir", "-p", "/media/harness"], check=True)
subprocess.run(["docker", "cp", VIDEO, f"{CONTAINER}:/media/harness/test-video.mp4"], check=True, capture_output=True)
log(f"create library -> {call('POST', '/Library/VirtualFolders?name=HarnessMedia&paths=%2Fmedia%2Fharness&collectionType=movies&refreshLibrary=true', token=token)[0]}")

status, cfg = call("GET", "/Plugins/Aysw/admin/config", token=token)
if status != 200:
    sys.exit(f"admin config read failed: {status} {cfg}")
cfg.update(Enabled=True, DeveloperMode=True, DeveloperPromptAfterSeconds=15, ServerFallbackDryRun=True, MinimumLogLevel="Information")
cfg.update(OVERRIDES)
log(f"PUT admin config -> {call('PUT', '/Plugins/Aysw/admin/config', cfg, token=token)[0]}")

log(f"library refresh -> {call('POST', '/Library/Refresh', token=token)[0]}")
item_id = None
for _ in range(60):
    status, items = call("GET", f"/Users/{user_id}/Items?Recursive=true&IncludeItemTypes=Movie,Video&Limit=5", token=token)
    if status == 200 and items["Items"]:
        item_id = items["Items"][0]["Id"]
        break
    time.sleep(2)
else:
    sys.exit("harness media item was not indexed")

print(json.dumps({"token": token, "userId": user_id, "itemId": item_id, "serverId": info.get("Id")}))
