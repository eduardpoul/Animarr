# Requires BuildKit with Dockerfile syntax 1.4+ (the default since Docker 23):
# the final stage uses `COPY --link` / `COPY --chmod`, which the legacy builder
# rejects with "unknown flag". On an older Docker, build with
# DOCKER_BUILDKIT=1 and add `# syntax=docker/dockerfile:1.7` as the first line.
#
# .NET images pinned to an exact patch + distro. The floating `10.0` tags
# meant a deploy reused whatever 10.0.x the build host had cached (no
# --pull), silently missing runtime security patches, and would jump to a
# new Ubuntu the day MCR flips the default — while several stages below
# assume Noble (glibc 2.39: the Vulkan llama build, the libva overlay). Bump
# both with each monthly .NET servicing release.
ARG DOTNET_SDK_TAG=10.0.402-noble
ARG DOTNET_RUNTIME_TAG=10.0.13-noble

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build

# Install Node.js 24 LTS (apt ships Node 18 which is too old for
# @tailwindcss/oxide 4.x used by Animarr.Web's stylesheet build).
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    curl -fsSL https://deb.nodesource.com/setup_24.x | bash - && \
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

# Install node deps for Animarr.UI (the Tailwind build target lives there
# since Phase 5 hard cutover stripped Razor + Tailwind from Animarr.Web).
# Platform-specific optional packages (@tailwindcss/oxide-linux-x64-gnu)
# are resolved by running `npm install` from inside the Linux container.
# Only package*.json are copied first, so this layer stays cached until the
# npm dependencies change instead of re-running on every source edit.
# (.dockerignore keeps the host's node_modules out of the COPY . . below.)
COPY ["src/Animarr.UI/package.json", "src/Animarr.UI/package-lock.json", "src/Animarr.UI/"]
WORKDIR "/src/src/Animarr.UI"
RUN npm install

WORKDIR /src
COPY . .

WORKDIR "/src/src/Animarr.UI"
# Explicitly recompile Tailwind. The MSBuild BuildTailwindCSS target has
# Inputs="Styles/*.css" Outputs="wwwroot/app.css" — when COPY brings both
# Styles/ source and an already-tracked wwwroot/app.css output into the
# image at the same layer timestamp, MSBuild's incremental check decides
# the output is up-to-date and skips the rebuild. Result: a stale compiled
# CSS overrides any changes the developer made to Styles/. Running the
# build explicitly here closes that hole — every Docker build emits fresh
# CSS regardless of what wwwroot/app.css the dev committed (or didn't).
RUN npm run css:build

# No separate `dotnet build` step: its -o /app/build output was never used,
# and `dotnet publish` below builds anyway (it was compiling everything twice).
WORKDIR "/src/src/Animarr.Web"

# Publish stage — produces the final layout including:
#   • Animarr.Web.dll (API + Razor + WASM static-file host)
#   • wwwroot/_framework/* (WASM bundle from Animarr.Web.Client)
#   • wwwroot/_content/Animarr.UI/* (RCL static web assets)
# `-r linux-x64 --self-contained false`: still framework-dependent (runs on the
# aspnet base), but NuGet native assets are resolved for this image's platform
# only — libe_sqlite3.so etc. land next to the DLLs instead of a runtimes/ tree
# for ~25 RIDs (win/osx/musl/arm/…, ~32 MB). Only Animarr.Web gets the RID; the
# WASM client keeps browser-wasm. (Don't add -r to the `dotnet restore` above:
# a restore-time RID does flow into the WASM client and fails.)
FROM build AS publish
# The commit the image is built from, appended to the version ("0.7.0+4a0e701",
# shown in Settings › About). The build context has no usable .git (worktrees),
# so pass it: docker build --build-arg ANIMARR_COMMIT=$(git rev-parse --short HEAD)
ARG ANIMARR_COMMIT=
RUN dotnet publish "Animarr.Web.csproj" -c Release -r linux-x64 --self-contained false \
        -o /app/publish /p:UseAppHost=false /p:SourceRevisionId=${ANIMARR_COMMIT}

