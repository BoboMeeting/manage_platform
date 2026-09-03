# syntax=docker/dockerfile:1

# ============================================================================
# 阶段 1：构建
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src

# 先拷 csproj 利用层缓存恢复 NuGet（依赖变化时只重新 restore 一次）
COPY src/ManagerPlatform/ManagerPlatform.csproj ./src/ManagerPlatform/
RUN dotnet restore src/ManagerPlatform/ManagerPlatform.csproj

# 拷贝剩余源码并发布
COPY src/ ./src/
RUN dotnet publish src/ManagerPlatform/ManagerPlatform.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ============================================================================
# 阶段 2：运行时镜像（仅包含发布产物）
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5080

# DataProtection 密钥持久化目录（无状态 JWT 不强依赖，但持久化可避免重启后旧 cookie 失效）
RUN mkdir -p /app/.dp-keys && chown -R app:app /app

# .NET 10 官方镜像内置非 root 用户 app（uid 1001）
USER app

COPY --from=build --chown=app:app /app/publish ./

EXPOSE 5080

ENTRYPOINT ["dotnet", "ManagerPlatform.dll"]
