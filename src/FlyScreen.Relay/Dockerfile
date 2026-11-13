# FlyScreen.Relay — 自托管信令 + STUN + 中继
# 构建: tools/pack-docker.ps1  或  docker build -t flyscreen-relay .
#
# 国内拉取慢时可换基础镜像前缀，例如:
#   docker build --build-arg SDK_IMAGE=mcr.m.daocloud.io/dotnet/sdk:10.0 \
#                --build-arg RUNTIME_IMAGE=mcr.m.daocloud.io/dotnet/aspnet:10.0 \
#                -t flyscreen-relay .
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY FlyScreen.Relay.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=
LABEL org.opencontainers.image.title="FlyScreen.Relay" \
      org.opencontainers.image.description="玄鸟自托管信令 + STUN + 中继服务"
EXPOSE 8443 9080 15155 15156/udp 8443/udp
ENTRYPOINT ["dotnet", "FlyScreen.Relay.dll"]
