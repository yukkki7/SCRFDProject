using System;
using System.CommandLine;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCRDFInference;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("SCRFD Face Detection Inference Tool");

        var imageOption = new Option<FileInfo>(
            name: "--image",
            description: "Path to the input image file"
        )
        { IsRequired = true };

        var modelOption = new Option<FileInfo?>(
            name: "--model",
            description: "Path to the SCRFD ONNX model file (optional if using --download-model)"
        );

        var downloadModelOption = new Option<string?>(
            name: "--download-model",
            description: "Download a SCRFD model (scrfd_500m, scrfd_1g, scrfd_2.5g, scrfd_10g)"
        );

        var listModelsOption = new Option<bool>(
            name: "--list-models",
            description: "List available SCRFD models"
        );

        var outputOption = new Option<FileInfo?>(
            name: "--output",
            description: "Path to save detection results (optional, defaults to console)"
        );

        var confidenceOption = new Option<float>(
            name: "--confidence",
            description: "Confidence threshold for face detection (0.0-1.0)",
            getDefaultValue: () => 0.5f
        );

        var visualizeOption = new Option<FileInfo?>(
            name: "--visualize",
            description: "Save output image with highest‐confidence box drawn (e.g. output.jpg)"
        );

        // wire up options
        rootCommand.AddOption(imageOption);
        rootCommand.AddOption(modelOption);
        rootCommand.AddOption(downloadModelOption);
        rootCommand.AddOption(listModelsOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(confidenceOption);
        rootCommand.AddOption(visualizeOption);

        rootCommand.SetHandler(async (
            FileInfo image,
            FileInfo? model,
            string? downloadModel,
            bool listModels,
            FileInfo? output,
            float confidence,
            FileInfo? visualize
        ) =>
        {
            if (listModels)
            {
                ModelDownloader.ListAvailableModels();
                return;
            }

            if (!string.IsNullOrEmpty(downloadModel))
            {
                try
                {
                    var downloaded = await ModelDownloader.DownloadModel(downloadModel);
                    Console.WriteLine($"Model downloaded to: {downloaded}");
                    model = new FileInfo(downloaded);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error downloading model: {ex.Message}");
                    return;
                }
            }

            if (model == null)
            {
                Console.WriteLine("Please specify --model or use --download-model");
                return;
            }

            await RunSCRFDInference(image, model, output, confidence, visualize);
        },
        imageOption, modelOption, downloadModelOption,
        listModelsOption, outputOption, confidenceOption, visualizeOption);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunSCRFDInference(
        FileInfo imageFile,
        FileInfo modelFile,
        FileInfo? outputFile,
        float confidenceThreshold,
        FileInfo? visualizeFile)
    {
        var telemetry = new FaceDetectionTelemetry();
        var sw = Stopwatch.StartNew();

        try
        {
            Console.WriteLine("Starting SCRFD face detection...");
            Console.WriteLine($" Image:      {imageFile.FullName}");
            Console.WriteLine($" Model:      {modelFile.FullName}");
            Console.WriteLine($" Confidence: {confidenceThreshold:F2}\n");

            telemetry.StartTime = DateTime.UtcNow;
            telemetry.ImagePath = imageFile.FullName;
            telemetry.ModelPath = modelFile.FullName;
            telemetry.ConfidenceThreshold = confidenceThreshold;

            using var scrfd = new SCRFDModel(modelFile.FullName);
            var result = await scrfd.DetectFaces(imageFile.FullName, confidenceThreshold);

            sw.Stop();
            telemetry.TotalTimeMs = sw.ElapsedMilliseconds;
            telemetry.InferenceTimeMs = result.InferenceTimeMs;
            telemetry.EndTime = DateTime.UtcNow;
            telemetry.Success = true;
            telemetry.DetectedFaces = result.Detections.Count;
            telemetry.ImageSize = result.ImageSize;
            telemetry.Detections = result.Detections;

            await OutputDetectionResults(telemetry, outputFile);

            if (visualizeFile != null && telemetry.Success && telemetry.Detections.Count > 0)
            {
                // pick by score × area
                var best = telemetry.Detections
                    .OrderByDescending(d => d.Confidence * (d.BoundingBox.Width * d.BoundingBox.Height))
                    .First();

                using var bmp = (Bitmap)Image.FromFile(imageFile.FullName);
                using var g = Graphics.FromImage(bmp);
                using var pen = new Pen(Color.Red, 3);
                using var font = new Font("Arial", 14f, FontStyle.Bold);
                using var brush = new SolidBrush(Color.Yellow);

                var b = best.BoundingBox;
                var rect = new RectangleF(b.X, b.Y, b.Width, b.Height);
                g.DrawRectangle(pen, Rectangle.Round(rect));

                string label = best.Confidence.ToString("F2");
                var sz = g.MeasureString(label, font);
                float y = b.Y - sz.Height < 0 ? b.Y + 1 : b.Y - sz.Height;
                g.DrawString(label, font, brush, b.X, y);

                Directory.CreateDirectory(Path.GetDirectoryName(visualizeFile.FullName)!);
                bmp.Save(visualizeFile.FullName, ImageFormat.Jpeg);
                Console.WriteLine($"Visualization saved to: {visualizeFile.FullName}");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            telemetry.TotalTimeMs = sw.ElapsedMilliseconds;
            telemetry.EndTime = DateTime.UtcNow;
            telemetry.Success = false;
            telemetry.ErrorMessage = ex.Message;

            Console.WriteLine($"Error: {ex.Message}");
            await OutputDetectionResults(telemetry, outputFile);
        }
    }

    static async Task OutputDetectionResults(
        FaceDetectionTelemetry telemetry,
        FileInfo? outputFile)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            telemetry,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        if (outputFile != null)
        {
            await File.WriteAllTextAsync(outputFile.FullName, json);
            Console.WriteLine($"\nResults saved to: {outputFile.FullName}");
        }
        else
        {
            Console.WriteLine("\n=== RESULTS ===");
            Console.WriteLine(json);
        }

        Console.WriteLine("\n=== SUMMARY ===");
        Console.WriteLine($" Status     : {(telemetry.Success ? "SUCCESS" : "FAILED")}");
        Console.WriteLine($" Faces      : {telemetry.DetectedFaces}");
        Console.WriteLine($" Total time : {telemetry.TotalTimeMs}ms");
        Console.WriteLine($" Inference  : {telemetry.InferenceTimeMs}ms");
        Console.WriteLine($" Image size : {telemetry.ImageSize.Width}×{telemetry.ImageSize.Height}");

        if (telemetry.Success && telemetry.Detections.Count > 0)
        {
            Console.WriteLine("\nDetected faces:");
            for (int i = 0; i < telemetry.Detections.Count; i++)
            {
                var d = telemetry.Detections[i];
                var bb = d.BoundingBox;
                Console.WriteLine(
                  $"  [{i + 1}] Conf={d.Confidence:F3}, " +
                  $"Box=({bb.X:F0},{bb.Y:F0},{bb.Width:F0},{bb.Height:F0}), " +
                  $"Kps={d.Keypoints.Count}"
                );
            }
        }

        if (!telemetry.Success && !string.IsNullOrEmpty(telemetry.ErrorMessage))
        {
            Console.WriteLine($" Error: {telemetry.ErrorMessage}");
        }
    }
}
