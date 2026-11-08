ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
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
COPY vendor/Emenda.Common vendor/Emenda.Common
COPY vendor/Emenda.Ai vendor/Emenda.Ai
COPY vendor/Emenda.Consul vendor/Emenda.Consul

RUN dotnet publish "src/Host/Emenda.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Standalone
ENV Emenda__Runtime__Mode=Standalone
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Emenda.Host.dll"]