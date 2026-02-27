using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TeslaCamViewer.Data;
using FFMpegCore;
using Minio;
using Minio.DataModel.Args;
using System.Globalization;
using System.Net.Http.Headers;

namespace TeslaCamViewer.Services;

public class ClipScanner : BackgroundService
{
    private readonly ILogger<ClipScanner> _logger;
    private readonly IServiceProvider _sp;
    private readonly IMinioClient _minio;
    private readonly string _sentryPath;
    private readonly string _savedPath;
    private readonly string _minioBucket;
    private readonly bool _enableReverseGeocoding;
    private readonly int _scanIntervalSeconds;
    private static readonly HttpClient GeoHttpClient = CreateGeoHttpClient();

    private static readonly TimeSpan FolderQuietPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FolderQuietPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FolderQuietStatusInterval = TimeSpan.FromSeconds(5);

    public ClipScanner(ILogger<ClipScanner> logger, IServiceProvider sp, IMinioClient minio, IConfiguration config)
    {
        _logger = logger;
        _sp = sp;
        _minio = minio;
        _sentryPath = config["TeslaCam:SentryClipsPath"] ?? "/mnt/sentry";
        _savedPath = config["TeslaCam:SavedClipsPath"] ?? "/mnt/saved";
        _minioBucket = config["MinIO:BucketName"] ?? "teslacam-videos";
        _enableReverseGeocoding = config.GetValue<bool>("TeslaCam:EnableReverseGeocoding");
        _scanIntervalSeconds = config.GetValue<int>("TeslaCam:ScanIntervalSeconds", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClipScanner started, SentryPath={SentryPath}, SavedPath={SavedPath}, MinIOBucket={Bucket}, ScanInterval={ScanInterval}s",
            _sentryPath, _savedPath, _minioBucket, _scanIntervalSeconds);

        // Ensure MinIO bucket exists
        await EnsureBucketExists(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanRoot(_sentryPath, "Sentry", stoppingToken);
                await ScanRoot(_savedPath, "Saved", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan error");
            }

            await Task.Delay(TimeSpan.FromSeconds(_scanIntervalSeconds), stoppingToken);
        }
    }

