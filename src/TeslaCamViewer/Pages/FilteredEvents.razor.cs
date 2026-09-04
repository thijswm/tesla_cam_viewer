using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;
using TeslaCamViewer.Shared;
using System.Globalization;

namespace TeslaCamViewer.Pages;

public partial class FilteredEvents : IAsyncDisposable
{
    [Inject] public IDbContextFactory<AppDbContext>? DbFactory { get; set; }
    [Inject] public ILogger<FilteredEvents> Logger { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "city")]
    public string? filterCity { get; set; }

    [SupplyParameterFromQuery(Name = "date")]
    public string? filterDateString { get; set; }

    [SupplyParameterFromQuery(Name = "source")]
    public string? filterSource { get; set; }

    private DateTime? filterDate => DateTime.TryParse(filterDateString, out var date) ? date : null;

    private List<ClipItem> _filteredEvents = new();
    private ClipItem? _selectedEvent;
    private bool isLoading = true;
    private bool isLoadingCameras = false;

    private List<string> _availableCities = new();
    private List<string> _availableSources = new();
    private List<DateTime> _eventDates = new();

    private List<CameraMetadata>? cameras;
    private bool isPlaying = false;

    // Timeline properties
    private double currentTime = 0;
    private double videoDuration = 60; // Default 60 seconds, will be updated from video
    private DateTime? minTimestamp = null; // Earliest camera timestamp (reference point for time 0)
    private DotNetObjectReference<FilteredEvents>? _jsRef;

    // Lightweight camera metadata without video data
    private class CameraMetadata
    {
        public int Id { get; set; }
        public string CameraName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public int EventId { get; set; }
    }

    protected override async Task OnInitializedAsync()
    {
        // If no filters are set, default to today's date
        if (string.IsNullOrEmpty(filterCity) && 
            string.IsNullOrEmpty(filterDateString) && 
            string.IsNullOrEmpty(filterSource))
        {
            filterDateString = DateTime.Today.ToString("yyyy-MM-dd");
            // Update URL to reflect the default date filter
            Navigation.NavigateTo($"/events?date={filterDateString}", forceLoad: false);
        }

        await LoadAvailableFilters();
        await LoadFilteredEvents();
        isLoading = false;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!isLoading)
        {
            await LoadAvailableFilters(); // Reload filters based on new parameters
            await LoadFilteredEvents();
            StateHasChanged();
        }
    }

    private async Task LoadAvailableFilters()
    {
        if (DbFactory == null) return;

        await using var db = await DbFactory.CreateDbContextAsync();

        // Build base query with current filters applied
        var query = db.Events.AsNoTracking();

        // Load cities based on current source and date filters
        var citiesQuery = query;
        if (!string.IsNullOrEmpty(filterSource))
            citiesQuery = citiesQuery.Where(e => e.Source == filterSource);
        if (filterDate.HasValue)
        {
            var (startDate, endDate) = EventLocalTime.UtcRangeForLocalDate(filterDate.Value);
            citiesQuery = citiesQuery.Where(e => e.TimeStamp >= startDate && e.TimeStamp < endDate);
        }
        _availableCities = await citiesQuery
            .Where(e => !string.IsNullOrEmpty(e.City))
            .Select(e => e.City)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        // Load sources based on current city and date filters
        var sourcesQuery = query;
        if (!string.IsNullOrEmpty(filterCity))
            sourcesQuery = sourcesQuery.Where(e => e.City.Contains(filterCity));
        if (filterDate.HasValue)
        {
            var (startDate, endDate) = EventLocalTime.UtcRangeForLocalDate(filterDate.Value);
            sourcesQuery = sourcesQuery.Where(e => e.TimeStamp >= startDate && e.TimeStamp < endDate);
        }
        _availableSources = await sourcesQuery
            .Where(e => !string.IsNullOrEmpty(e.Source))
            .Select(e => e.Source)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();

        // Load event dates based on current city and source filters
        var datesQuery = query;
        if (!string.IsNullOrEmpty(filterCity))
            datesQuery = datesQuery.Where(e => e.City.Contains(filterCity));
        if (!string.IsNullOrEmpty(filterSource))
            datesQuery = datesQuery.Where(e => e.Source == filterSource);
        _eventDates = (await datesQuery
                .Select(e => e.TimeStamp)
                .ToListAsync())
            .Select(EventLocalTime.ToLocalDate)
            .Distinct()
            .ToList();
    }

    private async Task LoadFilteredEvents()
    {
        if (DbFactory == null) return;

        await using var db = await DbFactory.CreateDbContextAsync();
        
        var query = db.Events.AsNoTracking();

        // Apply filters
        if (!string.IsNullOrEmpty(filterCity))
        {
            query = query.Where(e => e.City.Contains(filterCity));
        }

        if (filterDate.HasValue)
        {
            var (startDate, endDate) = EventLocalTime.UtcRangeForLocalDate(filterDate.Value);
            query = query.Where(e => e.TimeStamp >= startDate && e.TimeStamp < endDate);
        }

        if (!string.IsNullOrEmpty(filterSource))
        {
            query = query.Where(e => e.Source == filterSource);
        }

        var events = await query
            .OrderByDescending(e => e.TimeStamp)
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
        _filteredEvents = events.Select(e => new ClipItem(e)).ToList();
    }

    private async Task OnClipClicked(ClipItem clipItem)
    {
        await StopTimelineUpdater();

        _selectedEvent = clipItem;

        // Reset state
        isPlaying = false;
        currentTime = 0;

        isLoadingCameras = true;
        StateHasChanged();

        await PlayerInvokeVoid("reset");

        // Load camera metadata
        await LoadCameraMetadata();

        isLoadingCameras = false;
        StateHasChanged();

        await Task.Delay(100);

        await InitializeEventMap();

        await PlayerInvokeVoid("reload");
        await StartTimelineUpdater();
    }

    private async Task LoadCameraMetadata()
    {
        if (_selectedEvent == null || DbFactory == null) return;

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            cameras = await db.Cameras
                .Where(c => c.EventId == _selectedEvent.Event.Id)
                .Select(c => new CameraMetadata
                {
                    Id = c.Id,
                    CameraName = c.CameraName,
                    Timestamp = c.Timestamp,
                    Duration = c.Duration,
                    EventId = c.EventId
                })
                .ToListAsync();

            if (cameras?.Count > 0)
            {
                minTimestamp = cameras.Min(c => c.Timestamp);
                var maxTimestamp = cameras.Max(c => c.Timestamp.Add(c.Duration));
                videoDuration = (maxTimestamp - minTimestamp.Value).TotalSeconds;

                Logger.LogInformation("Loaded {Count} cameras for event {EventId}, duration: {Duration}s",
                    cameras.Count, _selectedEvent.Event.Id, videoDuration);
            }
            else
            {
                Logger.LogWarning("No cameras found for event {EventId}", _selectedEvent.Event.Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load camera metadata for event {EventId}", _selectedEvent?.Event.Id);
            cameras = null;
        }
    }

    private CameraMetadata? GetCameraByName(string cameraName)
    {
        return cameras?.FirstOrDefault(c => c.CameraName.Equals(cameraName, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsEventCamera(string cameraName)
    {
        if (_selectedEvent?.Event == null) return false;

        var cameraMap = new Dictionary<int, string>
        {
            { 0, "front" },
            { 1, "back" },
            { 2, "left_repeater" },
            { 3, "right_repeater" }
        };

        return cameraMap.TryGetValue(_selectedEvent.Event.Camera, out var name) &&
               name.Equals(cameraName, StringComparison.OrdinalIgnoreCase);
    }

    private string GetCameraOffsetSeconds(CameraMetadata camera)
    {
        if (!minTimestamp.HasValue)
        {
            return "0";
        }

        var offset = Math.Max(0, (camera.Timestamp - minTimestamp.Value).TotalSeconds);
        return offset.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private string GetVideoUrl(CameraMetadata camera)
    {
        return $"/api/camera/{camera.Id}";
    }

    private string GetDownloadFileName(string cameraName)
    {
        if (_selectedEvent?.Event == null) return $"{cameraName}.mp4";
        var timestamp = _selectedEvent.Event.TimeStamp.ToString("yyyy-MM-dd_HH-mm-ss");
        return $"{timestamp}_{cameraName}.mp4";
    }

    private async Task InitializeEventMap()
    {
        if (_selectedEvent?.Event == null) return;
        if (string.IsNullOrEmpty(_selectedEvent.Event.Lat) || string.IsNullOrEmpty(_selectedEvent.Event.Long)) return;

        try
        {
            var latParsed = double.TryParse(_selectedEvent.Event.Lat, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat)
                || double.TryParse(_selectedEvent.Event.Lat, System.Globalization.NumberStyles.Any,
                    new System.Globalization.CultureInfo("nl-NL"), out lat);

            var lonParsed = double.TryParse(_selectedEvent.Event.Long, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lon)
                || double.TryParse(_selectedEvent.Event.Long, System.Globalization.NumberStyles.Any,
                    new System.Globalization.CultureInfo("nl-NL"), out lon);

            if (latParsed && lonParsed)
            {
                await Task.Delay(300); // Wait for DOM to render
                await JS.InvokeVoidAsync("initEventMap", "event-map", lat, lon, 13);
                Logger.LogInformation("Initialized map for event {EventId} at {Lat}, {Lon}", 
                    _selectedEvent.Event.Id, lat, lon);
            }
            else
            {
                Logger.LogWarning("Invalid coordinates for event {EventId}: Lat={Lat}, Long={Long}",
                    _selectedEvent.Event.Id, _selectedEvent.Event.Lat, _selectedEvent.Event.Long);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize event map");
        }
    }

    // Timeline and video control methods
    private async Task TogglePlayPause()
    {
        isPlaying = !isPlaying;
        await PlayerInvokeVoid(isPlaying ? "play" : "pause");
        StateHasChanged();
    }

    private async Task OnTimelineChanged(double newTime)
    {
        currentTime = newTime;
        await PlayerInvokeVoid("seek", newTime);
    }

    private async Task SkipBackward()
    {
        var newTime = Math.Max(0, currentTime - 5);
        await OnTimelineChanged(newTime);
    }

    private async Task SkipForward()
    {
        var newTime = Math.Min(videoDuration, currentTime + 5);
        await OnTimelineChanged(newTime);
    }

    private async Task StartTimelineUpdater()
    {
        _jsRef ??= DotNetObjectReference.Create(this);
        await PlayerInvokeVoid("startTimeline", _jsRef, 250);
    }

    private async Task StopTimelineUpdater()
    {
        await PlayerInvokeVoid("stopTimeline");
    }

    [JSInvokable]
    public Task OnTimelineTick(double time)
    {
        if (!isPlaying || Math.Abs(time - currentTime) <= 0.5)
        {
            return Task.CompletedTask;
        }

        currentTime = time;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task PlayerInvokeVoid(string method, params object?[] args)
    {
        if (!RendererInfo.IsInteractive)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync($"teslaCamPlayer.{method}", args);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // Prerender / static render has no JS runtime.
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "teslaCamPlayer.{Method} failed", method);
        }
    }

    private int GetTimeMarkerCount()
    {
        return videoDuration switch
        {
            <= 30 => 6,
            <= 60 => 6,
            <= 120 => 8,
            _ => 10
        };
    }

    private string FormatTimeWithTimestamp(double seconds)
    {
        if (!minTimestamp.HasValue) return FormatTime(seconds);

        var absoluteTime = minTimestamp.Value.AddSeconds(seconds);
        return absoluteTime.ToString("HH:mm:ss");
    }

    private string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"mm\:ss");
    }

    private bool IsNotEventDate(DateTime date)
    {
        return !_eventDates.Any(d => d.Date == date.Date);
    }

    private string GetDateStyle(DateTime date)
    {
        if (!IsNotEventDate(date))
        {
            return "event_date";
        }
        return "";
    }

    private void ClearFilter(string filterName)
    {
        var uri = Navigation.ToAbsoluteUri(Navigation.Uri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        switch (filterName)
        {
            case nameof(filterCity):
                query.Remove("city");
                filterCity = null;
                break;
            case nameof(filterDate):
                query.Remove("date");
                filterDateString = null;
                break;
            case nameof(filterSource):
                query.Remove("source");
                filterSource = null;
                break;
        }

        var newUrl = $"/events?{query}";
        Navigation.NavigateTo(newUrl);
    }

    private async Task OnCityFilterChanged(string? city)
    {
        filterCity = city;
        await LoadAvailableFilters(); // Reload available dates and sources based on new city
        await UpdateFiltersAndNavigate();
    }

    private async Task OnDateFilterChanged(DateTime? date)
    {
        filterDateString = date?.ToString("yyyy-MM-dd");
        await LoadAvailableFilters(); // Reload available cities and sources based on new date
        await UpdateFiltersAndNavigate();
    }

    private async Task OnSourceFilterChanged(string? source)
    {
        filterSource = source;
        await LoadAvailableFilters(); // Reload available cities and dates based on new source
        await UpdateFiltersAndNavigate();
    }

    private async Task ClearAllFilters()
    {
        filterCity = null;
        filterDateString = null;
        filterSource = null;
        await LoadAvailableFilters(); // Reload all filters without restrictions
        Navigation.NavigateTo("/events");
    }

    private async Task UpdateFiltersAndNavigate()
    {
        var queryParams = new List<string>();
        
        if (!string.IsNullOrEmpty(filterCity))
            queryParams.Add($"city={Uri.EscapeDataString(filterCity)}");
        
        if (!string.IsNullOrEmpty(filterDateString))
            queryParams.Add($"date={filterDateString}");
        
        if (!string.IsNullOrEmpty(filterSource))
            queryParams.Add($"source={Uri.EscapeDataString(filterSource)}");

        var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
        Navigation.NavigateTo($"/events{queryString}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopTimelineUpdater();
        await PlayerInvokeVoid("dispose");

        _jsRef?.Dispose();
        _jsRef = null;
    }

    private async Task NavigateToPreviousEvent()
    {
        if (_selectedEvent == null || _filteredEvents.Count == 0) return;
        var currentIndex = _filteredEvents.FindIndex(e => e.Event.Id == _selectedEvent.Event.Id);
        if (currentIndex > 0)
        {
            await OnClipClicked(_filteredEvents[currentIndex - 1]);
        }
    }

    private async Task NavigateToNextEvent()
    {
        if (_selectedEvent == null || _filteredEvents.Count == 0) return;
        var currentIndex = _filteredEvents.FindIndex(e => e.Event.Id == _selectedEvent.Event.Id);
        if (currentIndex < _filteredEvents.Count - 1)
        {
            await OnClipClicked(_filteredEvents[currentIndex + 1]);
        }
    }

    private bool HasPreviousEvent()
    {
        if (_selectedEvent == null || _filteredEvents.Count == 0) return false;
        var currentIndex = _filteredEvents.FindIndex(e => e.Event.Id == _selectedEvent.Event.Id);
        return currentIndex > 0;
    }

    private bool HasNextEvent()
    {
        if (_selectedEvent == null || _filteredEvents.Count == 0) return false;
        var currentIndex = _filteredEvents.FindIndex(e => e.Event.Id == _selectedEvent.Event.Id);
        return currentIndex < _filteredEvents.Count - 1;
    }
}
