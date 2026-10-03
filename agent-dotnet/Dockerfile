FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["OpenWA.Agent.Api/OpenWA.Agent.Api.csproj", "OpenWA.Agent.Api/"]
COPY ["OpenWA.Agent.Core/OpenWA.Agent.Core.csproj", "OpenWA.Agent.Core/"]
COPY ["OpenWA.Agent.Infrastructure/OpenWA.Agent.Infrastructure.csproj", "OpenWA.Agent.Infrastructure/"]
COPY ["OpenWA.Agent.Storage/OpenWA.Agent.Storage.csproj", "OpenWA.Agent.Storage/"]
RUN dotnet restore "OpenWA.Agent.Api/OpenWA.Agent.Api.csproj"
COPY . .
WORKDIR "/src/OpenWA.Agent.Api"
RUN dotnet build "OpenWA.Agent.Api.csproj" -c Release -o /app/build
RUN dotnet publish "OpenWA.Agent.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "OpenWA.Agent.Api.dll"]
