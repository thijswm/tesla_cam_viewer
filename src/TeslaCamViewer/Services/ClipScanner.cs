using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Tracing;
using System.Text.Json;
using System.Xml.Linq;
using TeslaCamViewer.Data;

namespace TeslaCamViewer.Services;

public class ClipScanner : BackgroundService
{
    private readonly ILogger<ClipScanner> _logger;
    private readonly IServiceProvider _sp;
    private readonly string _sentryPath;
    private readonly string _savedPath;

    public ClipScanner(ILogger<ClipScanner> logger, IServiceProvider sp, IConfiguration config)
    {
        _logger = logger;
        _sp = sp;
        _sentryPath = config["TeslaCam:SentryClipsPath"] ?? "/mnt/sentry";
        _savedPath = config["TeslaCam:SavedClipsPath"] ?? "/mnt/saved";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClipScanner started, SentryPath={SentryPath}, SavedPath={SavedPath}", _sentryPath, _savedPath);
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

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var eventsProcessed = 0;
        var clipsInserted = 0;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var folderName = Path.GetFileName(dir);
            var evt = await db.Events.FirstOrDefaultAsync(e => e.FolderName == folderName && e.Source == source, ct);
            if (evt == null)
            {
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
                        evt.Long = doc.RootElement.TryGetProperty("long", out var lon) ? lon.GetString() ?? string.Empty : string.Empty;
                        evt.Lat = doc.RootElement.TryGetProperty("lat", out var lat) ? lat.GetString() ?? string.Empty : string.Empty;
                        if (doc.RootElement.TryGetProperty("camera", out var camera))
                        {
                            if (!int.TryParse(camera.GetString(), out var camInt))
                            {
                                throw new Exception($"Failed to parse camera value '{camera.GetString()}' in event.json");
                            }
                            evt.Camera = camInt;
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
            }

            foreach (var mp4 in Directory.EnumerateFiles(dir, "*.mp4"))
            {
                var name = Path.GetFileName(mp4);
                var camera = GetCameraFromName(name);
                var ts = GetTimestampFromName(name) ?? DateTime.UtcNow;
                if (!await db.Clips.AnyAsync(c => c.Path == mp4, ct))
                {
                    db.Clips.Add(new Clip { Camera = camera, Path = mp4, Timestamp = ts, Event = evt });
                    clipsInserted++;
                    _logger.LogDebug("Queued clip insert: {Camera} {Timestamp:u} -> {Path}", camera, ts, mp4);
                }
                else
                {
                    _logger.LogTrace("Clip already indexed, skipping: {Path}", mp4);
                }
            }
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Processed folder {FolderName}: total clips now={TotalClips}", folderName, await db.Clips.CountAsync(ct));
        }

        _logger.LogInformation("Scan completed for {Source}. EventsProcessed={EventsProcessed}, ClipsInserted={ClipsInserted}", source, eventsProcessed, clipsInserted);
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
}
