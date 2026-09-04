using TeslaCamViewer.Data;

namespace TeslaCamViewer.Shared
{
    public class ClipItem
    {
        public Event Event { get; set; }

        public ClipItem(Event ev)
        {
            Event = ev;
        }

        public string ThumbnailUrl => Event is null ? string.Empty : $"/api/thumbnail/{Event.Id}";
    }
}
