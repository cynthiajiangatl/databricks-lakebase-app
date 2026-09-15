FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TicketsDashboard.csproj .
RUN dotnet restore TicketsDashboard.csproj
COPY . .
RUN dotnet publish TicketsDashboard.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Matches the non-root user baked into the .NET images.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /publish .
ENTRYPOINT ["dotnet", "TicketsDashboard.dll"]
