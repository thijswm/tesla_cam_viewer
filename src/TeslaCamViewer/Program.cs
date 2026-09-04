using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;
using TeslaCamViewer.Services;
using MudBlazor.Services;
using Serilog;
using FFMpegCore;
using Minio;

var builder = WebApplication.CreateBuilder(args);

// Configure FFMpeg binary path (optional, for local dev if not in PATH)
var ffmpegPath = builder.Configuration["FFMpeg:BinaryFolder"];
if (!string.IsNullOrEmpty(ffmpegPath) && Directory.Exists(ffmpegPath))
{
    GlobalFFOptions.Configure(options => options.BinaryFolder = ffmpegPath);
}

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .Build())
    .CreateLogger();

builder.Host.UseSerilog(Log.Logger);

Log.Information("Starting TeslaCamViewer");

// Add DbContext factory for components that need concurrent access
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.EnableRetryOnFailure()));

// Configure MinIO client
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var config = builder.Configuration.GetSection("MinIO");
    var endpoint = config["Endpoint"] ?? "localhost:9000";
    var accessKey = config["AccessKey"] ?? "minioadmin";
    var secretKey = config["SecretKey"] ?? "minioadmin";
    var useSSL = config.GetValue<bool>("UseSSL");

    return new MinioClient()
        .WithEndpoint(endpoint)
        .WithCredentials(accessKey, secretKey)
        .WithSSL(useSSL)
        .Build();
});

builder.Services.AddHttpClient("minio-range", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddMudServices();

builder.Services.AddHostedService<ClipScanner>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Log.Information("Performing migration");
    db.Database?.Migrate();
    Log.Information("Migration done");
}

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.MapGet("/api/health", () => Results.Ok("ok"));
app.MapGet("/api/thumbnail/{eventId:int}", async (int eventId, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var thumbnail = await db.Events.AsNoTracking()
        .Where(e => e.Id == eventId)
        .Select(e => e.Thumbnail)
        .FirstOrDefaultAsync();

    if (thumbnail is null || thumbnail.Length == 0)
    {
        return Results.NotFound();
    }

    return Results.File(thumbnail, "image/png");
});
app.MapGet("/api/events", async (IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.Events.AsNoTracking()
        .OrderByDescending(e => e.CreatedAt)
        .Select(e => new Event
        {
            Id = e.Id,
            FolderName = e.FolderName,
            Type = e.Type,
            CreatedAt = e.CreatedAt,
            Source = e.Source,
            Lat = e.Lat,
            Long = e.Long,
            City = e.City,
            Street = e.Street,
            Camera = e.Camera,
            TimeStamp = e.TimeStamp
        })
        .ToListAsync();
});
app.MapGet("/api/clips", async (IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.Clips.OrderByDescending(c => c.Timestamp).Take(500).ToListAsync();
});

// Serve video files from Clip paths
app.MapGet("/api/video/{clipId:int}", async (int clipId, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var clip = await db.Clips.FindAsync(clipId);
    if (clip == null || string.IsNullOrWhiteSpace(clip.Path))
    {
        Log.Warning("Clip {ClipId} not found in database or has no path", clipId);
        return Results.NotFound();
    }

    if (!File.Exists(clip.Path))
    {
        Log.Warning("Video file not found at path: {Path}", clip.Path);
        return Results.NotFound(new { error = "Video file not found", path = clip.Path });
    }

    Log.Information("Serving video file: {Path} for clip {ClipId}", clip.Path, clipId);

    // Use Results.File for better range request handling and automatic stream disposal
    return Results.File(clip.Path, "video/mp4", enableRangeProcessing: true);
});

// Serve stitched camera videos from MinIO with HTTP byte-range support (required for seeking).
app.MapMethods("/api/camera/{cameraId:int}", ["GET", "HEAD"], async (int cameraId, IDbContextFactory<AppDbContext> dbFactory, IMinioClient minio, IHttpClientFactory httpClientFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var camera = await db.Cameras.FindAsync(cameraId);
    if (camera == null)
    {
        Log.Warning("Camera {CameraId} not found in database", cameraId);
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(camera.MinioPath))
    {
        Log.Warning("Camera {CameraId} has no MinIO path", cameraId);
        return Results.NotFound();
    }

    Log.Debug("Streaming video from MinIO for camera {CameraId} ({CameraName}): {Path}",
        cameraId, camera.CameraName, camera.MinioPath);

    return new MinioRangeStreamResult(
        minio,
        httpClientFactory.CreateClient("minio-range"),
        camera.BucketName,
        camera.MinioPath,
        camera.FileSize);
});

await app.RunAsync();
