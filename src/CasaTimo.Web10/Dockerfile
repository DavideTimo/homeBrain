FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CasaTimo.Web10/CasaTimo.Web10.csproj src/CasaTimo.Web10/
RUN dotnet restore src/CasaTimo.Web10/CasaTimo.Web10.csproj
COPY src/CasaTimo.Web10/ src/CasaTimo.Web10/
RUN dotnet publish src/CasaTimo.Web10/CasaTimo.Web10.csproj -c Release -o /app/publish

FROM nginx:alpine AS final
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY src/CasaTimo.Web10/nginx.conf /etc/nginx/conf.d/default.conf
