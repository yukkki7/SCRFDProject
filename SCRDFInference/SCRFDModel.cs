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
        
        // Debug: Print all output names and shapes
        Console.WriteLine("Model outputs:");
        foreach (var output in outputs)
        {
            if (output.Value is Tensor<float> tensor)
            {
                Console.WriteLine($"  {output.Name}: Shape=[{string.Join(", ", tensor.Dimensions.ToArray())}]");
            }
        }
        
        // SCRFD typically has multiple outputs for different feature pyramid levels
        // Common patterns: score_8, score_16, score_32, bbox_8, bbox_16, bbox_32, kps_8, kps_16, kps_32
        var scoreOutputs = outputs.Where(x => x.Name.Contains("score") || x.Name.Contains("cls")).ToList();
        var bboxOutputs = outputs.Where(x => x.Name.Contains("bbox") || x.Name.Contains("loc")).ToList();
        var kpsOutputs = outputs.Where(x => x.Name.Contains("kps") || x.Name.Contains("landmark")).ToList();
        
        if (scoreOutputs.Count == 0 || bboxOutputs.Count == 0)
        {
            Console.WriteLine("Warning: Could not find expected SCRFD outputs");
            Console.WriteLine($"Found {scoreOutputs.Count} score outputs and {bboxOutputs.Count} bbox outputs");
            return detections;
        }
        
        // Process each feature pyramid level
        for (int i = 0; i < Math.Min(scoreOutputs.Count, bboxOutputs.Count); i++)
        {
            var scoreOutput = scoreOutputs[i].Value as Tensor<float>;
            var bboxOutput = bboxOutputs[i].Value as Tensor<float>;
            var kpsOutput = i < kpsOutputs.Count ? kpsOutputs[i].Value as Tensor<float> : null;
            
            if (scoreOutput != null && bboxOutput != null)
            {
                var levelDetections = ProcessSingleLevel(scoreOutput, bboxOutput, kpsOutput, originalSize, inputSize, i);
                detections.AddRange(levelDetections);
            }
        }
        
        // Apply NMS (Non-Maximum Suppression)
        detections = ApplyNMS(detections, 0.4f);
        
        return detections;
    }
    
    private List<FaceDetection> ProcessSingleLevel(Tensor<float> scores, Tensor<float> bboxes, Tensor<float>? keypoints, Size originalSize, int inputSize, int level)
    {
        var detections = new List<FaceDetection>();
        
        if (scores == null || bboxes == null) return detections;
        
        var scoresArray = scores.ToArray();
        var bboxesArray = bboxes.ToArray();
        
        // Calculate scale factors for coordinate conversion
        var scaleX = (float)originalSize.Width / inputSize;
        var scaleY = (float)originalSize.Height / inputSize;
        
        // SCRFD uses anchor-based detection with different strides
        var stride = Math.Pow(2, level + 3); // 8, 16, 32 for levels 0, 1, 2
        var confidenceThreshold = 0.5f;
        
        // Process scores and bboxes based on actual tensor shapes
        var scoreDims = scores.Dimensions.ToArray();
        var bboxDims = bboxes.Dimensions.ToArray();
        
        Console.WriteLine($"Level {level}: Score dims=[{string.Join(",", scoreDims)}], BBox dims=[{string.Join(",", bboxDims)}]");
        
        // Typical SCRFD output format: [batch, anchors, classes] for scores, [batch, anchors, 4] for bboxes
        if (scoreDims.Length >= 2 && bboxDims.Length >= 2)
        {
            var numAnchors = scoreDims[1];
            var numClasses = scoreDims.Length > 2 ? scoreDims[2] : 1;
            
            for (int anchor = 0; anchor < numAnchors; anchor++)
            {
                // Get confidence score (use max across classes if multi-class)
                float confidence = 0;
                if (numClasses == 1)
                {
                    confidence = scoresArray[anchor];
                }
                else
                {
                    for (int cls = 0; cls < numClasses; cls++)
                    {
                        var score = scoresArray[anchor * numClasses + cls];
                        confidence = Math.Max(confidence, score);
                    }
                }
                
                if (confidence > confidenceThreshold)
                {
                    // Extract bounding box coordinates
                    var x1 = bboxesArray[anchor * 4] * scaleX;
                    var y1 = bboxesArray[anchor * 4 + 1] * scaleY;
                    var x2 = bboxesArray[anchor * 4 + 2] * scaleX;
                    var y2 = bboxesArray[anchor * 4 + 3] * scaleY;
                    
                    var detection = new FaceDetection
                    {
                        Confidence = confidence,
                        BoundingBox = new BoundingBox
                        {
                            X = x1,
                            Y = y1,
                            Width = x2 - x1,
                            Height = y2 - y1
                        }
                    };
                    
                    // Add keypoints if available
                    if (keypoints != null)
                    {
                        var kpsArray = keypoints.ToArray();
                        detection.Keypoints = new List<Point2D>();
                        for (int k = 0; k < 5; k++) // 5 facial keypoints
                        {
                            if (anchor * 10 + k * 2 + 1 < kpsArray.Length)
                            {
                                detection.Keypoints.Add(new Point2D
                                {
                                    X = kpsArray[anchor * 10 + k * 2] * scaleX,
                                    Y = kpsArray[anchor * 10 + k * 2 + 1] * scaleY
                                });
                            }
                        }
                    }
                    
                    detections.Add(detection);
                }
            }
        }
        
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

