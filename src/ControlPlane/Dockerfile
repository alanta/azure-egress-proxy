FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ControlPlane/ControlPlane.csproj", "src/ControlPlane/"]
COPY ["src/ServiceDefaults/ServiceDefaults.csproj", "src/ServiceDefaults/"]
RUN dotnet restore "src/ControlPlane/ControlPlane.csproj"

COPY . .
RUN dotnet publish "src/ControlPlane/ControlPlane.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
# The .NET images ship a non-root `app` user (UID via $APP_UID).
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "ControlPlane.dll"]
