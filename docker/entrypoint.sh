#!/bin/sh
# Animarr container entrypoint.
#
# When ANIMARR_LLM_VULKAN=1 the built-in ("embedded") llama.cpp provider should
# use the GPU via Vulkan. The default image is CPU-only and lean; the Vulkan
# userspace (libvulkan1 + mesa-vulkan-drivers) is installed HERE, at boot, only
# when the flag is set. Downloaded .deb packages are cached on the data volume
# so restarts and offline boots work after the first successful online boot.
#
# Every failure here is non-fatal: the app probes for a working Vulkan device on
# startup and falls back to the CPU llama-server binary on its own. The first
# GPU-enabled boot needs network access; after that the cache covers offline.
set -e

if [ "$ANIMARR_LLM_VULKAN" = "1" ]; then
    if ! dpkg -s mesa-vulkan-drivers >/dev/null 2>&1; then
        CACHE=/app/data/vulkan-debs
        mkdir -p "$CACHE"
        echo "[entrypoint] ANIMARR_LLM_VULKAN=1 — ensuring Vulkan userspace drivers…"
        if ls "$CACHE"/*.deb >/dev/null 2>&1 && dpkg -i "$CACHE"/*.deb >/dev/null 2>&1; then
            echo "[entrypoint] installed Vulkan drivers from volume cache."
        elif apt-get update \
             && apt-get install -y --no-install-recommends --download-only \
                  -o Dir::Cache::archives="$CACHE" libvulkan1 mesa-vulkan-drivers \
             && dpkg -i "$CACHE"/*.deb; then
            echo "[entrypoint] installed Vulkan drivers from network (cached for next boot)."
        else
            echo "[entrypoint] WARNING: Vulkan install failed — the LLM will run on CPU."
        fi
    fi
fi

exec dotnet Animarr.Web.dll
