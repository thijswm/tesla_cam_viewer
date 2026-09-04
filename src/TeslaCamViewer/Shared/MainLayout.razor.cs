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
            if (DbFactory == null)
            {
                return;
            }

            await using var db = await DbFactory.CreateDbContextAsync();
            var firstDayOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            _totalEvents = await db.Events.CountAsync();
            _eventsThisMonth = await db.Events.CountAsync(e => e.TimeStamp >= firstDayOfMonth);
            _sentryEvents = await db.Events.CountAsync(e => e.Source == "Sentry");
            _savedEvents = await db.Events.CountAsync(e => e.Source == "Saved");
        }
    }
}
