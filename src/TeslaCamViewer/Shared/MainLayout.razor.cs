using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;

namespace TeslaCamViewer.Shared
{
    public partial class MainLayout
    {
        [Inject] IDbContextFactory<AppDbContext>? DbFactory { get; set; }

        private int _totalEvents = 0;
        private int _eventsThisMonth = 0;
        private int _sentryEvents = 0;
        private int _savedEvents = 0;

        protected override async Task OnInitializedAsync()
        {
            if (DbFactory != null)
            {
                await using var db = await DbFactory.CreateDbContextAsync();
                var events = await db.Events.ToListAsync();
                CalculateStatistics(events);
            }
        }

        private void CalculateStatistics(List<Event> events)
        {
            _totalEvents = events.Count;
            
            var now = DateTime.UtcNow;
            var firstDayOfMonth = new DateTime(now.Year, now.Month, 1);
            _eventsThisMonth = events.Count(e => e.TimeStamp >= firstDayOfMonth);
            
            _sentryEvents = events.Count(e => e.Source == "Sentry");
            _savedEvents = events.Count(e => e.Source == "Saved");
        }
    }
}
