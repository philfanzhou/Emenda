ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${ASPNET_IMAGE} AS base
WORKDIR /app
EXPOSE 5007

FROM ${SDK_IMAGE} AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/Database/Ruoyu.Study.Mistake.Database.csproj src/Database/
COPY src/Domain/Ruoyu.Study.Mistake.Domain.csproj src/Domain/
COPY src/Service/Ruoyu.Study.Mistake.Service.csproj src/Service/
COPY src/Host/Ruoyu.Study.Mistake.Host.csproj src/Host/
COPY vendor/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj vendor/Ruoyu.Study.Common/
COPY vendor/Ruoyu.Study.Ai.Shared/Ruoyu.Study.Ai.Shared.csproj vendor/Ruoyu.Study.Ai.Shared/
COPY vendor/Ruoyu.Study.Consul.Shared/Ruoyu.Study.Consul.Shared.csproj vendor/Ruoyu.Study.Consul.Shared/
COPY src/StudentClient/Ruoyu.Study.Mistake.StudentClient.csproj src/StudentClient/

RUN dotnet restore "src/Host/Ruoyu.Study.Mistake.Host.csproj"

COPY src src
COPY docs/Integration/mistake-http-v1.json docs/Integration/mistake-http-v1.json
COPY docs/Integration/mistake-http-v2.json docs/Integration/mistake-http-v2.json
COPY docs/Integration/mistake-http-v3.json docs/Integration/mistake-http-v3.json
COPY vendor/Ruoyu.Study.Common vendor/Ruoyu.Study.Common
COPY vendor/Ruoyu.Study.Ai.Shared vendor/Ruoyu.Study.Ai.Shared
COPY vendor/Ruoyu.Study.Consul.Shared vendor/Ruoyu.Study.Consul.Shared

RUN dotnet publish "src/Host/Ruoyu.Study.Mistake.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Standalone
ENV Emenda__Runtime__Mode=Standalone
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Ruoyu.Study.Mistake.Host.dll"]