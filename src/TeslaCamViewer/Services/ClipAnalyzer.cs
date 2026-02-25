using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;
using FFMpegCore;
using FFMpegCore.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Linq;

namespace TeslaCamViewer.Services
{
    public class DetectionResult
    {
        public string Label { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    public class ClipAnalyzer
    {
        private readonly ILogger<ClipAnalyzer> _logger;
        private readonly MotionDetector _motionDetector;
        private readonly InferenceSession _inferenceSession;
        private readonly string _inputName;
        private readonly int _inputHeight;
        private readonly int _inputWidth;
        private readonly string[] _labels;

        public ClipAnalyzer(ILogger<ClipAnalyzer> logger, MotionDetector motionDetector, IConfiguration config)
        {
            _logger = logger;
            _motionDetector = motionDetector;

            var modelPath = config["TeslaCam:ModelsPath"] ?? "/app/models/model.onnx";

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            _inferenceSession = new InferenceSession(modelPath, opts);
            _inputName = _inferenceSession.InputMetadata.Keys.Single();

            // Get expected input dimensions from model metadata
            var inputMetadata = _inferenceSession.InputMetadata[_inputName];
            var shape = inputMetadata.Dimensions;
            
            // Assuming input shape is [batch, channels, height, width] or [batch, height, width, channels]
            _inputHeight = shape.Length >= 3 ? shape[^2] : 224;
            _inputWidth = shape.Length >= 2 ? shape[^1] : 224;

            // COCO labels for object detection (80 classes)
            _labels = new[]
            {
                "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
                "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
                "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
                "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
                "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
                "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
                "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
                "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
                "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator",
                "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
            };

            _logger.LogInformation("ONNX Model loaded: Input={InputName}, Shape={Shape}, Expected size: {Height}x{Width}",
                _inputName, string.Join("x", shape), _inputHeight, _inputWidth);
        }

        //public async Task AnalyzeAsync(string path)
        //{
        //    _logger.LogInformation("Analyzing clip: {Path}", path);

        //    var motionFrames = await _motionDetector.DetectMotionFrames(path);

        //    _logger.LogInformation("Processing {Count} motion frames through ONNX model", motionFrames.Count);

        //    foreach (var frameBytes in motionFrames)
        //    {
        //        try
        //        {
        //            var result = await InferFrame(frameBytes);
                    
        //            _logger.LogInformation("Inference result: {Result}", string.Join(", ", result.Select((v, i) => $"Class{i}={v:F4}")));
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Failed to run inference on frame");
        //        }
        //    }
        //}

        /// <summary>
        /// Detects objects in a single frame and returns bounding boxes
        /// </summary>
        public async Task<List<DetectionResult>> DetectObjectsInFrame(byte[] frameBytes, float confidenceThreshold = 0.5f)
        {
            var detections = new List<DetectionResult>();

            try
            {
                // Mock detections if no model loaded
                if (_inferenceSession == null)
                {
                    _logger.LogDebug("Using mock detections (no model loaded)");
                    return new List<DetectionResult>
                    {
                        new DetectionResult
                        {
                            Label = "car",
                            Confidence = 0.92f,
                            X = 100,
                            Y = 150,
                            Width = 200,
                            Height = 150
                        },
                        new DetectionResult
                        {
                            Label = "person",
                            Confidence = 0.85f,
                            X = 400,
                            Y = 200,
                            Width = 80,
                            Height = 180
                        }
                    };
                }

                using var image = await Image.LoadAsync<Rgb24>(new MemoryStream(frameBytes));
                var originalWidth = image.Width;
                var originalHeight = image.Height;

                // Resize for model
                image.Mutate(x => x.Resize(_inputWidth, _inputHeight));

                var tensor = ImageToTensor(image);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = _inferenceSession.Run(inputs);
                var output = results.First().AsTensor<float>();
                var dims = output.Dimensions;

                if (dims.Length == 3)
                {
                    var attrIndex = dims[1] == (4 + _labels.Length) ? 1 : 2;
                    var boxIndex = attrIndex == 1 ? 2 : 1;
                    var attributes = dims[attrIndex];
                    var boxes = dims[boxIndex];

                    if (attributes < 4)
                    {
                        _logger.LogWarning("Unexpected YOLO output shape: {Shape}", string.Join("x", dims.ToArray()));
                        return detections;
                    }

                    for (int i = 0; i < boxes; i++)
                    {
                        float x;
                        float y;
                        float w;
                        float h;
                        if (attrIndex == 1)
                        {
                            x = output[0, 0, i];
                            y = output[0, 1, i];
                            w = output[0, 2, i];
                            h = output[0, 3, i];
                        }
                        else
                        {
                            x = output[0, i, 0];
                            y = output[0, i, 1];
                            w = output[0, i, 2];
                            h = output[0, i, 3];
                        }

                        var bestClass = -1;
                        var bestScore = 0f;
                        for (int c = 4; c < attributes; c++)
                        {
                            var score = attrIndex == 1 ? output[0, c, i] : output[0, i, c];
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestClass = c - 4;
                            }
                        }

                        if (bestScore < confidenceThreshold || bestClass < 0)
                        {
                            continue;
                        }

                        var normalized = x <= 1f && y <= 1f && w <= 1f && h <= 1f;
                        if (normalized)
                        {
                            x *= _inputWidth;
                            y *= _inputHeight;
                            w *= _inputWidth;
                            h *= _inputHeight;
                        }

                        var left = x - (w / 2f);
                        var top = y - (h / 2f);

                        var scaleX = originalWidth / (float)_inputWidth;
                        var scaleY = originalHeight / (float)_inputHeight;

                        detections.Add(new DetectionResult
                        {
                            Label = bestClass < _labels.Length ? _labels[bestClass] : $"Class_{bestClass}",
                            Confidence = bestScore,
                            X = left * scaleX,
                            Y = top * scaleY,
                            Width = w * scaleX,
                            Height = h * scaleY
                        });
                    }
                }
                else
                {
                    _logger.LogWarning("Unsupported ONNX output shape: {Shape}", string.Join("x", dims.ToArray()));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect objects in frame");
            }

            return detections;
        }

        /// <summary>
        /// Draws bounding boxes on an image
        /// </summary>
        public async Task<byte[]> DrawDetectionsOnFrame(byte[] frameBytes, List<DetectionResult> detections, bool outputJpeg = false)
        {
            using var image = await Image.LoadAsync<Rgb24>(new MemoryStream(frameBytes));

            // Try to load system font, fallback to sans-serif
            Font? font = null;
            try
            {
                var fontFamily = SystemFonts.Get("Arial");
                font = fontFamily.CreateFont(16, FontStyle.Bold);
            }
            catch
            {
                // Fallback if Arial not found
                try
                {
                    var fontFamily = SystemFonts.Families.First();
                    font = fontFamily.CreateFont(16, FontStyle.Bold);
                }
                catch
                {
                    _logger.LogWarning("No system fonts available, skipping text labels");
                }
            }

            image.Mutate(ctx =>
            {
                foreach (var detection in detections)
                {
                    // Draw red rectangle
                    var rect = new RectangleF(detection.X, detection.Y, detection.Width, detection.Height);
                    ctx.Draw(Color.Red, 3f, rect);
                    
                    // Draw label with background if font is available
                    if (font != null)
                    {
                        var label = $"{detection.Label} {detection.Confidence:P0}";
                        var textLocation = new PointF(detection.X, Math.Max(0, detection.Y - 20));
                        
                        // Draw text background
                        var textSize = TextMeasurer.MeasureSize(label, new TextOptions(font));
                        var textBackground = new RectangleF(textLocation.X, textLocation.Y, textSize.Width + 4, textSize.Height + 2);
                        ctx.Fill(Color.Red, textBackground);
                        
                        // Draw text
                        ctx.DrawText(label, font, Color.White, textLocation);
                    }
                }
            });

            using var ms = new MemoryStream();
            if (outputJpeg)
            {
                await image.SaveAsJpegAsync(ms);
            }
            else
            {
                await image.SaveAsPngAsync(ms);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Processes a video stream and returns annotated video with object detection
        /// </summary>
        public async Task<string> ProcessVideoWithDetection(Stream videoStream, string outputPath, CancellationToken ct = default)
        {
            var tempInputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp4");
            var tempFramesDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFramesDir);

            try
            {
                // Save stream to temp file
                _logger.LogInformation("Saving video stream to temp file: {Path}", tempInputPath);
                using (var fileStream = File.Create(tempInputPath))
                {
                    await videoStream.CopyToAsync(fileStream, ct);
                }

                // Get video info
                var mediaInfo = await FFProbe.AnalyseAsync(tempInputPath, cancellationToken: ct);
                var fps = mediaInfo.PrimaryVideoStream?.FrameRate ?? 30;

                _logger.LogInformation("Processing video: FPS={Fps}, Duration={Duration}", fps, mediaInfo.Duration);

                // Extract all frames
                _logger.LogInformation("Extracting frames...");
                var framePattern = Path.Combine(tempFramesDir, "frame_%05d.png");
                await FFMpegArguments
                    .FromFileInput(tempInputPath)
                    .OutputToFile(framePattern, true, options => options
                        .WithVideoCodec("png"))
                    .ProcessAsynchronously();

                var frameFiles = Directory.GetFiles(tempFramesDir, "frame_*.png")
                    .OrderBy(f => f)
                    .ToList();

                _logger.LogInformation("Extracted {Count} frames, processing with object detection...", frameFiles.Count);

                // Process each frame
                int frameCount = 0;
                foreach (var framePath in frameFiles)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var frameBytes = await File.ReadAllBytesAsync(framePath, ct);
                    
                    // Detect objects
                    var detections = await DetectObjectsInFrame(frameBytes);

                    if (detections.Any())
                    {
                        _logger.LogInformation("Frame {Frame}: Detected {Count} objects: {Objects}",
                            frameCount,
                            detections.Count,
                            string.Join(", ", detections.Select(d => $"{d.Label}({d.Confidence:P0})")));

                        // Draw boxes on frame
                        var annotatedFrame = await DrawDetectionsOnFrame(frameBytes, detections);
                        await File.WriteAllBytesAsync(framePath, annotatedFrame, ct);
                    }

                    frameCount++;
                    
                    if (frameCount % 30 == 0)
                    {
                        _logger.LogInformation("Processed {Current}/{Total} frames...", frameCount, frameFiles.Count);
                    }
                }

                // Re-encode frames to video
                _logger.LogInformation("Re-encoding annotated frames to video...");
                await FFMpegArguments
                    .FromFileInput(framePattern, false, options => options
                        .WithFramerate(fps))
                    .OutputToFile(outputPath, true, options => options
                        .WithVideoCodec("libx264")
                        .WithConstantRateFactor(23)
                        .WithAudioCodec("copy"))
                    .ProcessAsynchronously();

                _logger.LogInformation("Video processing complete: {OutputPath}", outputPath);
                return outputPath;
            }
            finally
            {
                // Cleanup
                try
                {
                    if (File.Exists(tempInputPath))
                        File.Delete(tempInputPath);
                    if (Directory.Exists(tempFramesDir))
                        Directory.Delete(tempFramesDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temp files");
                }
            }
        }

        private async Task<float[]> InferFrame(byte[] frameBytes)
        {
            using var image = await Image.LoadAsync<Rgb24>(new MemoryStream(frameBytes));
            image.Mutate(x => x.Resize(_inputWidth, _inputHeight));

            var tensor = ImageToTensor(image);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, tensor)
            };

            using var results = _inferenceSession.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();

            return output;
        }

        private DenseTensor<float> ImageToTensor(Image<Rgb24> image)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });

