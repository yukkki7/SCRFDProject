using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics.Tensors;

namespace SCRDFInference;

public class SCRFDModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int[] _inputShape;
    
    public SCRFDModel(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        
        // Get input metadata
        var inputMeta = _session.InputMetadata.First();
        _inputName = inputMeta.Key;
        _inputShape = inputMeta.Value.Dimensions.ToArray();
        
        Console.WriteLine($"SCRFD Model loaded: {modelPath}");
        Console.WriteLine($"Input: {_inputName}, Shape: [{string.Join(", ", _inputShape)}]");
    }
    
    public async Task<SCRFDResult> DetectFaces(string imagePath)
    {
        var result = new SCRFDResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Load and preprocess image
        using var image = await Image.LoadAsync<Rgb24>(imagePath);
        var originalSize = new Size(image.Width, image.Height);
        
        // SCRFD typically uses 640x640 input
        var targetSize = _inputShape[2]; // Assuming NCHW format
        var preprocessed = PreprocessImage(image, targetSize);
        
        // Create input tensor
        var inputTensor = new DenseTensor<float>(preprocessed, _inputShape);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
        };
        
        // Run inference
        using var outputs = _session.Run(inputs);
        
        // Process outputs - SCRFD typically has multiple outputs for different scales
        var detections = ProcessOutputs(outputs, originalSize, targetSize);
        
        stopwatch.Stop();
        result.InferenceTimeMs = stopwatch.ElapsedMilliseconds;
        result.Detections = detections;
        result.ImageSize = originalSize;
        
        return result;
    }
    
    private float[] PreprocessImage(Image<Rgb24> image, int targetSize)
    {
        // Resize while maintaining aspect ratio
        var scale = Math.Min((float)targetSize / image.Width, (float)targetSize / image.Height);
        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);
        
        image.Mutate(x => x.Resize(newWidth, newHeight));
        
        // Create padded image
        using var paddedImage = new Image<Rgb24>(targetSize, targetSize, SixLabors.ImageSharp.Color.Black);
        var offsetX = (targetSize - newWidth) / 2;
        var offsetY = (targetSize - newHeight) / 2;
        
        paddedImage.Mutate(x => x.DrawImage(image, new Point(offsetX, offsetY), 1f));
        
        // Convert to float array (CHW format)
        var inputData = new float[3 * targetSize * targetSize];
        var pixelIndex = 0;
        
        // SCRFD preprocessing: normalize to [-1, 1] and apply mean/std
        var mean = new float[] { 127.5f, 127.5f, 127.5f };
        var std = new float[] { 128.0f, 128.0f, 128.0f };
        
        for (int y = 0; y < targetSize; y++)
        {
            for (int x = 0; x < targetSize; x++)
            {
                var pixel = paddedImage[x, y];
                
                // Normalize: (pixel - mean) / std
                inputData[pixelIndex] = (pixel.R - mean[0]) / std[0];                           // R channel
                inputData[pixelIndex + targetSize * targetSize] = (pixel.G - mean[1]) / std[1]; // G channel  
                inputData[pixelIndex + 2 * targetSize * targetSize] = (pixel.B - mean[2]) / std[2]; // B channel
                
                pixelIndex++;
            }
        }
        
        return inputData;
    }
    
    private List<FaceDetection> ProcessOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, Size originalSize, int inputSize)
    {
        var detections = new List<FaceDetection>();
        
        // SCRFD outputs: scores, bboxes, kpss (keypoints)
        var scoresOutput = outputs.FirstOrDefault(x => x.Name.Contains("scores") || x.Name.Contains("cls"))?.Value as Tensor<float>;
        var bboxesOutput = outputs.FirstOrDefault(x => x.Name.Contains("bbox") || x.Name.Contains("loc"))?.Value as Tensor<float>;
        var keypointsOutput = outputs.FirstOrDefault(x => x.Name.Contains("kps") || x.Name.Contains("landmark"))?.Value as Tensor<float>;
        
        if (scoresOutput == null || bboxesOutput == null)
        {
            Console.WriteLine("Warning: Could not find expected SCRFD outputs");
            return detections;
        }
        
        var scores = scoresOutput.ToArray();
        var bboxes = bboxesOutput.ToArray();
        var keypoints = keypointsOutput?.ToArray();
        
        // Calculate scale factors for coordinate conversion
        var scaleX = (float)originalSize.Width / inputSize;
        var scaleY = (float)originalSize.Height / inputSize;
        
        // Process detections (simplified - actual SCRFD has anchor-based decoding)
        var numDetections = scores.Length;
        var confidenceThreshold = 0.5f;
        
        for (int i = 0; i < numDetections; i++)
        {
            if (scores[i] > confidenceThreshold)
            {
                var detection = new FaceDetection
                {
                    Confidence = scores[i],
                    BoundingBox = new BoundingBox
                    {
                        X = bboxes[i * 4] * scaleX,
                        Y = bboxes[i * 4 + 1] * scaleY,
                        Width = (bboxes[i * 4 + 2] - bboxes[i * 4]) * scaleX,
                        Height = (bboxes[i * 4 + 3] - bboxes[i * 4 + 1]) * scaleY
                    }
                };
                
                // Add keypoints if available
                if (keypoints != null && keypoints.Length > i * 10)
                {
                    detection.Keypoints = new List<Point2D>();
                    for (int k = 0; k < 5; k++) // 5 facial keypoints
                    {
                        detection.Keypoints.Add(new Point2D
                        {
                            X = keypoints[i * 10 + k * 2] * scaleX,
                            Y = keypoints[i * 10 + k * 2 + 1] * scaleY
                        });
                    }
                }
                
                detections.Add(detection);
            }
        }
        
        // Apply NMS (Non-Maximum Suppression)
        detections = ApplyNMS(detections, 0.4f);
        
        return detections;
    }
    
    private List<FaceDetection> ApplyNMS(List<FaceDetection> detections, float nmsThreshold)
    {
        var result = new List<FaceDetection>();
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        
        while (sorted.Count > 0)
        {
            var best = sorted[0];
            result.Add(best);
            sorted.RemoveAt(0);
            
            sorted.RemoveAll(detection => CalculateIoU(best.BoundingBox, detection.BoundingBox) > nmsThreshold);
        }
        
        return result;
    }
    
    private float CalculateIoU(BoundingBox box1, BoundingBox box2)
    {
        var x1 = Math.Max(box1.X, box2.X);
        var y1 = Math.Max(box1.Y, box2.Y);
        var x2 = Math.Min(box1.X + box1.Width, box2.X + box2.Width);
        var y2 = Math.Min(box1.Y + box1.Height, box2.Y + box2.Height);
        
        if (x2 <= x1 || y2 <= y1) return 0;
        
        var intersection = (x2 - x1) * (y2 - y1);
        var area1 = box1.Width * box1.Height;
        var area2 = box2.Width * box2.Height;
        var union = area1 + area2 - intersection;
        
        return intersection / union;
    }
    
    public void Dispose()
    {
        _session?.Dispose();
    }
}