# llama.cpp server binaries for the built-in ("embedded") LLM provider. We copy
# just the llama-server executable from the official images:
#   • CPU build — no Vulkan link, so it always runs (default, lean).
#   • Vulkan build — only used when ANIMARR_LLM_VULKAN=1 and a /dev/dri render
#     node is passed in. Its userspace (libvulkan1 + mesa-vulkan-drivers) is
#     baked into the runtime stage below.
# Both pinned to llama.cpp b11669 (= release v0.7.0): the CPU image by tag +
# digest, the Vulkan source checkout by the same tag, so the two builds match
# and a deploy can't silently pick up whatever upstream pushed that day.
# To bump: pick a `server-bNNNN` tag on ghcr.io/ggml-org/llama.cpp, update the
# tag + digest here and LLAMA_CPP_REF below.
FROM ghcr.io/ggml-org/llama.cpp:server-b11669@sha256:8ffe0d28ff5261cc2f78ca1f67db1515bc2529b904a3c25f5b24f4cedfc3411d AS llama-cpu

# Vulkan llama-server is built FROM SOURCE on the same Ubuntu 24.04 / glibc 2.39
# base as the runtime image. The prebuilt :server-vulkan image is linked against
# a newer glibc (2.43) and fails to load in dotnet/aspnet:10.0. Build tooling
# stays in this throwaway stage; only the binary + its .so reach the final image.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS llama-vulkan
ARG LLAMA_CPP_REF=b11669
RUN apt-get update && apt-get install -y --no-install-recommends \
        git cmake ninja-build build-essential \
        libvulkan-dev glslc glslang-tools spirv-headers spirv-tools && \
    rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch "$LLAMA_CPP_REF" https://github.com/ggml-org/llama.cpp /src/llama.cpp && \
    cmake -S /src/llama.cpp -B /src/llama.cpp/build -G Ninja \
        -DCMAKE_BUILD_TYPE=Release -DGGML_VULKAN=ON -DGGML_NATIVE=OFF -DLLAMA_CURL=OFF && \
    cmake --build /src/llama.cpp/build -j --target llama-server
# Collect the binary + its shared libs. `cp -P` keeps the soname symlink chain
# (libX.so -> libX.so.0 -> libX.so.0.N.M) as symlinks: plain cp dereferenced
# them into three full copies of every library (147 MB instead of ~49 MB).
# COPY --from preserves the symlinks into the final image.
RUN mkdir -p /opt/out && \
    cp /src/llama.cpp/build/bin/llama-server /opt/out/ && \
    find /src/llama.cpp/build -name '*.so*' -exec cp -Pn {} /opt/out/ \;

