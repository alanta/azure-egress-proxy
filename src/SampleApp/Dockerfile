FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/SampleApp/SampleApp.csproj", "src/SampleApp/"]
COPY ["src/EgressProxy.Client/EgressProxy.Client.csproj", "src/EgressProxy.Client/"]
RUN dotnet restore "src/SampleApp/SampleApp.csproj"

COPY . .
RUN dotnet publish "src/SampleApp/SampleApp.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
# The .NET images ship a non-root `app` user (UID via $APP_UID).
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "SampleApp.dll"]
