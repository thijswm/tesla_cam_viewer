using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;

namespace TeslaCamViewer.Shared
{
    public partial class MainLayout
    {
        [Inject] AppDbContext? Db { get; set; }
        private List<Event> _events = [];
        private List<ClipItem> _eventsSelected = [];

        protected override async Task OnInitializedAsync()
        {
            if (Db != null)
            {
                _events = await Db.Events.Include(a => a.Clips).ToListAsync();
            }
        }

        private string GetDateStyle(DateTime date)
        {
            if (!IsNotEventDate(date))
            {
                return "event_date";
            }
            else
            {
                return "";
            }
        }

        private bool IsNotEventDate(DateTime date)
        {
            return _events.Find(e => e.TimeStamp.Date == date.Date) == null;
        }

        private void OnDateSelected(DateTime? dateTime)
        {
            // Clear selected event first to stop videos immediately
            SelectedEvent = null;
            StateHasChanged(); // Force UI update to stop videos

            _eventsSelected.Clear();
            if (dateTime.HasValue)
            {
                _eventsSelected = _events.Where(a => a.TimeStamp.Date == dateTime.Value.Date)
                    .OrderBy(a => a.TimeStamp)
                    .Select(a => new ClipItem(a))
                    .ToList();
            }

            StateHasChanged(); // Update UI with new event list
        }
    }
}
