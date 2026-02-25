# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app/bin
COPY --from=build /app/bin .
WORKDIR /app/models
COPY model/yolov8n.onnx /app/models
WORKDIR /app/bin
RUN apt-get update && apt-get install -y ffmpeg
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TeslaCamViewer.dll"]
