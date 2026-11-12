FROM node:22 AS web
WORKDIR /src/apps/sales/web
COPY apps/sales/web/package*.json ./
RUN npm ci
COPY apps/sales/web ./
RUN mkdir -p ../server/ArchiTech.Api && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY apps ./apps
COPY database ./database
COPY --from=web /src/apps/sales/server/ArchiTech.Api/wwwroot ./apps/sales/server/ArchiTech.Api/wwwroot
RUN dotnet publish apps/sales/server/ArchiTech.Api -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ArchiTech.Api.dll"]
