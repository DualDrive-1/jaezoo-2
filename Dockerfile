# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# ������ ��������� ������/������������, ���� �� ��� ��� (������������)
ARG APP_UID=10001
RUN set -eux; \
    if ! getent group app >/dev/null; then groupadd -g ${APP_UID} app; fi; \
    if ! id -u app >/dev/null 2>&1; then useradd -u ${APP_UID} -g app -m -s /usr/sbin/nologin app; fi

# �������� ������ �� build-������
COPY --from=build /app/publish ./

# ���������� ��������� ������ � ������������� �� �������������������� ������������
RUN chown -R app:app /app
USER app

# Render ����������� PORT ������������� � ������� ������ ���
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
EXPOSE 8080

ENTRYPOINT ["dotnet", "JaeZoo.Server.dll"]
