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

        public string? Thumbnail
        {
            get
            {
                if (Event?.Thumbnail != null)
                {
                    var base64 = Convert.ToBase64String(Event.Thumbnail);
                    return $"data:image/png;base64,{base64}";
                }
                else
                {
                    return null;
                }
            }
        }
    }
}
