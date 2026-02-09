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

    // Timeline properties
    private double currentTime = 0;
    private double videoDuration = 60; // Default 60 seconds, will be updated from video
    private System.Timers.Timer? updateTimer;
    private string currentTimeFormatted => FormatTime(currentTime);
    private string durationFormatted => FormatTime(videoDuration);

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
        if (SelectedEvent != null)
        {
            isLoadingCameras = true;
            StateHasChanged();

            // Load camera metadata without VideoData
            await LoadCameraMetadata();

            isLoadingCameras = false;
            StateHasChanged();

            // Start timeline update timer
            StartTimelineUpdater();
        }
        else
        {
            SelectedEvent = null;
            cameras = null;
            videoDuration = 60; // Reset to default
            StopTimelineUpdater();
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

            // Set video duration to the max duration of all cameras
            if (cameras.Any())
            {
                var maxDuration = cameras.Max(c => c.Duration);
                videoDuration = maxDuration.TotalSeconds;
                Logger.LogInformation("Loaded {Count} cameras for event {EventId}, max duration: {Duration}", 
                    cameras.Count, SelectedEvent.Event.Id, maxDuration);
            }
            else
            {
                videoDuration = 60; // Default fallback
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

    private async Task UpdateVideoDuration()
    {
        try
        {
            var duration = await JS.InvokeAsync<double>("eval", 
                "(() => { const v = document.querySelector('video'); return v ? v.duration : 60; })()");
            if (!double.IsNaN(duration) && duration > 0)
            {
                videoDuration = duration;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch
        {
            // Use default duration if unable to get from video
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

    private string GetVideoUrl(Clip clip)
    {
        // API endpoint to serve the video file
        return $"/api/video/{clip.Id}";
    }

    private string GetVideoUrl(Camera camera)
    {
        // API endpoint to serve the stitched camera video from byte array
        return $"/api/camera/{camera.Id}";
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

    private string GetCameraDisplayName(string camera)
    {
        return camera.ToLowerInvariant() switch
        {
            "front" => "Front",
            "back" => "Back",
            "left_repeater" => "Left Repeater",
            "right_repeater" => "Right Repeater",
            _ => camera
        };
    }

    private async Task PlayAllVideos()
    {
        await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => v.play())");
    }

    private async Task PauseAllVideos()
    {
        await JS.InvokeVoidAsync("eval", "document.querySelectorAll('video').forEach(v => v.pause())");
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
    }
}
