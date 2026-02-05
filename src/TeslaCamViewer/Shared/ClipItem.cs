using System.Buffers.Text;
using TeslaCamViewer.Data;

namespace TeslaCamViewer.Shared
{
    public class ClipItem
    {
        public Event Event { get; set; }
        public List<Clip> Clips { get; set; }

        public string? Thumbnail
        {
            get
            {
                if (Event.Thumbnail != null)
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
