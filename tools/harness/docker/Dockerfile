# syntax=docker/dockerfile:1.6
ARG JELLYFIN_VERSION=12.3
ARG JELLYFIN_PACKAGE_VERSION=12.0.0
ARG DOTNET_TARGET_FRAMEWORK=net10.0
ARG FILE_TRANSFORMATION_VERSION=3.0.2.0
ARG HARNESS_SERVER_ID=9f1d4f3cecb043f9b651c6b35acbbd7d

FROM node:24-bookworm-slim AS node-runtime

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS plugin-build
ARG JELLYFIN_PACKAGE_VERSION
ARG DOTNET_TARGET_FRAMEWORK

COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY --from=node-runtime /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

WORKDIR /src
COPY . .
RUN dotnet publish apps/server-plugin/src/Jellycheckr.Server/Jellycheckr.Server.csproj \
    -c Release \
    -f ${DOTNET_TARGET_FRAMEWORK} \
    -p:TargetFrameworks=${DOTNET_TARGET_FRAMEWORK} \
    -p:JellyfinPackageVersion=${JELLYFIN_PACKAGE_VERSION} \
    -p:JellycheckrCreatePluginZip=false \
    -o /out/plugin

FROM jellyfin/jellyfin:${JELLYFIN_VERSION}
ARG JELLYFIN_VERSION
ARG FILE_TRANSFORMATION_VERSION
ARG HARNESS_SERVER_ID

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl unzip \
    && rm -rf /var/lib/apt/lists/*

# File Transformation publishes one zip per Jellyfin minor; newer minors without their own
# build fall back to the latest published one.
RUN set -eux; \
    case "${JELLYFIN_VERSION}" in \
      12.0|12.0.*) file_transformation_asset="Release-12.0.0.zip" ;; \
      12.1|12.1.*) file_transformation_asset="Release-12.1.0.zip" ;; \
      12.*) file_transformation_asset="Release-12.2.0.zip" ;; \
      *) file_transformation_asset="" ;; \
    esac; \
    if [ -n "${file_transformation_asset}" ]; then \
        file_transformation_url="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases/download/${FILE_TRANSFORMATION_VERSION}/${file_transformation_asset}"; \
        mkdir -p /config/plugins/FileTransformation; \
        curl -fsSL "${file_transformation_url}" -o /tmp/file-transformation.zip; \
        unzip -oq /tmp/file-transformation.zip -d /config/plugins/FileTransformation; \
        rm -f /tmp/file-transformation.zip; \
    else \
        echo "Skipping File Transformation plugin install for Jellyfin ${JELLYFIN_VERSION}; no compatible harness package is pinned."; \
    fi

RUN mkdir -p /config/plugins/JellycheckrAysw
COPY --from=plugin-build /out/plugin/ /config/plugins/JellycheckrAysw/
RUN mkdir -p /config/data \
    && printf '%s' "${HARNESS_SERVER_ID}" > /config/data/device.txt
