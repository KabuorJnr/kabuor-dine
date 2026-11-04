# Kabuor ERP: one container with the API and the web app it serves.
# 1) build the React app, 2) publish the .NET API with the app in wwwroot, 3) run on the slim runtime.
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
# vite builds into ../src/Kabuor.Erp.Api/wwwroot
RUN mkdir -p ../src/Kabuor.Erp.Api && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/Kabuor.Erp.Api/Kabuor.Erp.Api.csproj src/Kabuor.Erp.Api/
RUN dotnet restore src/Kabuor.Erp.Api/Kabuor.Erp.Api.csproj
COPY src/ src/
COPY --from=web /src/src/Kabuor.Erp.Api/wwwroot src/Kabuor.Erp.Api/wwwroot
RUN dotnet publish src/Kabuor.Erp.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api --chown=$APP_UID:$APP_UID /app ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Database__AllowLocalFile=true
# Run as unprivileged non-root app user
USER $APP_UID
# Render (and most hosts) set PORT; listen on it (8080 when run elsewhere).
CMD ["sh", "-c", "dotnet Kabuor.Erp.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
