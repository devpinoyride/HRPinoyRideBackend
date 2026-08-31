# Multi-stage Dockerfile for the Pinoy Ride HR API (ASP.NET Core 8, Render-ready).
# Builds with the .NET SDK image and runs with the lean ASP.NET runtime image.
# The PORT env var from Render is honoured by ASPNETCORE_URLS (and Program.cs).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY PinoyRideHrApi.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
EXPOSE 8080
ENTRYPOINT ["dotnet", "PinoyRideHrApi.dll"]