# Octopus.Api — build context is the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Octopus.slnx ./
COPY src/Octopus.Api/Octopus.Api.csproj src/Octopus.Api/
COPY src/BuildingBlocks/Octopus.BuildingBlocks/Octopus.BuildingBlocks.csproj src/BuildingBlocks/Octopus.BuildingBlocks/
COPY src/Modules/Apps/Octopus.Apps/Octopus.Apps.csproj src/Modules/Apps/Octopus.Apps/
COPY src/Modules/Deployments/Octopus.Deployments/Octopus.Deployments.csproj src/Modules/Deployments/Octopus.Deployments/
COPY src/Modules/Runtime/Octopus.Runtime/Octopus.Runtime.csproj src/Modules/Runtime/Octopus.Runtime/
COPY src/Modules/Routing/Octopus.Routing/Octopus.Routing.csproj src/Modules/Routing/Octopus.Routing/
COPY src/Modules/GitHub/Octopus.GitHub/Octopus.GitHub.csproj src/Modules/GitHub/Octopus.GitHub/
RUN dotnet restore src/Octopus.Api/Octopus.Api.csproj
COPY . .
RUN dotnet publish src/Octopus.Api/Octopus.Api.csproj -c Release -o /app/publish --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Octopus.Api.dll"]
