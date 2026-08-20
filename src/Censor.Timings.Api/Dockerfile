FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Censor.Core/Censor.Core.csproj src/Censor.Core/packages.lock.json src/Censor.Core/
COPY src/Censor.Timings.Api/Censor.Timings.Api.csproj src/Censor.Timings.Api/packages.lock.json src/Censor.Timings.Api/
RUN dotnet restore src/Censor.Timings.Api/Censor.Timings.Api.csproj --locked-mode

COPY src/Censor.Core/ src/Censor.Core/
COPY src/Censor.Timings.Api/ src/Censor.Timings.Api/
RUN dotnet publish src/Censor.Timings.Api/Censor.Timings.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Censor.Timings.Api.dll"]
