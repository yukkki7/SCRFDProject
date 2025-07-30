using System.CommandLine;
using System.Diagnostics;

namespace SCRDFInference;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("SCRFD Face Detection Inference Tool");
        
        var imageOption = new Option<FileInfo>(
            name: "--image",
            description: "Path to the input image file");
        
        var modelOption = new Option<FileInfo?>(
            name: "--model",
            description: "Path to the SCRFD ONNX model file (optional if using --download-model)");
        
        var downloadModelOption = new Option<string?>(
            name: "--download-model",
            description: "Download a SCRFD model (scrfd_500m, scrfd_1g, scrfd_2.5g, scrfd_10g)");
        
        var listModelsOption = new Option<bool>(
            name: "--list-models",
            description: "List available SCRFD models");
        
        var outputOption = new Option<FileInfo?>(
            name: "--output",
            description: "Path to save detection results (optional, defaults to console)");
        
        var confidenceOption = new Option<float>(
            name: "--confidence",
            description: "Confidence threshold for face detection (0.0-1.0)",
            getDefaultValue: () => 0.5f);
        
        var visualizeOption = new Option<FileInfo?>(
            name: "--visualize",
            description: "Save output image with bounding boxes drawn (e.g., output.jpg)");
        
        rootCommand.AddOption(imageOption);
        rootCommand.AddOption(modelOption);
        rootCommand.AddOption(downloadModelOption);
        rootCommand.AddOption(listModelsOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(confidenceOption);
        rootCommand.AddOption(visualizeOption);
        
        rootCommand.SetHandler(async (image, model, downloadModel, listModels, output, confidence, visualize) =>
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
                    var modelPath = await ModelDownloader.DownloadModel(downloadModel);
                    Console.WriteLine($"Model ready at: {modelPath}");
                    
                    if (image != null)
                    {
                        await RunSCRFDInference(image, new FileInfo(modelPath), output, confidence, visualize);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error downloading model: {ex.Message}");
                }
                return;
            }
            
            if (image == null)
            {
                Console.WriteLine("Please specify an image file with --image");
                return;
            }
            
            if (model == null)
            {
                Console.WriteLine("Please specify a model file with --model or download one with --download-model");
                return;
            }
            
            await RunSCRFDInference(image, model, output, confidence, visualize);
            
        }, imageOption, modelOption, downloadModelOption, listModelsOption, outputOption, confidenceOption, visualizeOption);
        
        return await rootCommand.InvokeAsync(args);
    }
    
    static async Task RunSCRFDInference(FileInfo imageFile, FileInfo modelFile, FileInfo? outputFile, float confidence, FileInfo? visualizeFile)
    {
        var telemetry = new FaceDetectionTelemetry();
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            Console.WriteLine($"Starting SCRFD face detection...");
            Console.WriteLine($"Image: {imageFile.FullName}");
            Console.WriteLine($"Model: {modelFile.FullName}");
            Console.WriteLine($"Confidence threshold: {confidence}");
            Console.WriteLine();
            
            telemetry.StartTime = DateTime.UtcNow;
            telemetry.ImagePath = imageFile.FullName;
            telemetry.ModelPath = modelFile.FullName;
            telemetry.ConfidenceThreshold = confidence;
            
            // Load model and run face detection
            using var scrfdModel = new SCRFDModel(modelFile.FullName);
            var result = await scrfdModel.DetectFaces(imageFile.FullName);
            
            stopwatch.Stop();
            telemetry.TotalTimeMs = stopwatch.ElapsedMilliseconds;
            telemetry.InferenceTimeMs = result.InferenceTimeMs;
            telemetry.EndTime = DateTime.UtcNow;
            telemetry.Success = true;
            telemetry.DetectedFaces = result.Detections.Count;
            telemetry.ImageSize = result.ImageSize;
            telemetry.Detections = result.Detections;
            
            // Output results
            await OutputDetectionResults(telemetry, outputFile);
            
            // Save visualization if requested
            if (visualizeFile != null && telemetry.Success && telemetry.Detections.Count > 0)
            {
                await ImageVisualizer.SaveDetectionResults(imageFile.FullName, telemetry.Detections, visualizeFile.FullName);
            }
            
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            telemetry.TotalTimeMs = stopwatch.ElapsedMilliseconds;
            telemetry.EndTime = DateTime.UtcNow;
            telemetry.Success = false;
            telemetry.ErrorMessage = ex.Message;
            
            Console.WriteLine($"Error during face detection: {ex.Message}");
            await OutputDetectionResults(telemetry, outputFile);
        }
    }
    
    static async Task OutputDetectionResults(FaceDetectionTelemetry telemetry, FileInfo? outputFile)
    {
        var telemetryJson = System.Text.Json.JsonSerializer.Serialize(telemetry, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
        
        if (outputFile != null)
        {
            await File.WriteAllTextAsync(outputFile.FullName, telemetryJson);
            Console.WriteLine($"\nResults saved to: {outputFile.FullName}");
        }
        else
        {
            Console.WriteLine("\n=== FACE DETECTION RESULTS ===");
            Console.WriteLine(telemetryJson);
        }
        
        // Output summary to console
        Console.WriteLine("\n=== SUMMARY ===");
        Console.WriteLine($"Status: {(telemetry.Success ? "SUCCESS" : "FAILED")}");
        Console.WriteLine($"Faces detected: {telemetry.DetectedFaces}");
        Console.WriteLine($"Total time: {telemetry.TotalTimeMs}ms");
        Console.WriteLine($"Inference time: {telemetry.InferenceTimeMs}ms");
        Console.WriteLine($"Image size: {telemetry.ImageSize.Width}x{telemetry.ImageSize.Height}");
        
        if (telemetry.Success && telemetry.Detections.Count > 0)
        {
            Console.WriteLine("\nDetected faces:");
            for (int i = 0; i < telemetry.Detections.Count; i++)
            {
                var face = telemetry.Detections[i];
                Console.WriteLine($"  Face {i + 1}: Confidence={face.Confidence:F3}, " +
                                $"Box=({face.BoundingBox.X:F0},{face.BoundingBox.Y:F0},{face.BoundingBox.Width:F0},{face.BoundingBox.Height:F0}), " +
                                $"Keypoints={face.Keypoints.Count}");
            }
        }
        
        if (!telemetry.Success && !string.IsNullOrEmpty(telemetry.ErrorMessage))
        {
            Console.WriteLine($"Error: {telemetry.ErrorMessage}");
        }
    }
}
