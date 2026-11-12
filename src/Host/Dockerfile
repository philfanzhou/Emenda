ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG APP_VERSION=0.0.0-dev
ARG APP_REVISION=
ARG APP_CHANNEL=development

FROM ${ASPNET_IMAGE} AS base
WORKDIR /app
EXPOSE 5007

FROM ${SDK_IMAGE} AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/Database/Emenda.Database.csproj src/Database/
COPY src/Domain/Emenda.Domain.csproj src/Domain/
COPY src/Service/Emenda.Service.csproj src/Service/
COPY src/Host/Emenda.Host.csproj src/Host/
COPY vendor/Emenda.Common/Emenda.Common.csproj vendor/Emenda.Common/
COPY vendor/Emenda.Ai/Emenda.Ai.csproj vendor/Emenda.Ai/
COPY vendor/Emenda.Consul/Emenda.Consul.csproj vendor/Emenda.Consul/
COPY src/StudentClient/Emenda.StudentClient.csproj src/StudentClient/

RUN dotnet restore "src/Host/Emenda.Host.csproj"

COPY src src
COPY docs/Integration/mistake-http-v1.json docs/Integration/mistake-http-v1.json
COPY docs/Integration/mistake-http-v2.json docs/Integration/mistake-http-v2.json
COPY docs/Integration/mistake-http-v3.json docs/Integration/mistake-http-v3.json
COPY docs/Integration/emenda-intake-v1.json docs/Integration/emenda-intake-v1.json
COPY vendor/Emenda.Common vendor/Emenda.Common
COPY vendor/Emenda.Ai vendor/Emenda.Ai
COPY vendor/Emenda.Consul vendor/Emenda.Consul

RUN dotnet publish "src/Host/Emenda.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
ARG APP_VERSION
ARG APP_REVISION
ARG APP_CHANNEL
LABEL org.opencontainers.image.title="Emenda" \
      org.opencontainers.image.description="Self-hosted AI-driven mistake management service for learning platforms" \
      org.opencontainers.image.source="https://github.com/philfanzhou/Emenda" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${APP_VERSION}" \
      org.opencontainers.image.revision="${APP_REVISION}" \
      org.opencontainers.image.channel="${APP_CHANNEL}"
ENV ASPNETCORE_ENVIRONMENT=Standalone
ENV Emenda__Runtime__Mode=Standalone
ENV APP_VERSION=${APP_VERSION}
ENV APP_REVISION=${APP_REVISION}
ENV APP_CHANNEL=${APP_CHANNEL}
# Volume-mountable state directory: the dynamic caller credential file and
# future admin-console state live here so they survive container replacement.
RUN mkdir -p /app/data
VOLUME /app/data
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Emenda.Host.dll"]