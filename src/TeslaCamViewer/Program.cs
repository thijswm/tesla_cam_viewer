using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;
using TeslaCamViewer.Services;
using MudBlazor.Services;
using Serilog;
using FFMpegCore;

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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

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
app.MapGet("/api/events", async (AppDbContext db) =>
    await db.Events.OrderByDescending(e => e.CreatedAt).ToListAsync());
app.MapGet("/api/clips", async (AppDbContext db) =>
    await db.Clips.OrderByDescending(c => c.Timestamp).Take(500).ToListAsync());

// Serve video files from Clip paths
app.MapGet("/api/video/{clipId:int}", async (int clipId, AppDbContext db) =>
{
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

// Serve stitched camera videos from byte arrays
app.MapGet("/api/camera/{cameraId:int}", async (int cameraId, AppDbContext db) =>
{
    var camera = await db.Cameras.FindAsync(cameraId);
    if (camera == null)
    {
        Log.Warning("Camera {CameraId} not found in database", cameraId);
        return Results.NotFound();
    }

    if (camera.VideoData == null || camera.VideoData.Length == 0)
    {
        Log.Warning("Camera {CameraId} has no video data", cameraId);
        return Results.NotFound(new { error = "No video data available" });
    }

    Log.Information("Serving stitched video for camera {CameraId} ({CameraName}): {Size} bytes", 
        cameraId, camera.CameraName, camera.VideoData.Length);

    // Serve from byte array with range support
    return Results.Bytes(camera.VideoData, "video/mp4", enableRangeProcessing: true);
});

app.Run();
