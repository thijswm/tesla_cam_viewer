using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;
using TeslaCamViewer.Shared;
using System.Timers;

namespace TeslaCamViewer.Pages;

public partial class Index : IDisposable
{
    [Inject] public AppDbContext Db { get; set; } = default!;
    [Inject] public ILogger<Index> Logger { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    [CascadingParameter] public ClipItem? SelectedEvent { get; set; }

    private List<CameraMetadata>? cameras;
    private bool isLoadingCameras;
    private bool isPlaying = false;

    // Timeline properties
    private double currentTime = 0;
    private double videoDuration = 60; // Default 60 seconds, will be updated from video
    private DateTime? minTimestamp = null; // Earliest camera timestamp (reference point for time 0)
    private System.Timers.Timer? updateTimer;

    // Lightweight camera metadata without video data
    private class CameraMetadata
    {
        public int Id { get; set; }
        public string CameraName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public int EventId { get; set; }
    }

    protected override async Task OnParametersSetAsync()
    {
        // Stop timer immediately when switching events for better responsiveness
        StopTimelineUpdater();

        if (SelectedEvent != null)
        {
            // Reset state
            isPlaying = false;
            currentTime = 0;

            isLoadingCameras = true;
            StateHasChanged();

            // Pause all videos from previous event
            try
            {
                await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => { v.pause(); v.currentTime = 0; })");
            }
            catch
            {
                // Ignore errors if no videos exist yet
            }

            // Load camera metadata without VideoData
            await LoadCameraMetadata();

            isLoadingCameras = false;
            StateHasChanged();

            // Start timeline update timer for new event
            StartTimelineUpdater();

            // Initialize map with event location
            await InitializeEventMap();
        }
        else
        {
            SelectedEvent = null;
            cameras = null;
            videoDuration = 60; // Reset to default
            minTimestamp = null; // Reset reference timestamp
            currentTime = 0;
            isPlaying = false;
        }
    }

