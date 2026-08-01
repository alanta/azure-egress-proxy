FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Central Package Management lives at the repository root, so the restore layer needs it: without
# these three files every PackageReference here is versionless, which the SDK rejects (NU1015).
# The lock files come with them, so this restore is the same pinned closure CI restores — the
# guarantee in SECURITY_GUIDELINES.md § 2.1 holds inside the image build too, not just outside it.
COPY ["Directory.Build.props", "Directory.Packages.props", "nuget.config", "./"]
COPY ["src/ControlPlane/ControlPlane.csproj", "src/ControlPlane/packages.lock.json", "src/ControlPlane/"]
COPY ["src/ServiceDefaults/ServiceDefaults.csproj", "src/ServiceDefaults/packages.lock.json", "src/ServiceDefaults/"]
RUN dotnet restore "src/ControlPlane/ControlPlane.csproj" --locked-mode

COPY . .
RUN dotnet publish "src/ControlPlane/ControlPlane.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
# The .NET images ship a non-root `app` user (UID via $APP_UID).
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "ControlPlane.dll"]
