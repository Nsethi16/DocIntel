FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN rm -f NuGet.config && dotnet publish "src/DocIntel.Api/DocIntel.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false --source https://api.nuget.org/v3/index.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocIntel.Api.dll"]
