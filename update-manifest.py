"""Add or replace one plugin version in manifest.json (Jellyfin plugin repository format).

Usage: python3 update-manifest.py <version> <zip-url> [changelog]
  version    release version, e.g. 1.0.0 (stored as 1.0.0.0, matching the plugin's meta.json)
  zip-url    download URL of the release zip, e.g.
             https://github.com/bobbleheadhobo/jellycheckr/releases/download/v1.0.0/Jellycheckr_1.0.0.zip

The zip is downloaded to compute the MD5 checksum Jellyfin verifies on install, and its meta.json
supplies targetAbi, so the manifest always matches the published file.
"""
import hashlib
import io
import json
import sys
import urllib.request
import zipfile
from datetime import datetime, timezone
from pathlib import Path

MANIFEST = Path(__file__).with_name("manifest.json")
PACKAGE = {
    "guid": "a53af988-9d8f-4a7c-8d5f-f902fd90e4bd",
    "name": "Jellycheckr AYSW",
    "description": "Are You Still Watching prompts for Jellyfin playback.",
    "overview": "Jellycheckr AYSW plugin with bundled web prompt UI and optional server fallback enforcement.",
    "owner": "bobbleheadhobo",
    "category": "General",
    "imageUrl": "https://raw.githubusercontent.com/bobbleheadhobo/jellycheckr/main/apps/server-plugin/src/logo.png",
}


def main(version, zip_url, changelog=""):
    with urllib.request.urlopen(zip_url, timeout=60) as response:
        data = response.read()
    meta = json.loads(zipfile.ZipFile(io.BytesIO(data)).read("meta.json").decode("utf-8-sig"))
    manifest_version = version if version.count(".") == 3 else version + ".0"
    if meta["version"] != manifest_version:
        sys.exit(f"zip meta.json version {meta['version']} does not match {manifest_version}")

    entry = {
        "version": manifest_version,
        "changelog": changelog or f"Release {version}",
        "targetAbi": meta["targetAbi"],
        "sourceUrl": zip_url,
        "checksum": hashlib.md5(data).hexdigest(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    packages = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else [dict(PACKAGE, versions=[])]
    package = next(p for p in packages if p["guid"] == PACKAGE["guid"])
    versions = [v for v in package["versions"] if v["version"] != manifest_version] + [entry]
    package["versions"] = sorted(versions, key=lambda v: [int(x) for x in v["version"].split(".")], reverse=True)
    MANIFEST.write_text(json.dumps(packages, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(entry, indent=2))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    main(*sys.argv[1:4])
