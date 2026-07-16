# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install Node.js 22 LTS (apt ships Node 18 which is too old for
# @tailwindcss/oxide 4.x used by Animarr.Web's stylesheet build).
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    rm -rf /var/lib/apt/lists/*

# Install WASM build tooling — Animarr.Web.Client compiles to WebAssembly, and
# the .NET SDK image doesn't include wasm-tools by default. Without this the
# build of the WASM client (which Animarr.Web ProjectReferences for static-
# file hosting) fails with "missing wasm-tools workload".
RUN dotnet workload install wasm-tools

WORKDIR /src

# Copy all project files first so `dotnet restore` resolves the full graph
# without yanking the full source tree (faster layer cache hit on edits).
COPY ["src/Animarr.Shared/Animarr.Shared.csproj",       "src/Animarr.Shared/"]
COPY ["src/Animarr.UI/Animarr.UI.csproj",               "src/Animarr.UI/"]
COPY ["src/Animarr.Web.Client/Animarr.Web.Client.csproj","src/Animarr.Web.Client/"]
COPY ["src/Animarr.Web/Animarr.Web.csproj",             "src/Animarr.Web/"]
RUN dotnet restore "src/Animarr.Web/Animarr.Web.csproj"

COPY . .

# Install node deps for Animarr.UI (the Tailwind build target lives there
# since Phase 5 hard cutover stripped Razor + Tailwind from Animarr.Web).
# Platform-specific optional packages (@tailwindcss/oxide-linux-x64-gnu)
# are resolved by running `npm install` from inside the Linux container.
WORKDIR "/src/src/Animarr.UI"
RUN npm install
# Explicitly recompile Tailwind. The MSBuild BuildTailwindCSS target has
# Inputs="Styles/*.css" Outputs="wwwroot/app.css" — when COPY brings both
# Styles/ source and an already-tracked wwwroot/app.css output into the
# image at the same layer timestamp, MSBuild's incremental check decides
# the output is up-to-date and skips the rebuild. Result: a stale compiled
# CSS overrides any changes the developer made to Styles/. Running the
# build explicitly here closes that hole — every Docker build emits fresh
# CSS regardless of what wwwroot/app.css the dev committed (or didn't).
RUN npm run css:build

WORKDIR "/src/src/Animarr.Web"
RUN dotnet build "Animarr.Web.csproj" -c Release -o /app/build

# Publish stage — produces the final layout including:
#   • Animarr.Web.dll (API + Razor + WASM static-file host)
#   • wwwroot/_framework/* (WASM bundle from Animarr.Web.Client)
#   • wwwroot/_content/Animarr.UI/* (RCL static web assets)
FROM build AS publish
RUN dotnet publish "Animarr.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# llama.cpp server binaries for the built-in ("embedded") LLM provider. We copy
# just the llama-server executable from the official images:
#   • CPU build — no Vulkan link, so it always runs (default, lean).
#   • Vulkan build — only invoked after the entrypoint installs libvulkan1 at
#     runtime (ANIMARR_LLM_VULKAN=1). The binaries cost only tens of MB; the
#     heavy Vulkan userspace (mesa) is NOT baked in — it's installed on demand.
FROM ghcr.io/ggml-org/llama.cpp:server AS llama-cpu

# Vulkan llama-server is built FROM SOURCE on the same Ubuntu 24.04 / glibc 2.39
# base as the runtime image. The prebuilt :server-vulkan image is linked against
# a newer glibc (2.43) and fails to load in dotnet/aspnet:10.0. Build tooling
# stays in this throwaway stage; only the binary + its .so reach the final image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS llama-vulkan
RUN apt-get update && apt-get install -y --no-install-recommends \
        git cmake ninja-build build-essential \
        libvulkan-dev glslc glslang-tools spirv-headers spirv-tools && \
    rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 https://github.com/ggml-org/llama.cpp /src/llama.cpp && \
    cmake -S /src/llama.cpp -B /src/llama.cpp/build -G Ninja \
        -DCMAKE_BUILD_TYPE=Release -DGGML_VULKAN=ON -DGGML_NATIVE=OFF -DLLAMA_CURL=OFF && \
    cmake --build /src/llama.cpp/build -j --target llama-server && \
    mkdir -p /opt/out && \
    cp /src/llama.cpp/build/bin/llama-server /opt/out/ && \
    find /src/llama.cpp/build -name '*.so*' -exec cp -n {} /opt/out/ \;

# FFmpeg stage — pinned static build (security fix for CVE-2026-8461)
# -------------------------------------------------------------------
# Ubuntu Noble's apt ffmpeg is 6.1.1, vulnerable to CVE-2026-8461 ("PixelSmash":
# heap out-of-bounds write in the MagicYUV decoder, CVSS 8.8), fixed upstream in
# 8.1.2 — and Ubuntu has shipped no backport. We instead drop in the static
# n8.1.2 build from BtbN/FFmpeg-Builds: the Linux build the official
# ffmpeg.org/download page recommends, maintained by FFmpeg upstream developer
# Timo Rothenpieler ("BtbN"). Pinned to a dated autobuild tag + sha256 so the
# binary is reproducible and changes only when we consciously bump it. The gpl
# static binaries link only glibc (verified via ldd); hwaccel (vaapi/nvenc/qsv)
# is dlopen'd at runtime against the driver libs the final stage installs.
#
# To bump: pick a newer tag at https://github.com/BtbN/FFmpeg-Builds/releases
# and update FFMPEG_URL + FFMPEG_SHA256. The `-version` check below fails the
# build if the archive is corrupt or the wrong arch.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS ffmpeg
ARG FFMPEG_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-23-13-52/ffmpeg-n8.1.2-linux64-gpl-8.1.tar.xz
ARG FFMPEG_SHA256=0c6772b77fdbf127cc1498eca39a40e20b88817f36b66d553cebcfcca32b6d78
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl xz-utils ca-certificates && \
    rm -rf /var/lib/apt/lists/* && \
    curl -fsSL -o /tmp/ffmpeg.tar.xz "$FFMPEG_URL" && \
    echo "${FFMPEG_SHA256}  /tmp/ffmpeg.tar.xz" | sha256sum -c - && \
    mkdir -p /opt/ffmpeg/bin && \
    tar -xJf /tmp/ffmpeg.tar.xz -C /tmp && \
    cp /tmp/ffmpeg-*/bin/ffmpeg /tmp/ffmpeg-*/bin/ffprobe /opt/ffmpeg/bin/ && \
    /opt/ffmpeg/bin/ffmpeg -version

