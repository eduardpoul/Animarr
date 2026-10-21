#!/bin/sh
# Animarr container entrypoint.
#
# The Vulkan userspace for the embedded llama.cpp provider (libvulkan1 +
# mesa-vulkan-drivers) is baked into the image. ANIMARR_LLM_VULKAN=1 is read by
# the app itself: with the flag set, a /dev/dri render node present and the
# Vulkan llama-server loading, it offloads to the GPU; otherwise it runs the CPU
# build. Older images apt-installed the drivers here on every new container and
# cached the .debs in /app/data/vulkan-debs; that cache is no longer read and the
# directory can be deleted.
#
# exec keeps dotnet as PID 1 so SIGTERM reaches it (clean llama-server and
# torrent shutdown).
set -e

exec dotnet Animarr.Web.dll
