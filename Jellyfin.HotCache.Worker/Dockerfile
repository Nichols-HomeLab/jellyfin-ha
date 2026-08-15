FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Jellyfin.HotCache.Worker/Jellyfin.HotCache.Worker.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out/ .
USER 1000:1000
ENTRYPOINT ["dotnet", "Jellyfin.HotCache.Worker.dll"]
