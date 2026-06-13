FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/CasaTimo.Workers/CasaTimo.Workers.csproj", "src/CasaTimo.Workers/"]
COPY ["src/CasaTimo.Core/CasaTimo.Core.csproj", "src/CasaTimo.Core/"]
COPY ["src/CasaTimo.Infrastructure/CasaTimo.Infrastructure.csproj", "src/CasaTimo.Infrastructure/"]
RUN dotnet restore "src/CasaTimo.Workers/CasaTimo.Workers.csproj"
COPY . .
WORKDIR "/src/src/CasaTimo.Workers"
RUN dotnet build "CasaTimo.Workers.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CasaTimo.Workers.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CasaTimo.Workers.dll"]
