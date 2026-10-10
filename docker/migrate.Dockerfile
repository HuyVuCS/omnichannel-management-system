# Build context: ./backend  (see docker-compose.prod.yml, service "migrate")
# An EF Core migration bundle: one self-contained executable that applies the pending migrations and nothing else.
# It connects with the connection string in OMNICHANNEL_MIGRATOR_CONNECTION (the migrator role - never the role of
# the application), which is given to the container at run time and is in no layer of this image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# The tool version is the one of the repository's tool manifest.
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore

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
# musl is not a target: the runtime image below is glibc. x64 is what .NET calls amd64.
RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
    && dotnet ef migrations bundle \
        --project src/Omnichannel.Infrastructure --startup-project src/Omnichannel.Api \
        --configuration Release --self-contained --target-runtime "linux-$arch" \
        --output /app/efbundle

# Self-contained: only the native dependencies of .NET are needed.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app
COPY --from=build /app/efbundle .
# Non-root user shipped with the official .NET images.
USER $APP_UID
ENTRYPOINT ["/app/efbundle"]
