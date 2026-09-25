# Build context: ./backend  (see docker-compose.yml)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer so it is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Omnichannel.Domain/Omnichannel.Domain.csproj src/Omnichannel.Domain/
COPY src/Omnichannel.Application/Omnichannel.Application.csproj src/Omnichannel.Application/
COPY src/Omnichannel.Infrastructure/Omnichannel.Infrastructure.csproj src/Omnichannel.Infrastructure/
COPY src/Omnichannel.Api/Omnichannel.Api.csproj src/Omnichannel.Api/
RUN dotnet restore src/Omnichannel.Api/Omnichannel.Api.csproj

COPY src/Omnichannel.Domain/ src/Omnichannel.Domain/
COPY src/Omnichannel.Application/ src/Omnichannel.Application/
COPY src/Omnichannel.Infrastructure/ src/Omnichannel.Infrastructure/
COPY src/Omnichannel.Api/ src/Omnichannel.Api/
RUN dotnet publish src/Omnichannel.Api/Omnichannel.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
# Non-root user shipped with the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "Omnichannel.Api.dll"]
