# Jellycheckr plugin repository

Jellyfin plugin repository for the `bobbleheadhobo/jellycheckr` builds.

In Jellyfin: **Dashboard > Plugins > Repositories > +**, then add:

```
https://raw.githubusercontent.com/bobbleheadhobo/jellycheckr/plugin-repo/manifest.json
```

Install **Jellycheckr AYSW** from the Catalog and restart Jellyfin.

## Adding a release

After the release workflow publishes a new version on `main`:

```bash
python3 update-manifest.py <version> https://github.com/bobbleheadhobo/jellycheckr/releases/download/v<version>/Jellycheckr_<version>.zip "<changelog>"
```

The script downloads the zip, takes `targetAbi` from its `meta.json`, adds the MD5 checksum Jellyfin verifies on install, and keeps older versions in the list. Commit and push `manifest.json`.
