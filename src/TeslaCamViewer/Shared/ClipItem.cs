using TeslaCamViewer.Data;
using FFMpegCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

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

        /// <summary>
        /// Extracts the first frame from each clip's MP4 file and returns a list of base64 PNG data URLs.
        /// Returns null for clips that fail to extract.
        /// </summary>
        public async Task<List<KeyValuePair<Clip, string?>>> GetThumbnailsForAllClipsAsync(ILogger? logger = null)
        {
            var thumbnails = new List<KeyValuePair<Clip, string?>>();

            //logger?.LogInformation("Starting thumbnail extraction for {ClipCount} clips in event {EventFolder}",
            //    Clips?.Count ?? 0, Event?.FolderName);

            //foreach (var clip in Clips ?? new List<Clip>())
            //{
            //    var thumb = await ExtractThumbnailFromMp4Async(clip.Path, logger);
            //    thumbnails.Add(new KeyValuePair<Clip, string?>(clip, thumb));
            //}

            //logger?.LogInformation("Completed thumbnail extraction: {SuccessCount}/{TotalCount} successful",
            //    thumbnails.Count(t => t.Value != null), thumbnails.Count);

            return thumbnails;
        }

        private async Task<string?> ExtractThumbnailFromMp4Async(string mp4Path, ILogger? logger = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mp4Path) || !File.Exists(mp4Path))
                {
                    logger?.LogWarning("Thumbnail extraction skipped: path invalid or file not found: {Path}", mp4Path);
                    return null;
                }

                logger?.LogDebug("Extracting thumbnail from {Path} using FFMpeg", mp4Path);

                // Create a temporary file for the extracted frame
                var tempPng = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

                try
                {
                    // Extract first frame at higher resolution for better quality
                    var success = await FFMpeg.SnapshotAsync(
                        mp4Path,
                        tempPng,
                        new System.Drawing.Size(960, 540), // Higher resolution extraction
                        TimeSpan.FromMilliseconds(100)
                    );

                    if (!success || !File.Exists(tempPng))
                    {
                        logger?.LogWarning("FFMpeg snapshot failed for {Path}", mp4Path);
                        return null;
                    }

                    // Read the PNG, resize to thumbnail with better quality settings
                    using var image = await Image.LoadAsync(tempPng);
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(480, 270), // Larger thumbnail size for better quality
                        Mode = ResizeMode.Max, // Preserve aspect ratio without cropping
                        Sampler = KnownResamplers.Lanczos3 // High quality resampling
                    }));

                    using var ms = new MemoryStream();
                    // Use higher quality PNG encoding
                    var encoder = new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.BestCompression,
                        FilterMethod = PngFilterMethod.Adaptive
                    };
                    await image.SaveAsync(ms, encoder);
                    var bytes = ms.ToArray();
                    var base64 = Convert.ToBase64String(bytes);

                    logger?.LogDebug("Successfully extracted thumbnail from {Path}, size: {Size} bytes", mp4Path, bytes.Length);

                    return $"data:image/png;base64,{base64}";
                }
                finally
                {
                    if (File.Exists(tempPng))
                        File.Delete(tempPng);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to extract thumbnail from {Path}", mp4Path);
                return null;
            }
        }
    }
}
