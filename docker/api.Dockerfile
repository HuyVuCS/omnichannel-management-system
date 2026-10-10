# Build context: ./backend  (see docker-compose.yml, docker-compose.prod.yml)
# The SDK stage runs on the build machine's own architecture and cross-compiles for the target (-a $TARGETARCH), so an
# arm64 image built on an amd64 machine does not run the compiler under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore in its own layer so it is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Omnichannel.Domain/Omnichannel.Domain.csproj src/Omnichannel.Domain/
COPY src/Omnichannel.Application/Omnichannel.Application.csproj src/Omnichannel.Application/
COPY src/Omnichannel.Infrastructure/Omnichannel.Infrastructure.csproj src/Omnichannel.Infrastructure/
COPY src/Omnichannel.Api/Omnichannel.Api.csproj src/Omnichannel.Api/
RUN dotnet restore src/Omnichannel.Api/Omnichannel.Api.csproj -a $TARGETARCH

COPY src/Omnichannel.Domain/ src/Omnichannel.Domain/
COPY src/Omnichannel.Application/ src/Omnichannel.Application/
COPY src/Omnichannel.Infrastructure/ src/Omnichannel.Infrastructure/
COPY src/Omnichannel.Api/ src/Omnichannel.Api/
RUN dotnet publish src/Omnichannel.Api/Omnichannel.Api.csproj -c Release -a $TARGETARCH -o /app/publish --no-restore /p:UseAppHost=false

# The full runtime image (not chiseled, not Alpine): it carries ICU and the time zone database.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
# Non-root user shipped with the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "Omnichannel.Api.dll"]
