FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Services/Notifications/GrowGift.Notifications/GrowGift.Notifications.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# Run as non-root
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "GrowGift.Notifications.dll"]
