using FFMpegCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TeslaCamViewer.Services;

public class MotionDetector
{
    private readonly ILogger<MotionDetector> _logger;

    public MotionDetector(ILogger<MotionDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyzes a video file and extracts frames where motion is detected
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="motionThreshold">Motion sensitivity (0.0-1.0). Lower = more sensitive. Default: 0.05</param>
    /// <param name="frameInterval">Check every N frames for performance. Default: 5</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of byte arrays containing PNG images where motion was detected</returns>
    public async Task<List<byte[]>> DetectMotionFrames(
        string videoPath, 
        double motionThreshold = 0.05, 
        int frameInterval = 5,
        CancellationToken ct = default)
    {
        var motionFrames = new List<byte[]>();

        try
        {
            _logger.LogInformation("Starting motion detection for video: {VideoPath}", videoPath);

            // Get video info
            var mediaInfo = await FFProbe.AnalyseAsync(videoPath, cancellationToken: ct);
            var fps = mediaInfo.PrimaryVideoStream?.FrameRate ?? 30;
            var duration = mediaInfo.Duration;

            _logger.LogInformation("Video info: FPS={Fps}, Duration={Duration}", fps, duration);

            // Extract frames at intervals
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Extract frames using FFmpeg - output pattern: frame_0001.png, frame_0002.png, etc.
                var framePattern = Path.Combine(tempDir, "frame_%04d.png");
                
                // Use FFMpeg to extract frames at specified interval
                // -vf select extracts every Nth frame based on frameInterval
                await FFMpegArguments
                    .FromFileInput(videoPath)
                    .OutputToFile(framePattern, true, options => options
                        .WithCustomArgument($"-vf select='not(mod(n\\,{frameInterval}))'")
                        .WithCustomArgument("-vsync vfr"))
                    .ProcessAsynchronously();

                // Analyze frames for motion
                var frameFiles = Directory.GetFiles(tempDir, "frame_*.png")
                    .OrderBy(f => f)
                    .ToList();

                _logger.LogInformation("Extracted {Count} frames for analysis", frameFiles.Count);

                if (frameFiles.Count == 0)
                {
                    _logger.LogWarning("No frames extracted from video: {VideoPath}", videoPath);
                    return motionFrames;
                }

                Image<Rgba32>? previousFrame = null;

                for (int i = 0; i < frameFiles.Count; i++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var currentFramePath = frameFiles[i];
                    using var currentFrame = await Image.LoadAsync<Rgba32>(currentFramePath, ct);

                    if (previousFrame != null)
                    {
                        // Compare current frame with previous frame
                        var motionScore = CalculateMotionScore(previousFrame, currentFrame);

                        if (motionScore > motionThreshold)
                        {
                            // Motion detected - save this frame
                            var frameBytes = await File.ReadAllBytesAsync(currentFramePath, ct);
                            motionFrames.Add(frameBytes);

                            _logger.LogInformation("Motion detected at frame {Index}/{Total}, score: {Score:F4}", 
                                i, frameFiles.Count, motionScore);
                        }
                    }

                    // Update previous frame for next comparison
                    previousFrame?.Dispose();
                    previousFrame = currentFrame.Clone();
                }

                previousFrame?.Dispose();

                _logger.LogInformation("Motion detection completed. Found {Count} frames with motion out of {Total} analyzed", 
                    motionFrames.Count, frameFiles.Count);
            }
            finally
            {
                // Clean up temp directory
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp directory: {TempDir}", tempDir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during motion detection for video: {VideoPath}", videoPath);
            throw;
        }

        return motionFrames;
    }

    /// <summary>
    /// Calculates a motion score between two frames (0.0 = no change, 1.0 = complete change)
    /// </summary>
    private double CalculateMotionScore(Image<Rgba32> frame1, Image<Rgba32> frame2)
    {
        if (frame1.Width != frame2.Width || frame1.Height != frame2.Height)
        {
            throw new ArgumentException("Frames must have the same dimensions");
        }

        long totalDifference = 0;

        // Compare pixels in a grid pattern for performance (every 4th pixel)
        int step = 4;
        long comparedPixels = 0;

        for (int y = 0; y < frame1.Height; y += step)
        {
            for (int x = 0; x < frame1.Width; x += step)
            {
                var pixel1 = frame1[x, y];
                var pixel2 = frame2[x, y];

                // Calculate grayscale difference
                var gray1 = (pixel1.R + pixel1.G + pixel1.B) / 3;
                var gray2 = (pixel2.R + pixel2.G + pixel2.B) / 3;

                totalDifference += Math.Abs(gray1 - gray2);
                comparedPixels++;
            }
        }

        // Normalize to 0.0-1.0 range
        // Maximum possible difference per pixel is 255
        var motionScore = (double)totalDifference / (comparedPixels * 255.0);

        return motionScore;
    }

    /// <summary>
    /// Analyzes multiple camera angles for the same event and returns motion frames
    /// </summary>
    public async Task<Dictionary<string, List<byte[]>>> DetectMotionInEvent(
        List<string> cameraVideos,
        double motionThreshold = 0.05,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, List<byte[]>>();

        foreach (var videoPath in cameraVideos)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var cameraName = Path.GetFileNameWithoutExtension(videoPath);
                var frames = await DetectMotionFrames(videoPath, motionThreshold, frameInterval: 5, ct);
                results[cameraName] = frames;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process video: {VideoPath}", videoPath);
            }
        }

        return results;
    }
}
