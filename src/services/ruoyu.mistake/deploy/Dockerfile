FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5006
EXPOSE 5007

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/services/ruoyu.mistake/src/Contract/Ruoyu.Study.Mistake.Contract.csproj src/services/ruoyu.mistake/src/Contract/
COPY src/services/ruoyu.mistake/src/Database/Ruoyu.Study.Mistake.Database.csproj src/services/ruoyu.mistake/src/Database/
COPY src/services/ruoyu.mistake/src/Domain/Ruoyu.Study.Mistake.Domain.csproj src/services/ruoyu.mistake/src/Domain/
COPY src/services/ruoyu.mistake/src/Service/Ruoyu.Study.Mistake.Service.csproj src/services/ruoyu.mistake/src/Service/
COPY src/services/ruoyu.mistake/src/Host/Ruoyu.Study.Mistake.Host.csproj src/services/ruoyu.mistake/src/Host/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj src/services/ruoyu.common/src/Ruoyu.Study.Common/
COPY src/services/ruoyu.student/src/Contract/Ruoyu.Study.Student.Contract.csproj src/services/ruoyu.student/src/Contract/

RUN dotnet restore "src/services/ruoyu.mistake/src/Host/Ruoyu.Study.Mistake.Host.csproj"

COPY src/services/ruoyu.mistake/src src/services/ruoyu.mistake/src
COPY src/services/ruoyu.common src/services/ruoyu.common
COPY src/services/ruoyu.student/src/Contract src/services/ruoyu.student/src/Contract

RUN dotnet publish "src/services/ruoyu.mistake/src/Host/Ruoyu.Study.Mistake.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Ruoyu.Study.Mistake.Host.dll"]
