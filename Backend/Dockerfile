FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish SmartGreenhouse.Backend.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
COPY --from=build /app/publish /app/publish
WORKDIR /app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/publish/SmartGreenhouse.Backend.dll"]
