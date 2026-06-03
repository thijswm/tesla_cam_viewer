using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeslaCamViewer.Data;

namespace TeslaCamViewer.Pages;

public partial class Calendar
{
    [Inject] public AppDbContext Db { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public ILogger<Calendar>? Logger { get; set; }

    private readonly string[] _dayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private DateTime _currentMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private List<CalendarCell> _calendarCells = [];

    private double _touchStartX;

    protected override async Task OnInitializedAsync()
    {
        await LoadMonthAsync();
    }

    private async Task LoadMonthAsync()
    {
        var start = DateTime.SpecifyKind(_currentMonth, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(_currentMonth.AddMonths(1), DateTimeKind.Utc);

        var events = await Db.Events
            .Where(e => e.TimeStamp >= start && e.TimeStamp < end)
            .OrderBy(e => e.TimeStamp)
            .ToListAsync();

        var daysInMonth = DateTime.DaysInMonth(start.Year, start.Month);
        var firstDayOffset = (int)start.DayOfWeek;

        var cells = new List<CalendarCell>();

        for (var i = 0; i < firstDayOffset; i++)
        {
            cells.Add(CalendarCell.Empty());
        }

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(start.Year, start.Month, day, 0, 0, 0, DateTimeKind.Utc);
            var dayEvents = events.Where(e => e.TimeStamp.Date == date.Date).ToList();
            var thumbnail = dayEvents.FirstOrDefault()?.Thumbnail;

            cells.Add(new CalendarCell
            {
                Date = date,
                EventCount = dayEvents.Count,
                Thumbnail = ConvertThumbnail(thumbnail)
            });
        }

        _calendarCells = cells;
    }

    private async Task PreviousMonth()
    {
        _currentMonth = _currentMonth.AddMonths(-1);
        await LoadMonthAsync();
    }

    private async Task NextMonth()
    {
        _currentMonth = _currentMonth.AddMonths(1);
        await LoadMonthAsync();
    }

    private static string? ConvertThumbnail(byte[]? thumbnail)
    {
        if (thumbnail == null || thumbnail.Length == 0)
        {
            return null;
        }

        var base64 = Convert.ToBase64String(thumbnail);
        return $"data:image/png;base64,{base64}";
    }

    private void OnDateClicked(DateTime? date)
    {
        if (!date.HasValue) return;

        var url = $"/events?date={date.Value:yyyy-MM-dd}";
        Logger?.LogInformation("Calendar date clicked: {Date}, navigating to {Url}", date, url);
        Navigation.NavigateTo(url);
    }

    private void OnCellClicked(CalendarCell cell)
    {
        if (cell.Date.HasValue && cell.EventCount > 0)
        {
            OnDateClicked(cell.Date);
        }
    }

    private void OnTouchStart(TouchEventArgs e)
    {
        if (e.Touches.Length > 0)
        {
            _touchStartX = e.Touches[0].ClientX;
        }
    }

    private async Task OnTouchEnd(TouchEventArgs e)
    {
        if (e.ChangedTouches.Length > 0)
        {
            var deltaX = e.ChangedTouches[0].ClientX - _touchStartX;
            if (Math.Abs(deltaX) > 100) // Use larger threshold to avoid interfering with taps
            {
                if (deltaX > 0)
                    await PreviousMonth();
                else
                    await NextMonth();

                StateHasChanged();
            }
        }
    }

    private sealed class CalendarCell
    {
        public DateTime? Date { get; init; }
        public int EventCount { get; init; }
        public string? Thumbnail { get; init; }

        public static CalendarCell Empty() => new();
    }
}
