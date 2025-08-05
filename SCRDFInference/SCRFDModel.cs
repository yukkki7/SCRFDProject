using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics.Tensors;

namespace SCRDFInference;

public class PreprocessingInfo
{
    public float Scale { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int TargetSize { get; set; }
}

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
    
    public async Task<SCRFDResult> DetectFaces(string imagePath, float confidenceThreshold = 0.5f)
    {
        var result = new SCRFDResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Load and preprocess image
        using var image = await Image.LoadAsync<Rgb24>(imagePath);
        var originalSize = new Size(image.Width, image.Height);
        
        // SCRFD typically uses 640x640 input
        var targetSize = _inputShape[2]; // Assuming NCHW format
        
        // Calculate preprocessing parameters for coordinate transformation
        var scale = Math.Min((float)targetSize / image.Width, (float)targetSize / image.Height);
        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);
        var offsetX = (targetSize - newWidth) / 2;
        var offsetY = (targetSize - newHeight) / 2;
        
        var preprocessingInfo = new PreprocessingInfo
        {
            Scale = scale,
            OffsetX = offsetX,
            OffsetY = offsetY,
            TargetSize = targetSize
        };
        
        var preprocessed = PreprocessImage(image, targetSize);
        
        // Create input tensor
        var inputTensor = new DenseTensor<float>(preprocessed, _inputShape);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
        };
        
        // Run inference
        using var outputs = _session.Run(inputs);
        
        // Process outputs with correct coordinate transformation
        var detections = ProcessOutputs(outputs, originalSize, preprocessingInfo, confidenceThreshold);
        
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
    
    private List<FaceDetection> ProcessOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, Size originalSize, PreprocessingInfo preprocessingInfo, float confidenceThreshold)
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
                var levelDetections = ProcessSingleLevel(scoreOutput, bboxOutput, kpsOutput, originalSize, preprocessingInfo, i, confidenceThreshold);
                detections.AddRange(levelDetections);
            }
        }
        
        // Apply NMS (Non-Maximum Suppression) with more aggressive threshold
        detections = ApplyNMS(detections, 0.3f);
        
        return detections;
    }
    
    private List<FaceDetection> ProcessSingleLevel(Tensor<float> scores, Tensor<float> bboxes, Tensor<float>? keypoints, Size originalSize, PreprocessingInfo preprocessingInfo, int level, float confidenceThreshold = 0.5f)
    {
        var detections = new List<FaceDetection>();
        
        if (scores == null || bboxes == null) return detections;
        
        var scoresArray = scores.ToArray();
        var bboxesArray = bboxes.ToArray();
        
        // SCRFD uses anchor-based detection with different strides
        var levelStride = Math.Pow(2, level + 3); // 8, 16, 32 for levels 0, 1, 2
        
        // Process scores and bboxes based on actual tensor shapes
        var scoreDims = scores.Dimensions.ToArray();
        var bboxDims = bboxes.Dimensions.ToArray();
        
        Console.WriteLine($"Level {level}: Score dims=[{string.Join(",", scoreDims)}], BBox dims=[{string.Join(",", bboxDims)}]");
        
        // Debug: Print some sample values
        if (scoresArray.Length > 0 && bboxesArray.Length > 0)
        {
            Console.WriteLine($"Sample raw scores: [{scoresArray[0]:F3}, {scoresArray[Math.Min(1, scoresArray.Length-1)]:F3}, {scoresArray[Math.Min(2, scoresArray.Length-1)]:F3}]");
            Console.WriteLine($"Sample sigmoid scores: [{Sigmoid(scoresArray[0]):F3}, {Sigmoid(scoresArray[Math.Min(1, scoresArray.Length-1)]):F3}, {Sigmoid(scoresArray[Math.Min(2, scoresArray.Length-1)]):F3}]");
            Console.WriteLine($"Sample bboxes: [{bboxesArray[0]:F1}, {bboxesArray[Math.Min(1, bboxesArray.Length-1)]:F1}, {bboxesArray[Math.Min(2, bboxesArray.Length-1)]:F1}, {bboxesArray[Math.Min(3, bboxesArray.Length-1)]:F1}]");
        }
        
        int candidateCount = 0;
        int filteredCount = 0;
        
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
                    // Apply sigmoid activation to convert logits to probability
                    var rawScore = scoresArray[anchor];
                    confidence = Sigmoid(rawScore);
                }
                else
                {
                    for (int cls = 0; cls < numClasses; cls++)
                    {
                        var rawScore = scoresArray[anchor * numClasses + cls];
                        var score = Sigmoid(rawScore);
                        confidence = Math.Max(confidence, score);
                    }
                }
                
                if (confidence > confidenceThreshold)
                {
                    candidateCount++;
                    // SCRFD outputs are in distance format, need to decode with anchors
                    // Generate anchor for this position
                    var currentStride = (float)Math.Pow(2, level + 3); // 8, 16, 32 for levels 0, 1, 2
                    var featureMapSize = (int)(preprocessingInfo.TargetSize / currentStride);
                    
                    // SCRFD has 2 anchors per spatial location
                    var spatialIndex = anchor / 2;  // Which spatial location
                    var anchorIndex = anchor % 2;   // Which anchor at that location
                    
                    // Calculate spatial position
                    var gridY = spatialIndex / featureMapSize;
                    var gridX = spatialIndex % featureMapSize;
                    
                    // Calculate anchor center position in 640x640 space
                    var anchorX = (gridX + 0.5f) * currentStride;
                    var anchorY = (gridY + 0.5f) * currentStride;
                    
                    // Decode bounding box from distance predictions
                    var left = bboxesArray[anchor * 4];     // distance to left edge
                    var top = bboxesArray[anchor * 4 + 1];  // distance to top edge  
                    var right = bboxesArray[anchor * 4 + 2]; // distance to right edge
                    var bottom = bboxesArray[anchor * 4 + 3]; // distance to bottom edge
                    
                    // Convert distances to coordinates in model space (640x640)
                    var x1_model = anchorX - left;
                    var y1_model = anchorY - top;
                    var x2_model = anchorX + right;
                    var y2_model = anchorY + bottom;
                    
                    // Transform coordinates back to original image space
                    // Step 1: Remove padding offset
                    var x1_scaled = x1_model - preprocessingInfo.OffsetX;
                    var y1_scaled = y1_model - preprocessingInfo.OffsetY;
                    var x2_scaled = x2_model - preprocessingInfo.OffsetX;
                    var y2_scaled = y2_model - preprocessingInfo.OffsetY;
                    
                    // Step 2: Scale back to original image size
                    var x1_original = x1_scaled / preprocessingInfo.Scale;
                    var y1_original = y1_scaled / preprocessingInfo.Scale;
                    var x2_original = x2_scaled / preprocessingInfo.Scale;
                    var y2_original = y2_scaled / preprocessingInfo.Scale;
                    
                    // Ensure coordinates are within image bounds
                    x1_original = Math.Max(0, Math.Min(originalSize.Width, x1_original));
                    y1_original = Math.Max(0, Math.Min(originalSize.Height, y1_original));
                    x2_original = Math.Max(0, Math.Min(originalSize.Width, x2_original));
                    y2_original = Math.Max(0, Math.Min(originalSize.Height, y2_original));
                    
                    // Calculate width and height
                    var width = x2_original - x1_original;
                    var height = y2_original - y1_original;
                    
                    // Filter out invalid detections
                    var minFaceSize = 10; // Minimum face size in pixels (relaxed)
                    var maxFaceSize = Math.Min(originalSize.Width, originalSize.Height); // Max 100% of image
                    var aspectRatioMin = 0.2; // Very relaxed aspect ratio
                    var aspectRatioMax = 5.0;
                    
                    // Debug first few filtered detections
                    if (filteredCount < 3 && (width < minFaceSize || height < minFaceSize || 
                        width > maxFaceSize || height > maxFaceSize ||
                        width / height < aspectRatioMin || width / height > aspectRatioMax))
                    {
                        Console.WriteLine($"  Filtered detection: size=({width:F1}x{height:F1}), aspect={width/height:F2}, bounds=({x1_original:F1},{y1_original:F1})");
                    }
                    
                    if (width < minFaceSize || height < minFaceSize || 
                        width > maxFaceSize || height > maxFaceSize ||
                        width / height < aspectRatioMin || width / height > aspectRatioMax)
                    {
                        filteredCount++;
                        continue; // Skip this detection
                    }
                    
                    // Debug output for coordinate transformation
                    if (detections.Count < 3) // Only show first few detections
                    {
                        Console.WriteLine($"Detection {detections.Count + 1}:");
                        Console.WriteLine($"  Anchor: ({anchorX:F1}, {anchorY:F1}), Distances: L={left:F1}, T={top:F1}, R={right:F1}, B={bottom:F1}");
                        Console.WriteLine($"  Model coords: ({x1_model:F1}, {y1_model:F1}, {x2_model:F1}, {y2_model:F1})");
                        Console.WriteLine($"  Final coords: ({x1_original:F1}, {y1_original:F1}, {x2_original:F1}, {y2_original:F1})");
                        Console.WriteLine($"  Preprocessing: scale={preprocessingInfo.Scale:F3}, offset=({preprocessingInfo.OffsetX}, {preprocessingInfo.OffsetY})");
                    }
                    
                    var detection = new FaceDetection
                    {
                        Confidence = confidence,
                        BoundingBox = new BoundingBox
                        {
                            X = x1_original,
                            Y = y1_original,
                            Width = width,
                            Height = height
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
                                // SCRFD keypoints are relative to anchor position
                                var kp_x_offset = kpsArray[anchor * 10 + k * 2];
                                var kp_y_offset = kpsArray[anchor * 10 + k * 2 + 1];
                                
                                // Convert to absolute coordinates in model space
                                var kp_x_model = (float)(anchorX + kp_x_offset);
                                var kp_y_model = (float)(anchorY + kp_y_offset);
                                
                                // Transform keypoints back to original image space
                                var kp_x_scaled = kp_x_model - preprocessingInfo.OffsetX;
                                var kp_y_scaled = kp_y_model - preprocessingInfo.OffsetY;
                                
                                var kp_x_original = kp_x_scaled / preprocessingInfo.Scale;
                                var kp_y_original = kp_y_scaled / preprocessingInfo.Scale;
                                
                                detection.Keypoints.Add(new Point2D
                                {
                                    X = kp_x_original,
                                    Y = kp_y_original
                                });
                            }
                        }
                    }
                    
                    detections.Add(detection);
                }
            }
        }
        
        Console.WriteLine($"Level {level}: Candidates above threshold: {candidateCount}, Filtered out: {filteredCount}, Final detections: {detections.Count}");
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
    
    private static float Sigmoid(float x)
    {
        return 1.0f / (1.0f + (float)Math.Exp(-x));
    }
    
    public void Dispose()
    {
        _session?.Dispose();
    }
}

