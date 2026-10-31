FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ANGI.AiGateway/ANGI.AiGateway.csproj ANGI.AiGateway/
RUN dotnet restore ANGI.AiGateway/ANGI.AiGateway.csproj
COPY ANGI.AiGateway/ ANGI.AiGateway/
RUN dotnet publish ANGI.AiGateway/ANGI.AiGateway.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ANGI.AiGateway.dll"]
