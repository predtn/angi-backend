# ANGI Web API image for Render (or any Docker host).
#   docker build -t angi-backend .
#   docker run -p 8080:8080 --env-file .env.production angi-backend
# Settings come from environment variables (Jwt__SecretKey, ConnectionStrings__Default, ...), see README "Deploy".
# The image also holds /app/efbundle, which applies the EF migrations: run it as the pre-deploy command.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is reused while only source files change.
COPY ANGI.slnx ./
COPY ANGI.Domain/ANGI.Domain.csproj ANGI.Domain/
COPY ANGI.Application/ANGI.Application.csproj ANGI.Application/
COPY ANGI.Infrastructure/ANGI.Infrastructure.csproj ANGI.Infrastructure/
COPY ANGI.WebApi/ANGI.WebApi.csproj ANGI.WebApi/
RUN dotnet restore ANGI.WebApi/ANGI.WebApi.csproj

COPY ANGI.Domain/ ANGI.Domain/
COPY ANGI.Application/ ANGI.Application/
COPY ANGI.Infrastructure/ ANGI.Infrastructure/
COPY ANGI.WebApi/ ANGI.WebApi/
RUN dotnet publish ANGI.WebApi/ANGI.WebApi.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Migration bundle: same EF tools version as Microsoft.EntityFrameworkCore.Design in ANGI.Infrastructure.
RUN dotnet tool install --global dotnet-ef --version 10.0.12
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
        --project ANGI.Infrastructure --startup-project ANGI.WebApi \
        --configuration Release --output /app/publish/efbundle --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=true
COPY --from=build /app/publish .
# The aspnet image's non-root user.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ANGI.WebApi.dll"]
