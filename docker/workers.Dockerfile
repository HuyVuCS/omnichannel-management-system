# Build context: ./backend  (see docker-compose.prod.yml)
# Same pattern as api.Dockerfile: the SDK stage runs on the build machine's architecture and cross-compiles.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore in its own layer so it is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Omnichannel.Domain/Omnichannel.Domain.csproj src/Omnichannel.Domain/
COPY src/Omnichannel.Application/Omnichannel.Application.csproj src/Omnichannel.Application/
COPY src/Omnichannel.Infrastructure/Omnichannel.Infrastructure.csproj src/Omnichannel.Infrastructure/
COPY src/Omnichannel.Workers/Omnichannel.Workers.csproj src/Omnichannel.Workers/
RUN dotnet restore src/Omnichannel.Workers/Omnichannel.Workers.csproj -a $TARGETARCH

COPY src/Omnichannel.Domain/ src/Omnichannel.Domain/
COPY src/Omnichannel.Application/ src/Omnichannel.Application/
COPY src/Omnichannel.Infrastructure/ src/Omnichannel.Infrastructure/
COPY src/Omnichannel.Workers/ src/Omnichannel.Workers/
RUN dotnet publish src/Omnichannel.Workers/Omnichannel.Workers.csproj -c Release -a $TARGETARCH -o /app/publish --no-restore /p:UseAppHost=false

# The full runtime image (not chiseled, not Alpine): it carries ICU and the time zone database, which the export files
# and the e-mails need (vi culture, Asia/Ho_Chi_Minh). The Workers host has no HTTP endpoint: the plain runtime is enough.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Non-root user shipped with the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "Omnichannel.Workers.dll"]
