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

WORKDIR "/src/src/Animarr.Web"
RUN dotnet build "Animarr.Web.csproj" -c Release -o /app/build

# Publish stage — produces the final layout including:
#   • Animarr.Web.dll (API + Razor + WASM static-file host)
#   • wwwroot/_framework/* (WASM bundle from Animarr.Web.Client)
#   • wwwroot/_content/Animarr.UI/* (RCL static web assets)
FROM build AS publish
RUN dotnet publish "Animarr.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

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
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ffmpeg \
        mesa-va-drivers \
        intel-media-va-driver \
        vainfo \
        libva-drm2 libva2 && \
    rm -rf /var/lib/apt/lists/*

# Create data directory for SQLite
RUN mkdir -p /app/data && chmod 777 /app/data

COPY --from=publish /app/publish .

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Animarr.Web.dll"]
