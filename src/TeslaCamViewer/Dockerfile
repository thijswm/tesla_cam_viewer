# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG APP_VERSION=0.0.0
ARG APP_INFORMATIONAL_VERSION=dev
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app \
    -p:Version=${APP_VERSION} \
    -p:InformationalVersion=${APP_INFORMATIONAL_VERSION}

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app/bin
COPY --from=build /app .
RUN apt-get update && apt-get install -y ffmpeg
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TeslaCamViewer.dll"]
