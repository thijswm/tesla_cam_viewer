using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TeslaCamViewer.Data;
using TeslaCamViewer.Shared;
using System.Timers;

namespace TeslaCamViewer.Pages;

public partial class Index : IDisposable
{
    [Inject] public AppDbContext Db { get; set; } = default!;
    [Inject] public ILogger<Index> Logger { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    [CascadingParameter] public ClipItem? SelectedClip { get; set; }

    private List<KeyValuePair<Clip, string?>>? thumbnails;
    private bool isLoadingThumbnails;
    private Clip? selectedClip;

    // Timeline properties
    private double currentTime = 0;
    private double videoDuration = 60; // Default 60 seconds, will be updated from video
    private System.Timers.Timer? updateTimer;
    private string currentTimeFormatted => FormatTime(currentTime);
    private string durationFormatted => FormatTime(videoDuration);

    protected override async Task OnParametersSetAsync()
    {
        if (SelectedClip != null)
        {
            isLoadingThumbnails = true;
            StateHasChanged();

            thumbnails = await SelectedClip.GetThumbnailsForAllClipsAsync(Logger);

            isLoadingThumbnails = false;
            StateHasChanged();

            // Start timeline update timer
            StartTimelineUpdater();

            // Get video duration from the first video
            await Task.Delay(500); // Wait for videos to load
            await UpdateVideoDuration();
        }
        else
        {
            thumbnails = null;
            selectedClip = null;
            StopTimelineUpdater();
        }
    }

    private void StartTimelineUpdater()
    {
        StopTimelineUpdater();
        updateTimer = new System.Timers.Timer(100); // Update every 100ms
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
            if (Math.Abs(currentTime - time) > 0.5) // Only update if difference is significant
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

    private void OnClipSelected(Clip clip)
    {
        selectedClip = clip;
        StateHasChanged();
    }

    private string GetVideoUrl(Clip clip)
    {
        // API endpoint to serve the video file
        return $"/api/video/{clip.Id}";
    }

    private Clip? GetClipByCamera(string cameraName)
    {
        return thumbnails?.FirstOrDefault(t => t.Key.Camera.Equals(cameraName, StringComparison.OrdinalIgnoreCase)).Key;
    }

    private string? GetThumbnailForClip(Clip clip)
    {
        return thumbnails?.FirstOrDefault(t => t.Key.Id == clip.Id).Value;
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
