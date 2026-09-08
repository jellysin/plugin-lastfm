FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510 AS builder
WORKDIR /src

COPY *.sln .
COPY global.json Directory.Build.props version.txt ./
COPY Jellyfin.Plugin.Lastfm/*.csproj ./Jellyfin.Plugin.Lastfm/
COPY Jellyfin.Plugin.Lastfm/packages.lock.json ./Jellyfin.Plugin.Lastfm/
RUN dotnet restore Jellyfin.Plugin.Lastfm/Jellyfin.Plugin.Lastfm.csproj --locked-mode

COPY . .

# Publish the plugin in Release configuration
RUN dotnet build Jellyfin.Plugin.Lastfm/Jellyfin.Plugin.Lastfm.csproj --no-restore -c Release -o /app/plugin

FROM scratch AS artifact
COPY --from=builder /app/plugin/Jellyfin.Plugin.Lastfm.dll /
