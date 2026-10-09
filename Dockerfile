FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

COPY Directory.Build.props nuget.config ./
COPY src/SmartTVRelay.Core/SmartTVRelay.Core.csproj src/SmartTVRelay.Core/
COPY src/SmartTVRelay.Viewer/SmartTVRelay.Viewer.csproj src/SmartTVRelay.Viewer/
RUN dotnet restore src/SmartTVRelay.Viewer/SmartTVRelay.Viewer.csproj --configfile nuget.config
COPY src/ src/
RUN dotnet publish src/SmartTVRelay.Viewer/SmartTVRelay.Viewer.csproj \
    --configuration Release --no-restore --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
RUN apt-get update && \
    apt-get install --yes --no-install-recommends ca-certificates curl ffmpeg && \
    rm -rf /var/lib/apt/lists/*
ARG VCS_REF
RUN printf '%s' "$VCS_REF" | grep -Eq '^[0-9a-f]{40}$'
LABEL org.opencontainers.image.revision=$VCS_REF

WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5189 \
    DOTNET_EnableDiagnostics=0 \
    Viewer__WorkDir=/tmp/smarttvrelay-viewer
EXPOSE 5189
USER app
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
    CMD curl --fail --silent --show-error http://127.0.0.1:5189/healthz > /dev/null || exit 1
ENTRYPOINT ["sh", "-c", "umask 077; exec dotnet SmartTVRelay.Viewer.dll"]
