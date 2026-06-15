FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CasaTimo.Core/CasaTimo.Core.csproj src/CasaTimo.Core/
COPY src/CasaTimo.Infrastructure/CasaTimo.Infrastructure.csproj src/CasaTimo.Infrastructure/
COPY src/CasaTimo.Workers/CasaTimo.Workers.csproj src/CasaTimo.Workers/
RUN dotnet restore src/CasaTimo.Workers/CasaTimo.Workers.csproj
COPY src/CasaTimo.Core/ src/CasaTimo.Core/
COPY src/CasaTimo.Infrastructure/ src/CasaTimo.Infrastructure/
COPY src/CasaTimo.Workers/ src/CasaTimo.Workers/
RUN dotnet publish src/CasaTimo.Workers/CasaTimo.Workers.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CasaTimo.Workers.dll"]