    private async Task InitializeEventMap()
    {
        try
        {
            if (SelectedEvent?.Event == null)
            {
                Logger.LogWarning("Cannot initialize map - no event selected");
                return;
            }

            Logger.LogInformation("Attempting to initialize map for event {EventId}, Lat={Lat}, Long={Long}",
                SelectedEvent.Event.Id, SelectedEvent.Event.Lat, SelectedEvent.Event.Long);

            // Parse latitude and longitude from strings
            if (double.TryParse(SelectedEvent.Event.Lat, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(SelectedEvent.Event.Long, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                // Give the DOM more time to render the map container
                await Task.Delay(300);
            }
            else
            {
                Logger.LogWarning("Invalid coordinates for event {EventId}: Lat={Lat}, Long={Long}",
                    SelectedEvent.Event.Id, SelectedEvent.Event.Lat, SelectedEvent.Event.Long);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize event map");
        }
    }

    private async Task LoadCameraMetadata()
    {
        try
        {
            if (SelectedEvent?.Event?.Id == null)
            {
                cameras = null;
                return;
            }

            // Load cameras without the VideoData to avoid loading large byte arrays
            cameras = await Db.Cameras
                .Where(c => c.EventId == SelectedEvent.Event.Id)
                .Select(c => new CameraMetadata
                {
                    Id = c.Id,
                    CameraName = c.CameraName,
                    Timestamp = c.Timestamp,
                    Duration = c.Duration,
                    EventId = c.EventId
                })
                .ToListAsync();

            // Set video duration from earliest camera start to latest camera end
            if (cameras.Any())
            {
                // Get the earliest timestamp as reference point (time 0)
                minTimestamp = cameras.Min(c => c.Timestamp);

                // Calculate the latest end time
                var maxEndTime = cameras.Max(c => c.Timestamp + c.Duration);

                // Total duration from earliest start to latest end
                videoDuration = (maxEndTime - minTimestamp.Value).TotalSeconds;

                Logger.LogInformation("Loaded {Count} cameras for event {EventId}, duration: {Duration}, reference timestamp: {MinTimestamp}",
                    cameras.Count, SelectedEvent.Event.Id, TimeSpan.FromSeconds(videoDuration), minTimestamp);
            }
            else
            {
                videoDuration = 60; // Default fallback
                minTimestamp = null;
                Logger.LogInformation("No cameras found for event {EventId}", SelectedEvent.Event.Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load camera metadata for event {EventId}", SelectedEvent?.Event?.Id);
            cameras = null;
        }
    }

    private void StartTimelineUpdater()
    {
        StopTimelineUpdater();
        updateTimer = new System.Timers.Timer(200); // Update every 200ms for smoother updates
        updateTimer.Elapsed += async (sender, e) => await UpdateCurrentTime();
        updateTimer.Start();
    }

    private void StopTimelineUpdater()
    {
        if (updateTimer != null)
        {
            updateTimer.Stop();
            updateTimer.Dispose();
            updateTimer = null;
        }
    }

    private async Task UpdateCurrentTime()
    {
        try
        {
            var time = await JS.InvokeAsync<double>("eval",
                "(() => { const v = document.querySelector('video'); return v ? v.currentTime : 0; })()");

            // Update if difference is significant (reduced to 0.1 seconds for smoother slider)
            if (Math.Abs(currentTime - time) > 0.1)
            {
                currentTime = time;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch
        {
            // Ignore errors when videos aren't loaded yet
        }
    }

    private async Task OnTimelineChanged(double newTime)
    {
        currentTime = newTime;
        await JS.InvokeVoidAsync("eval", $"document.querySelectorAll('video').forEach(v => v.currentTime = {newTime})");
    }

    private string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    private string FormatTimeWithTimestamp(double seconds)
    {
        // If we have a minimum timestamp, show actual clock time
        if (minTimestamp.HasValue)
        {
            var actualTime = minTimestamp.Value.AddSeconds(seconds);
            return actualTime.ToString("HH:mm:ss");
        }

        // Otherwise show elapsed time
        return FormatTime(seconds);
    }

    private int GetTimeMarkerCount()
    {
        // Show markers based on video duration
        // Short videos (< 30s): 6 markers
        // Medium videos (30s - 2min): 8 markers
        // Long videos (> 2min): 10 markers
        if (videoDuration < 30)
            return 6;
        else if (videoDuration < 120)
            return 8;
        else
            return 10;
    }

    private string GetVideoUrl(CameraMetadata camera)
    {
        // API endpoint to serve the stitched camera video from byte array
        return $"/api/camera/{camera.Id}";
    }

    private CameraMetadata? GetCameraByName(string cameraName)
    {
        // Get the camera metadata for this event (without VideoData)
        return cameras?
            .FirstOrDefault(c => c.CameraName.Equals(cameraName, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsEventCamera(string cameraName)
    {
        // Check if this camera triggered the event based on Event.Camera integer
        // 1 = front
        // 2 = back
        // 3 = left_repeater
        // 4 = right_repeater
        // 5 = either left_repeater or right_repeater (both get red border)
        if (SelectedEvent?.Event == null) return false;

        return SelectedEvent.Event.Camera switch
        {
            1 => cameraName.Equals("front", StringComparison.OrdinalIgnoreCase),
            2 => cameraName.Equals("back", StringComparison.OrdinalIgnoreCase),
            3 => cameraName.Equals("left_repeater", StringComparison.OrdinalIgnoreCase),
            4 => cameraName.Equals("right_repeater", StringComparison.OrdinalIgnoreCase),
            5 => cameraName.Equals("left_repeater", StringComparison.OrdinalIgnoreCase) ||
                 cameraName.Equals("right_repeater", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private async Task TogglePlayPause()
    {
        if (isPlaying)
        {
            await PauseAllVideos();
        }
        else
        {
            await PlayAllVideos();
        }
    }

    private async Task PlayAllVideos()
    {
        await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => v.play())");
        isPlaying = true;
    }

    private async Task PauseAllVideos()
    {
        await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => v.pause())");
        isPlaying = false;
    }

    private async Task SkipForward()
    {
        await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => v.currentTime += 5)");
    }

    private async Task SkipBackward()
    {
        await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => v.currentTime -= 5)");
    }

    public void Dispose()
    {
        StopTimelineUpdater();

        // Clean up map
        try
        {
            JS.InvokeVoidAsync("destroyMap");
        }
        catch
        {
            // Ignore errors during disposal
        }
    }
}
