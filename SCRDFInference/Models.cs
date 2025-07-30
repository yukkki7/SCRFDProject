namespace SCRDFInference;

public class SCRFDResult
{
    public List<FaceDetection> Detections { get; set; } = new();
    public Size ImageSize { get; set; }
    public long InferenceTimeMs { get; set; }
}

public class FaceDetection
{
    public float Confidence { get; set; }
    public BoundingBox BoundingBox { get; set; } = new();
    public List<Point2D> Keypoints { get; set; } = new();
}

public class BoundingBox
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public class Point2D
{
    public float X { get; set; }
    public float Y { get; set; }
}

public struct Size
{
    public int Width { get; set; }
    public int Height { get; set; }
    
    public Size(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

public class FaceDetectionTelemetry
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long TotalTimeMs { get; set; }
    public long InferenceTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public float ConfidenceThreshold { get; set; }
    public int DetectedFaces { get; set; }
    public Size ImageSize { get; set; }
    public List<FaceDetection> Detections { get; set; } = new();
}