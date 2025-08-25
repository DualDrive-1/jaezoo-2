# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# запуск не от root — безопаснее
RUN adduser --disabled-password --gecos "app" app && chown -R app:app /app
USER app

COPY --from=build /app/publish ./

# Render подставляет PORT автоматически
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

EXPOSE 8080
ENTRYPOINT ["dotnet", "JaeZoo.Server.dll"]
