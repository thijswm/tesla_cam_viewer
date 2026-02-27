using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;
using TeslaCamViewer.Services;
using MudBlazor.Services;
using Serilog;
using FFMpegCore;
using Minio;
using Minio.DataModel.Args;

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
app.MapGet("/api/events", async (IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.Events.OrderByDescending(e => e.CreatedAt).ToListAsync();
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

// Serve stitched camera videos from MinIO
app.MapGet("/api/camera/{cameraId:int}", async (int cameraId, IDbContextFactory<AppDbContext> dbFactory, IMinioClient minio, HttpContext httpContext) =>
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
        return Results.NotFound(new { error = "No video path available" });
    }

    try
    {
        Log.Information("Streaming video from MinIO for camera {CameraId} ({CameraName}): {Path}",
            cameraId, camera.CameraName, camera.MinioPath);

        var memoryStream = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(camera.BucketName)
            .WithObject(camera.MinioPath)
            .WithCallbackStream(stream => stream.CopyTo(memoryStream)),
            httpContext.RequestAborted);

        memoryStream.Position = 0;
        return Results.Stream(memoryStream, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to stream video from MinIO for camera {CameraId}", cameraId);
        return Results.Problem("Failed to retrieve video from storage");
    }
});

await app.RunAsync();
