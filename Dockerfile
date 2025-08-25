# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# создаЄм системную группу/пользовател€, если их ещЄ нет (идемпотентно)
ARG APP_UID=10001
RUN set -eux; \
    if ! getent group app >/dev/null; then groupadd -g ${APP_UID} app; fi; \
    if ! id -u app >/dev/null 2>&1; then useradd -u ${APP_UID} -g app -m -s /usr/sbin/nologin app; fi

# копируем сборку из build-стадии
COPY --from=build /app/publish ./

# выставл€ем владельца файлов и переключаемс€ на непривилегированного пользовател€
RUN chown -R app:app /app
USER app

# Render подставл€ет PORT автоматически Ч слушаем именно его
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
EXPOSE 8080

ENTRYPOINT ["dotnet", "JaeZoo.Server.dll"]