# FFmpeg stage — pinned static jellyfin-ffmpeg build
# -------------------------------------------------
# Ubuntu Noble's apt ffmpeg is 6.1.1, vulnerable to CVE-2026-8461 ("PixelSmash":
# heap out-of-bounds write in the MagicYUV decoder, CVSS 8.8), fixed upstream in
# 8.1.2 — and Ubuntu has shipped no backport, so we drop in a static build.
# Since 0.7.x that build is jellyfin-ffmpeg (https://github.com/jellyfin/jellyfin-ffmpeg):
# upstream FFmpeg 8.1.3 + ~100 media-server patches — HDR / Dolby Vision tone
# mapping on AMD/Intel/NVIDIA (tonemap_opencl/_vaapi/_cuda, libplacebo fixes,
# DV P5 reshaping, fast CPU `tonemapx`), `remove_dovi` / `remove_hdr10plus`
# bitstream options, DOVI side data carried into hlsenc/mpegtsenc, GPU subtitle
# burn-in, OpenCL deinterlace and AMD `hevc_vaapi` fixes. The "portable" build
# is fully static (like the BtbN one it replaces): hwaccel is dlopen'd at runtime
# against the driver libs the final stage installs. Pinned to a release tag +
# sha256 so the binary changes only when we consciously bump it.
#
# To bump: pick a newer tag at https://github.com/jellyfin/jellyfin-ffmpeg/releases
# (the `…_portable_linux64-gpl.tar.xz` asset; its sha256 is the release asset
# digest) and update FFMPEG_URL + FFMPEG_SHA256. The archive holds just
# `ffmpeg` + `ffprobe`. The `-version` check below fails the build if the archive
# is corrupt or the wrong arch.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_TAG} AS ffmpeg
ARG FFMPEG_URL=https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v8.1.3-1/jellyfin-ffmpeg_8.1.3-1_portable_linux64-gpl.tar.xz
ARG FFMPEG_SHA256=b86dc023c64a5a9a410e6e3a938a970460214fae78f31c1553c9f88f1c289b6a
RUN apt-get update &&     apt-get install -y --no-install-recommends curl xz-utils ca-certificates &&     rm -rf /var/lib/apt/lists/* &&     curl -fsSL -o /tmp/ffmpeg.tar.xz "$FFMPEG_URL" &&     echo "${FFMPEG_SHA256}  /tmp/ffmpeg.tar.xz" | sha256sum -c - &&     mkdir -p /opt/ffmpeg/bin &&     tar -xJf /tmp/ffmpeg.tar.xz -C /opt/ffmpeg/bin ffmpeg ffprobe &&     chmod +x /opt/ffmpeg/bin/ffmpeg /opt/ffmpeg/bin/ffprobe &&     /opt/ffmpeg/bin/ffmpeg -version

# libva ≥ 2.21 — the static ffmpeg calls vaMapBuffer2 (added in libva
# 2.21) on the sw-decode → hwupload → h264_vaapi path (legacy-codec transcode
# offload). Ubuntu Noble ships libva 2.20, and the lazily-bound symbol aborts
# ffmpeg (implib assert) the first time hwupload runs. Build 2.22 from source
# (a tiny meson project, libdrm the only dep) and overlay it over the distro
# libs in the final stage. Same soname (libva.so.2) — mesa's radeonsi VA
# driver keeps working, ABI is backward compatible.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_TAG} AS libva
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
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_TAG} AS final
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
#     • the static ffmpeg already includes h264_nvenc / hevc_nvenc encoders
#       (they're enabled at compile time, only the runtime libs are external).
#
# Deploy script (deploy.ps1) detects host capabilities and adds the matching
# docker run flags (`--device /dev/dri --group-add video` for VAAPI,
# `--gpus all` for NVIDIA). One image, any GPU.
# NOTE: ffmpeg/ffprobe are NOT installed from apt anymore — they come from the
# pinned static jellyfin-ffmpeg stage above (CVE-2026-8461 fix; see the COPY below). apt
# here installs only the hwaccel userspace those static binaries dlopen at runtime,
# plus tzdata so the viewer's X-Time-Zone (IANA id) resolves for the stats.
#
# Vulkan for the embedded LLM (ANIMARR_LLM_VULKAN=1): libvulkan1 + the Mesa
# Vulkan ICDs (radv / anv / lavapipe). These used to be apt-installed by the
# entrypoint at every new container's boot (~9 s from the network, 99 MB
# written to the container layer, offline boots relied on a .deb cache on the
# data volume). The heavy part (libllvm + mesa-libgallium) is already here via
# mesa-va-drivers, so baking them in costs only ~17 MB compressed. Being
# installed changes nothing unless the flag is set: the app still picks the
# Vulkan llama-server only with ANIMARR_LLM_VULKAN=1 and a /dev/dri render node.
#
# fontconfig + DejaVu (~3 MB): the static ffmpeg's libass finds fonts through
# fontconfig, and with none installed a burned-in SRT / ASS that brings no
# attached fonts drew no text at all. DejaVu covers Latin, Cyrillic and Greek.
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        mesa-va-drivers \
        intel-media-va-driver \
        vainfo \
        libva-drm2 libva2 \
        libgomp1 \
        libvulkan1 \
        mesa-vulkan-drivers \
        fontconfig \
        fonts-dejavu-core \
        tzdata \
        ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Layer order: least → most frequently changing. Every layer after a changed one
# is rebuilt and re-shipped (deploys `docker save` the image), so the app
# publish — new on every deploy — must be the LAST layer. Previously it sat
# before llama/entrypoint, so an app-only change re-shipped ~315 MB of
# unchanged layers.
#
# `COPY --link` makes a layer independent of the layers below it: when the apt
# layer changes (base bump), these copies are reused from cache as the same
# blobs instead of being re-executed with fresh directory mtimes (which made
# byte-identical ffmpeg/llama layers get new digests on every rebuild).
# Not used for /lib/x86_64-linux-gnu below: on Noble /lib is a symlink to
# usr/lib, and --link does not resolve symlinks in the destination path.

# Drop in the pinned static ffmpeg/ffprobe (see the FFmpeg stage above). They
# land in /usr/local/bin — ahead of /usr/bin on PATH — so the app's `ffmpeg`/
# `ffprobe` child processes resolve to the pinned jellyfin-ffmpeg build. COPY preserves
# the +x bits set during extraction.
COPY --link --from=ffmpeg /opt/ffmpeg/bin/ffmpeg /opt/ffmpeg/bin/ffprobe /usr/local/bin/

# Overlay libva 2.22 over Noble's 2.20 (see the libva stage above) — required
# by the static ffmpeg's hwupload path (vaMapBuffer2). Same soname, drop-in.
COPY --from=libva /opt/libva/lib/libva.so.2.2200.0 /lib/x86_64-linux-gnu/libva.so.2.2200.0
COPY --from=libva /opt/libva/lib/libva-drm.so.2.2200.0 /lib/x86_64-linux-gnu/libva-drm.so.2.2200.0
RUN ln -sf libva.so.2.2200.0 /lib/x86_64-linux-gnu/libva.so.2 && \
    ln -sf libva-drm.so.2.2200.0 /lib/x86_64-linux-gnu/libva-drm.so.2

# Built-in llama.cpp servers for the "embedded" LLM provider (see FROM aliases).
# The official llama-server is a thin launcher that dynamically loads sibling
# .so files in /app (libllama, libggml-*, libllama-server-impl, …), so we copy
# the WHOLE /app dir, not just the binary. CPU build always runs; the Vulkan
# build is only used with ANIMARR_LLM_VULKAN=1 (its Vulkan userspace is in the
# apt layer above). The app sets LD_LIBRARY_PATH to these dirs at launch.
# Both llama-server binaries are already executable in their source stages
# (the old `RUN chmod +x` layer here was an empty 4 kB diff).
COPY --link --from=llama-cpu    /app/     /opt/llama/cpu/
COPY --link --from=llama-vulkan /opt/out/ /opt/llama/vulkan/

# Entrypoint wrapper: execs the app as PID 1 so SIGTERM reaches it (clean
# llama-server and torrent shutdown). --chmod: git stores it as 0644.
COPY --link --chmod=755 docker/entrypoint.sh /entrypoint.sh

# Create data directory for SQLite
RUN mkdir -p /app/data && chmod 777 /app/data

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
# NVIDIA hosts (`--gpus all`): the container toolkit mounts only the compute +
# utility driver libs by default — without `video` there is no libnvcuvid /
# libnvidia-encode, so NVDEC + NVENC (and tonemap_cuda) fail inside the
# container. Ignored on hosts without an NVIDIA GPU.
ENV NVIDIA_DRIVER_CAPABILITIES=compute,video,utility

ENTRYPOINT ["/entrypoint.sh"]

# The app itself — keep this the last layer (see "Layer order" above).
COPY --link --from=publish /app/publish .
