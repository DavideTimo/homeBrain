FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/CasaTimo.Api/CasaTimo.Api.csproj", "src/CasaTimo.Api/"]
COPY ["src/CasaTimo.Core/CasaTimo.Core.csproj", "src/CasaTimo.Core/"]
COPY ["src/CasaTimo.Infrastructure/CasaTimo.Infrastructure.csproj", "src/CasaTimo.Infrastructure/"]
RUN dotnet restore "src/CasaTimo.Api/CasaTimo.Api.csproj"
COPY . .
WORKDIR "/src/src/CasaTimo.Api"
RUN dotnet build "CasaTimo.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CasaTimo.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CasaTimo.Api.dll"]
