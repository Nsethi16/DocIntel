# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_IDE-BOC-RevenueProcessingforComOps=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore "src/DocIntel.Api/DocIntel.Api.csproj" --configfile NuGet.config --locked-mode \
    && dotnet publish "src/DocIntel.Api/DocIntel.Api.csproj" -c Release -o /app/publish \
       --no-restore /p:UseAppHost=false /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
RUN mkdir -p /app/App_Data && chown app:app /app/App_Data
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocIntel.Api.dll"]
