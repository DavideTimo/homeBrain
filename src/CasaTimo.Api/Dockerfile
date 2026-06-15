FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CasaTimo.Core/CasaTimo.Core.csproj src/CasaTimo.Core/
COPY src/CasaTimo.Infrastructure/CasaTimo.Infrastructure.csproj src/CasaTimo.Infrastructure/
COPY src/CasaTimo.Api/CasaTimo.Api.csproj src/CasaTimo.Api/
RUN dotnet restore src/CasaTimo.Api/CasaTimo.Api.csproj
COPY src/CasaTimo.Core/ src/CasaTimo.Core/
COPY src/CasaTimo.Infrastructure/ src/CasaTimo.Infrastructure/
COPY src/CasaTimo.Api/ src/CasaTimo.Api/
RUN dotnet publish src/CasaTimo.Api/CasaTimo.Api.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CasaTimo.Api.dll"]
