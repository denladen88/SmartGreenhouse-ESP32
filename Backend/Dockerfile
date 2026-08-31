# ---------- Етап 1: збірка WebApp (React/TS → статика) ----------
FROM node:22-bookworm-slim AS web
WORKDIR /web

# Спершу тільки маніфести залежностей — щоб Docker кешував "npm ci",
# поки package.json / package-lock.json не змінюються (той самий трюк,
# що з .csproj на етапі 2).
COPY WebApp/package.json WebApp/package-lock.json ./
RUN npm ci

# Тепер решта коду фронта і сама збірка. Результат — /web/dist
# (index.html + assets/*.js + assets/*.css).
COPY WebApp/ ./
RUN npm run build

# ---------- Етап 2: збірка Backend (.NET → dll) ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Кеш NuGet: спершу тільки .csproj, потім restore.
COPY Backend/*.csproj ./
RUN dotnet restore

# Решта коду бекенду.
COPY Backend/ ./

# Кладемо зібраний фронт у wwwroot/ ДО publish. У csproj є
# <Content Update="wwwroot/**/*" ...>, тож publish його підхопить
# і покладе поруч із dll.
COPY --from=web /web/dist ./wwwroot

RUN dotnet publish SmartGreenhouse.Backend.csproj -c Release -o /app/publish

# ---------- Етап 3: фінальний образ (тільки .NET runtime) ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
COPY --from=build /app/publish /app/publish
WORKDIR /app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/publish/SmartGreenhouse.Backend.dll"]
