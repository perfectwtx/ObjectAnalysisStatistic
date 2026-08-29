# syntax=docker/dockerfile:1

# 多阶段构建：第一阶段编译 CLI 工具
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先还原依赖（利用 Docker 层缓存）
COPY src/ObjectAnalyzer.Core/ObjectAnalyzer.Core.csproj src/ObjectAnalyzer.Core/
COPY src/ObjectAnalyzer/ObjectAnalyzer.csproj src/ObjectAnalyzer/
RUN dotnet restore src/ObjectAnalyzer/ObjectAnalyzer.csproj

COPY . .
RUN dotnet publish src/ObjectAnalyzer/ObjectAnalyzer.csproj -c Release -o /app --no-restore

# 第二阶段：运行时镜像
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /data

COPY --from=build /app /app

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "/app/ObjectAnalyzer.dll"]
