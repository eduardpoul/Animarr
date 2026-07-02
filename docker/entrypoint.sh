#!/bin/sh
# Animarr container entrypoint.
#
# When ANIMARR_LLM_VULKAN=1 the built-in ("embedded") llama.cpp provider should
# use the GPU via Vulkan. The default image is CPU-only and lean; the Vulkan
# userspace (libvulkan1 + mesa-vulkan-drivers) is installed HERE, at boot, only
# when the flag is set. Downloaded .deb packages are cached on the data volume
# so restarts and offline boots work after the first successful online boot.
#
# The cache is only valid for the image that wrote it: a base-image bump can
# change the dependency set (this bit us — mesa grew a libxcb-shm0 dep the new
# base no longer preinstalled, dpkg -i wedged half-unpacked and poisoned every
# later apt call). So: verify the install actually LANDED, and on a stale cache
# roll dpkg back, drop the cache and redo a full apt install (which resolves
# the complete dep set, unlike the old --download-only + dpkg combo).
#
# Every failure here is non-fatal: the app probes for a working Vulkan device on
# startup and falls back to the CPU llama-server binary on its own. The first
# GPU-enabled boot needs network access; after that the cache covers offline.
set -e

# "Present in the dpkg database" is NOT "installed" — a failed dpkg -i leaves
# the package half-unpacked and dpkg -s still succeeds. Check the real status.
vulkan_ok() {
    dpkg -s mesa-vulkan-drivers 2>/dev/null | grep -q '^Status: install ok installed'
}

if [ "$ANIMARR_LLM_VULKAN" = "1" ] && ! vulkan_ok; then
    CACHE=/app/data/vulkan-debs
    mkdir -p "$CACHE"
    echo "[entrypoint] ANIMARR_LLM_VULKAN=1 — ensuring Vulkan userspace drivers…"

    # 1) Offline path: the .deb set a previous boot of THIS image cached.
    if ls "$CACHE"/*.deb >/dev/null 2>&1; then
        if dpkg -i "$CACHE"/*.deb >/dev/null 2>&1 && vulkan_ok; then
            echo "[entrypoint] installed Vulkan drivers from volume cache."
        else
            echo "[entrypoint] volume cache is stale/incomplete — dropping it, reinstalling from network."
            rm -f "$CACHE"/*.deb
            # Un-wedge dpkg (half-unpacked packages block every apt operation).
            dpkg --configure -a >/dev/null 2>&1 || true
        fi
    fi

    # 2) Network path: a real apt install resolves the FULL dependency set for
    #    this base image. Downloads are redirected to the volume cache and kept
    #    (the base's docker-clean hook only purges /var/cache/apt), so the next
    #    boot takes the offline path.
    if ! vulkan_ok; then
        if apt-get update \
           && apt-get install -y -f --no-install-recommends \
                -o Dir::Cache::archives="$CACHE" \
                -o APT::Keep-Downloaded-Packages=true \
                libvulkan1 mesa-vulkan-drivers \
           && vulkan_ok; then
            echo "[entrypoint] installed Vulkan drivers from network (cached for next boot)."
        else
            echo "[entrypoint] WARNING: Vulkan install failed — the LLM will run on CPU."
        fi
    fi
fi

exec dotnet Animarr.Web.dll
