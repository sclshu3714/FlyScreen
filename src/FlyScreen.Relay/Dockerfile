FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FlyScreen.Relay.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=
EXPOSE 8443 9080 15155 15156/udp 8443/udp
ENTRYPOINT ["dotnet", "FlyScreen.Relay.dll"]
