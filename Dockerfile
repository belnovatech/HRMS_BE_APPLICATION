FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore HRMS.Backend.slnx
RUN dotnet publish src/HRMS.Api/HRMS.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/App_Data/uploads && chown -R $APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HRMS.Api.dll"]