            for (int y = 0; y < _inputHeight; y++)
            {
                for (int x = 0; x < _inputWidth; x++)
                {
                    var pixel = image[x, y];

                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }

            return tensor;
        }

        public async Task StreamDetectionsAsMjpeg(string videoPath, Stream outputStream, CancellationToken ct = default)
        {
            const string boundary = "frame";

            try
            {
                await FFMpegArguments
                    .FromFileInput(videoPath)
                    .OutputToPipe(new StreamPipeSink(async (ffmpegOutput, pipeCt) =>
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, pipeCt);
                        var linkedCt = linkedCts.Token;

                        await foreach (var frame in ReadJpegFramesAsync(ffmpegOutput, linkedCt))
                        {
                            if (linkedCt.IsCancellationRequested)
                            {
                                break;
                            }

                            var detections = await DetectObjectsInFrame(frame);
                            var annotated = await DrawDetectionsOnFrame(frame, detections, outputJpeg: true);
                            await WriteMjpegFrameAsync(outputStream, annotated, boundary, linkedCt);
                        }
                    }), options => options
                        .ForceFormat("image2pipe")
                        .WithCustomArgument("-vcodec mjpeg")
                        .WithCustomArgument("-vf fps=10"))
                    .ProcessAsynchronously();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        private static async IAsyncEnumerable<byte[]> ReadJpegFramesAsync(Stream input, [EnumeratorCancellation] CancellationToken ct)
        {
            var buffer = new List<byte>(1024 * 1024);
            var readBuffer = new byte[16 * 1024];

            while (true)
            {
                var bytesRead = await input.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct);
                if (bytesRead <= 0)
                {
                    yield break;
                }

                for (var i = 0; i < bytesRead; i++)
                {
                    buffer.Add(readBuffer[i]);
                }

                while (true)
                {
                    var startIndex = FindJpegMarker(buffer, 0xD8, 0);
                    if (startIndex < 0)
                    {
                        if (buffer.Count > 2)
                        {
                            buffer.RemoveRange(0, buffer.Count - 2);
                        }
                        break;
                    }

                    var endIndex = FindJpegMarker(buffer, 0xD9, startIndex + 2);
                    if (endIndex < 0)
                    {
                        if (startIndex > 0)
                        {
                            buffer.RemoveRange(0, startIndex);
                        }
                        break;
                    }

                    var length = endIndex - startIndex + 2;
                    var frame = buffer.GetRange(startIndex, length).ToArray();
                    buffer.RemoveRange(0, endIndex + 2);
                    yield return frame;
                }
            }
        }

        private static int FindJpegMarker(List<byte> buffer, byte marker, int startIndex)
        {
            for (var i = startIndex; i < buffer.Count - 1; i++)
            {
                if (buffer[i] == 0xFF && buffer[i + 1] == marker)
                {
                    return i;
                }
            }

            return -1;
        }

        private static async Task WriteMjpegFrameAsync(Stream outputStream, byte[] frameBytes, string boundary, CancellationToken ct)
        {
            try
            {
                var header = $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frameBytes.Length}\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);

                await outputStream.WriteAsync(headerBytes, ct);
                await outputStream.WriteAsync(frameBytes, ct);
                await outputStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
                await outputStream.FlushAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }
}