    private async Task EnsureBucketExists(CancellationToken ct)
    {
        try
        {
            var bucketExists = await _minio.BucketExistsAsync(new BucketExistsArgs()
                .WithBucket(_minioBucket), ct);

            if (!bucketExists)
            {
                await _minio.MakeBucketAsync(new MakeBucketArgs()
                    .WithBucket(_minioBucket), ct);
                _logger.LogInformation("Created MinIO bucket: {Bucket}", _minioBucket);
            }
            else
            {
                _logger.LogInformation("MinIO bucket exists: {Bucket}", _minioBucket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure MinIO bucket exists");
        }
    }

    private async Task ScanRoot(string root, string source, CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            _logger.LogWarning("Directory {Root} does not exist, skipping", root);
            return;
        }

        _logger.LogInformation("Scanning {Source} clips in {Root}", source, root);

        using var scope = _sp.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var eventsProcessed = 0;
        var clipsInserted = 0;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var folderName = Path.GetFileName(dir);
            var evt = await db.Events.FirstOrDefaultAsync(e => e.FolderName == folderName && e.Source == source, ct);
            if (evt == null)
            {
                _logger.LogInformation("Waiting for folder: {Folder} to be quiet", dir);
                await WaitForDirectoryQuiet(dir, FolderQuietPeriod, FolderQuietPollInterval, FolderQuietStatusInterval, ct);
                evt = new Event { FolderName = folderName, CreatedAt = DateTime.UtcNow, Source = source };
                var eventJson = Path.Combine(dir, "event.json");
                if (File.Exists(eventJson))
                {
                    try
                    {
                        using var s = File.OpenRead(eventJson);
                        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                        evt.Type = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "unknown" : "unknown";
                        evt.City = doc.RootElement.TryGetProperty("city", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                        evt.Street = doc.RootElement.TryGetProperty("street", out var st) ? st.GetString() ?? string.Empty : string.Empty;
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsProp))
                        {
                            var tsText = tsProp.GetString();
                            if (!string.IsNullOrWhiteSpace(tsText))
                            {
                                // Parse as UTC if no offset provided
                                if (DateTime.TryParse(tsText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                                {
                                    evt.TimeStamp = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                                }
                                else
                                {
                                    throw new Exception($"Failed to parse timestamp '{tsText}' in event.json");
                                }
                            }
                            else
                            {
                                throw new Exception($"Timestamp in event.json is empty");
                            }
                        }
                        else
                        {
                            throw new Exception($"Timestamp not found in event.json");
                        }
                        evt.Long = doc.RootElement.TryGetProperty("est_lon", out var lon) ? lon.GetString() ?? string.Empty : string.Empty;
                        evt.Lat = doc.RootElement.TryGetProperty("est_lat", out var lat) ? lat.GetString() ?? string.Empty : string.Empty;
                        if (doc.RootElement.TryGetProperty("camera", out var camera))
                        {
                            if (!int.TryParse(camera.GetString(), out var camInt))
                            {
                                throw new Exception($"Failed to parse camera value '{camera.GetString()}' in event.json");
                            }
                            evt.Camera = camInt;
                        }

                        if (_enableReverseGeocoding
                            && double.TryParse(evt.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latValue)
                            && double.TryParse(evt.Long, NumberStyles.Float, CultureInfo.InvariantCulture, out var lonValue))
                        {
                            _logger.LogInformation("Reverse geocoding enabled for {FolderName} at {Lat},{Lon}", folderName, latValue, lonValue);
                            await TryPopulateAddressAsync(evt, latValue, lonValue, ct);
                            _logger.LogInformation("Reverse geocoding result for {FolderName}: Street={Street}, City={City}", folderName, evt.Street, evt.City);
                        }
                        _logger.LogDebug("Parsed event.json for {FolderName}: Type={Type}", folderName, evt.Type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read event.json in {Dir}", dir);
                    }

                    var eventThumbnail = Path.Combine(dir, "thumb.png");
                    if (File.Exists(eventThumbnail))
                    {
                        try
                        {
                            evt.Thumbnail = await File.ReadAllBytesAsync(eventThumbnail, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to read thumb.png in {Dir}", dir);
                        }
                    }
                }
                db.Events.Add(evt);
                eventsProcessed++;
                _logger.LogInformation("New event detected: {FolderName} ({Source})", folderName, source);

                // Save the event first to get its ID before creating cameras
                await db.SaveChangesAsync(ct);

                // Simplified - no redundant checks
                foreach (var mp4 in Directory.EnumerateFiles(dir, "*.mp4"))
                {
                    var name = Path.GetFileName(mp4);
                    var camera = GetCameraFromName(name);
                    var ts = GetTimestampFromName(name) ?? DateTime.UtcNow;
                    
                    db.Clips.Add(new Clip { Camera = camera, Path = mp4, Timestamp = ts, Event = evt });
                    clipsInserted++;
                    _logger.LogDebug("Queued clip insert: {Camera} {Timestamp:u} -> {Path}", camera, ts, mp4);
                }

                await StitchAndStoreCameras(evt, dir, db, ct);
                await db.SaveChangesAsync(ct);

                // Log just the clips for THIS event instead of all clips
                _logger.LogInformation("Processed folder {FolderName}: clips added={ClipsAdded}", folderName, clipsInserted);
            }
        }

        _logger.LogInformation("Scan completed for {Source}. EventsProcessed={EventsProcessed}, ClipsInserted={ClipsInserted}", source, eventsProcessed, clipsInserted);
    }

    private async Task StitchAndStoreCameras(Event evt, string dir, AppDbContext db, CancellationToken ct)
    {
        var cameraNames = new[] { "front", "back", "left_repeater", "right_repeater" };

        foreach (var cameraName in cameraNames)
        {
            try
            {
                // Check if camera already exists for this event (quick DB check)
                if (await db.Cameras.AnyAsync(c => c.EventId == evt.Id && c.CameraName == cameraName, ct))
                {
                    _logger.LogDebug("Camera {CameraName} already exists for event {EventId}, skipping", cameraName, evt.Id);
                    continue;
                }

                // Get all MP4 files for this camera, sorted by timestamp
                var mp4Files = Directory.EnumerateFiles(dir, "*.mp4")
                    .Where(f => GetCameraFromName(Path.GetFileName(f)) == cameraName)
                    .OrderBy(f => GetTimestampFromName(Path.GetFileName(f)))
                    .ToList();

                if (!mp4Files.Any())
                {
                    _logger.LogDebug("No MP4 files found for camera {CameraName} in {Dir}", cameraName, dir);
                    continue;
                }

                _logger.LogInformation("Processing {Count} videos for camera {CameraName} in event {EventId}", mp4Files.Count, cameraName, evt.Id);

                // Process video and upload to MinIO (WITHOUT holding DB connection)
                var cameraData = await ProcessAndUploadCamera(evt, cameraName, mp4Files, ct);

                if (cameraData != null)
                {
                    // Quick database insert after upload is complete
                    db.Cameras.Add(cameraData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process camera {CameraName} for event {EventId}", cameraName, evt.Id);
            }
        }
    }

    private async Task<Camera?> ProcessAndUploadCamera(Event evt, string cameraName, List<string> mp4Files, CancellationToken ct)
    {
        // Create temporary output file
        var tempOutput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp4");

        try
        {
            string fileToUpload;
            DateTime timestamp;
            TimeSpan duration;
            long fileSize;

            if (mp4Files.Count == 1)
            {
                // Single file - upload directly
                fileToUpload = mp4Files[0];
                timestamp = GetTimestampFromName(Path.GetFileName(mp4Files[0])) ?? DateTime.UtcNow;

                // Get video duration
                var mediaInfo = await FFProbe.AnalyseAsync(fileToUpload, cancellationToken: ct);
                duration = mediaInfo.Duration;
                fileSize = new FileInfo(fileToUpload).Length;

                _logger.LogInformation("Single video for camera {CameraName}: {Size} bytes, duration: {Duration}",
                    cameraName, fileSize, duration);
            }
            else
            {
                // Multiple files - stitch them together
                await StitchVideos(mp4Files, tempOutput, ct);

                fileToUpload = tempOutput;
                timestamp = GetTimestampFromName(Path.GetFileName(mp4Files[0])) ?? DateTime.UtcNow;

                // Get duration of stitched video
                var mediaInfo = await FFProbe.AnalyseAsync(fileToUpload, cancellationToken: ct);
                duration = mediaInfo.Duration;
                fileSize = new FileInfo(fileToUpload).Length;

                _logger.LogInformation("Stitched {Count} videos for camera {CameraName}: {Size} bytes, duration: {Duration}",
                    mp4Files.Count, cameraName, fileSize, duration);
            }

            // Upload to MinIO (this can take time, but DB connection is not held)
            var minioPath = $"events/{evt.FolderName}/{cameraName}.mp4";
            await UploadToMinio(fileToUpload, minioPath, ct);

            //await _clipAnalyzer.AnalyzeAsync(fileToUpload);

            // Return camera data to be inserted
            return new Camera
            {
                CameraName = cameraName,
                MinioPath = minioPath,
                BucketName = _minioBucket,
                Timestamp = timestamp,
                Duration = duration,
                FileSize = fileSize,
                EventId = evt.Id
            };
        }
        finally
        {
            // Clean up temp file if it was created
            if (mp4Files.Count > 1 && File.Exists(tempOutput))
            {
                try
                {
                    File.Delete(tempOutput);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file {TempOutput}", tempOutput);
                }
            }
        }
    }

    private async Task UploadToMinio(string filePath, string minioPath, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Uploading {FilePath} to MinIO bucket {Bucket} as {Path}",
                filePath, _minioBucket, minioPath);

            using var fileStream = File.OpenRead(filePath);
            await _minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(_minioBucket)
                .WithObject(minioPath)
                .WithStreamData(fileStream)
                .WithObjectSize(fileStream.Length)
                .WithContentType("video/mp4"), ct);

            _logger.LogInformation("Successfully uploaded to MinIO: {Path}", minioPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload to MinIO: {Path}", minioPath);
            throw;
        }
    }

    private async Task StitchVideos(List<string> inputFiles, string outputFile, CancellationToken ct)
    {
        // Create a temporary text file listing all input videos for FFmpeg concat
        var concatFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");

        try
        {
            // Write concat file
            var lines = inputFiles.Select(f => $"file '{f.Replace("\\", "/")}'");
            await File.WriteAllLinesAsync(concatFile, lines, ct);

            _logger.LogDebug("Created concat file: {ConcatFile}", concatFile);

            // Use FFmpeg to concatenate videos
            var ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" -c copy \"{outputFile}\"";

            await FFMpegArguments
                .FromFileInput(concatFile, false, options => options
                    .WithCustomArgument("-f concat")
                    .WithCustomArgument("-safe 0"))
                .OutputToFile(outputFile, true, options => options
                    .CopyChannel())
                .ProcessAsynchronously();

            _logger.LogDebug("FFmpeg concatenation completed: {OutputFile}", outputFile);
        }
        finally
        {
            // Clean up concat file
            if (File.Exists(concatFile))
            {
                try
                {
                    File.Delete(concatFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete concat file {ConcatFile}", concatFile);
                }
            }
        }
    }

    private static string GetCameraFromName(string fileName)
    {
        fileName = fileName.ToLowerInvariant();
        if (fileName.Contains("back")) return "back";
        if (fileName.Contains("front")) return "front";
        if (fileName.Contains("left_repeater")) return "left_repeater";
        if (fileName.Contains("right_repeater")) return "right_repeater";
        return "unknown";
    }

    private static DateTime? GetTimestampFromName(string fileName)
    {
        // Example: 2026-02-04_10-25-44-back.mp4
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        // split on '_' and '-'
        var parts = baseName.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

        // parts now contains:
        // [0]=2026, [1]=02, [2]=04, [3]=10, [4]=24, [5]=44, [6]=back

        int year = int.Parse(parts[0]);
        int month = int.Parse(parts[1]);
        int day = int.Parse(parts[2]);
        int hour = int.Parse(parts[3]);
        int minute = int.Parse(parts[4]);
        int second = int.Parse(parts[5]);

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    private async Task WaitForDirectoryQuiet(string directory, TimeSpan quietPeriod, TimeSpan pollInterval, TimeSpan statusInterval, CancellationToken ct)
    {
        DateTime? lastChange = null;
        DateTime? lastObserved = null;
        var lastStatusLog = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            // Check if required files exist
            var eventJsonPath = Path.Combine(directory, "event.json");
            var thumbPngPath = Path.Combine(directory, "thumb.png");
            var mp4Files = Directory.EnumerateFiles(directory, "*.mp4").ToList();

            bool hasRequiredFiles = File.Exists(eventJsonPath)
                && File.Exists(thumbPngPath)
                && mp4Files.Count >= 4;

            if (!hasRequiredFiles)
            {
                if (DateTime.UtcNow - lastStatusLog >= statusInterval)
                {
                    _logger.LogInformation("Waiting for required files in folder: {Folder} (event.json: {EventJson}, thumb.png: {ThumbPng}, mp4 files: {Mp4Count}/4)",
                        directory, File.Exists(eventJsonPath), File.Exists(thumbPngPath), mp4Files.Count);
                    lastStatusLog = DateTime.UtcNow;
                }

                await Task.Delay(pollInterval, ct);
                continue;
            }

            DateTime? latestWrite = null;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var writeTime = File.GetLastWriteTimeUtc(file);
                if (latestWrite == null || writeTime > latestWrite)
                {
                    latestWrite = writeTime;
                }
            }

            if (latestWrite == null)
            {
                latestWrite = DateTime.UtcNow;
            }

            if (lastObserved == null || latestWrite != lastObserved)
            {
                lastObserved = latestWrite;
                lastChange = DateTime.UtcNow;
            }

            if (lastChange != null && DateTime.UtcNow - lastChange >= quietPeriod)
            {
                _logger.LogInformation("Folder is ready: {Folder} (all required files present and quiet period met)", directory);
                return;
            }

            if (DateTime.UtcNow - lastStatusLog >= statusInterval)
            {
                _logger.LogInformation("Still waiting for folder quiet: {Folder}", directory);
                lastStatusLog = DateTime.UtcNow;
            }

            await Task.Delay(pollInterval, ct);
        }
    }

    private static HttpClient CreateGeoHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TeslaCamViewer", "1.0"));
        return client;
    }

    private async Task TryPopulateAddressAsync(Event evt, double lat, double lon, CancellationToken ct)
    {
        try
        {
            var url = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={lat.ToString(CultureInfo.InvariantCulture)}&lon={lon.ToString(CultureInfo.InvariantCulture)}&zoom=18&addressdetails=1";
            using var response = await GeoHttpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("address", out var address))
            {
                var street = GetAddressValue(address, "road")
                    ?? GetAddressValue(address, "neighbourhood")
                    ?? GetAddressValue(address, "suburb");
                if (!string.IsNullOrWhiteSpace(street))
                {
                    if (!string.IsNullOrWhiteSpace(evt.Street) && !string.Equals(evt.Street, street, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("Overriding street from reverse geocoding: {OldStreet} -> {NewStreet}", evt.Street, street);
                    }
                    evt.Street = street;
                }

                if (string.IsNullOrWhiteSpace(evt.City))
                {
                    evt.City = GetAddressValue(address, "city")
                        ?? GetAddressValue(address, "town")
                        ?? GetAddressValue(address, "village")
                        ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reverse-geocode address for {Lat},{Lon}", lat, lon);
        }
    }

    private static string? GetAddressValue(JsonElement address, string key)
    {
        return address.TryGetProperty(key, out var value) ? value.GetString() : null;
    }
}
