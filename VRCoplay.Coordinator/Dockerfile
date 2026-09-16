FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY VRCoplay.Coordinator.csproj .
RUN dotnet restore
COPY Program.cs .
COPY Sessions/ Sessions/
COPY Routing/ Routing/
COPY Releases/ Releases/
COPY Shared/ Shared/
COPY wwwroot/ wwwroot/
RUN dotnet publish -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
WORKDIR /app
COPY --from=build /app .
COPY THIRD-PARTY-NOTICES.md .
USER $APP_UID
ENTRYPOINT ["dotnet", "VRCoplay.Coordinator.dll"]