# libva ≥ 2.21 — the static BtbN ffmpeg calls vaMapBuffer2 (added in libva
# 2.21) on the sw-decode → hwupload → h264_vaapi path (legacy-codec transcode
# offload). Ubuntu Noble ships libva 2.20, and the lazily-bound symbol aborts
# ffmpeg (implib assert) the first time hwupload runs. Build 2.22 from source
# (a tiny meson project, libdrm the only dep) and overlay it over the distro
# libs in the final stage. Same soname (libva.so.2) — mesa's radeonsi VA
# driver keeps working, ABI is backward compatible.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS libva
ARG LIBVA_URL=https://github.com/intel/libva/archive/refs/tags/2.22.0.tar.gz
ARG LIBVA_SHA256=467c418c2640a178c6baad5be2e00d569842123763b80507721ab87eb7af8735
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl ca-certificates build-essential meson ninja-build pkg-config libdrm-dev && \
    rm -rf /var/lib/apt/lists/* && \
    curl -fsSL -o /tmp/libva.tar.gz "$LIBVA_URL" && \
    echo "${LIBVA_SHA256}  /tmp/libva.tar.gz" | sha256sum -c - && \
    tar -xzf /tmp/libva.tar.gz -C /tmp && \
    cd /tmp/libva-2.22.0 && \
    meson setup build --prefix=/opt/libva --libdir=lib -Ddefault_library=shared \
        -Ddriverdir=/usr/lib/x86_64-linux-gnu/dri && \
    ninja -C build install

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# ffmpeg + ffprobe — needed by /api/video (MKV → fragmented-MP4 remux for
# in-browser playback), /api/probe (track listing for player UI selectors),
# and /api/subtitle (extracting embedded subtitle tracks as VTT/ASS).
# Stream-copy remux is near-zero CPU; only transcode hits cycles.
#
# Hardware-acceleration user-space libs — we bundle drivers for every common
# vendor so a single image works on any host GPU without rebuilding:
#
#   AMD/Intel-Gen9-older VAAPI:
#     • mesa-va-drivers       — Mesa's open-source VA-API drivers (radeonsi
#                               for AMD GCN/RDNA, i965 for older Intel)
#     • libva-drm2 / libva2   — VA-API runtime
#     • vainfo                — diagnostic (`docker exec animarr vainfo`)
#
#   Intel Gen11+ (Iris/Xe/Arc) — needs the newer iHD driver:
#     • intel-media-va-driver — replaces i965 for modern Intel iGPU,
#                               supports HEVC encode + AV1 on Arc
#
#   NVIDIA NVENC:
#     • libs come from the HOST at runtime via nvidia-container-toolkit
#       (mounted into the container when `--gpus all` is set). We don't
#       bake `libnvidia-encode-*` into the image because the version has
#       to match the host driver — and the toolkit handles that mounting.
#     • ffmpeg from Debian already includes h264_nvenc / hevc_nvenc encoders
#       (they're enabled at compile time, only the runtime libs are external).
#
# Deploy script (deploy.ps1) detects host capabilities and adds the matching
# docker run flags (`--device /dev/dri --group-add video` for VAAPI,
# `--gpus all` for NVIDIA). One image, any GPU.
# NOTE: ffmpeg/ffprobe are NOT installed from apt anymore — they come from the
# pinned static BtbN stage above (CVE-2026-8461 fix; see the COPY below). apt
# here installs only the hwaccel userspace those static binaries dlopen at runtime.
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        mesa-va-drivers \
        intel-media-va-driver \
        vainfo \
        libva-drm2 libva2 \
        libgomp1 \
        libvulkan1 \
        ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Drop in the pinned static ffmpeg/ffprobe (see the FFmpeg stage above). They
# land in /usr/local/bin — ahead of /usr/bin on PATH — so the app's `ffmpeg`/
# `ffprobe` child processes resolve to the patched 8.1.2 build. COPY preserves
# the +x bits set during extraction.
COPY --from=ffmpeg /opt/ffmpeg/bin/ffmpeg /opt/ffmpeg/bin/ffprobe /usr/local/bin/

# Overlay libva 2.22 over Noble's 2.20 (see the libva stage above) — required
# by the static ffmpeg's hwupload path (vaMapBuffer2). Same soname, drop-in.
COPY --from=libva /opt/libva/lib/libva.so.2.2200.0 /lib/x86_64-linux-gnu/libva.so.2.2200.0
COPY --from=libva /opt/libva/lib/libva-drm.so.2.2200.0 /lib/x86_64-linux-gnu/libva-drm.so.2.2200.0
RUN ln -sf libva.so.2.2200.0 /lib/x86_64-linux-gnu/libva.so.2 && \
    ln -sf libva-drm.so.2.2200.0 /lib/x86_64-linux-gnu/libva-drm.so.2

# Create data directory for SQLite
RUN mkdir -p /app/data && chmod 777 /app/data

COPY --from=publish /app/publish .

# Built-in llama.cpp servers for the "embedded" LLM provider (see FROM aliases).
# The official llama-server is a thin launcher that dynamically loads sibling
# .so files in /app (libllama, libggml-*, libllama-server-impl, …), so we copy
# the WHOLE /app dir, not just the binary. CPU build always runs; the Vulkan
# build is only invoked after the entrypoint installs libvulkan1 at boot
# (ANIMARR_LLM_VULKAN=1). The app sets LD_LIBRARY_PATH to these dirs at launch.
COPY --from=llama-cpu    /app/     /opt/llama/cpu/
COPY --from=llama-vulkan /opt/out/ /opt/llama/vulkan/
RUN chmod +x /opt/llama/cpu/llama-server /opt/llama/vulkan/llama-server

# Entrypoint wrapper: optionally installs the Vulkan userspace at boot, then
# execs the app as PID 1 so SIGTERM reaches it (clean llama-server shutdown).
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["/entrypoint.sh"]
