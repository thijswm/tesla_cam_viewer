using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using TeslaCamViewer.Data;

namespace TeslaCamViewer.Shared
{
    public partial class MainLayout
    {
        [Inject] AppDbContext Db { get; set; }
        private List<Event> _events = [];
        private List<ClipItem> _clips = [];

        protected override async Task OnInitializedAsync()
        {
            _events = await Db.Events.Include(a => a.Clips).ToListAsync();
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
            _clips.Clear();
            if (dateTime.HasValue)
            {
                var events = _events.Where(a => a.TimeStamp.Date == dateTime.Value.Date).OrderBy(a => a.TimeStamp);
                foreach (var evt in events)
                {
                    var groupedClips = evt.Clips.GroupBy(a => a.Timestamp);
                    foreach (var group in groupedClips)
                    {
                        _clips.Add(new ClipItem { Event = evt, Clips = group.ToList() });
                    }
                }
            }
        }
    }
}
