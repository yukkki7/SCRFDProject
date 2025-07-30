using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SCRDFInference;

public static class ImageVisualizer
{
    public static async Task SaveDetectionResults(string inputImagePath, List<FaceDetection> detections, string outputImagePath)
    {
        using var image = await Image.LoadAsync<Rgb24>(inputImagePath);
        
        // Draw bounding boxes and keypoints
        image.Mutate(ctx =>
        {
            for (int i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                var bbox = detection.BoundingBox;
                
                // Choose color based on confidence
                var color = GetConfidenceColor(detection.Confidence);
                
                // Draw bounding box - ensure positive width/height
                var x = Math.Min(bbox.X, bbox.X + bbox.Width);
                var y = Math.Min(bbox.Y, bbox.Y + bbox.Height);
                var width = Math.Abs(bbox.Width);
                var height = Math.Abs(bbox.Height);
                
                var rect = new RectangleF(x, y, width, height);
                ctx.Draw(color, 4, rect);
                
                // Draw a small filled rectangle as face number indicator
                var numberRect = new RectangleF(x, y - 20, 30, 15);
                ctx.Fill(color, numberRect);
                
                // Draw keypoints if available
                if (detection.Keypoints.Count >= 5)
                {
                    DrawFacialKeypoints(ctx, detection.Keypoints, color);
                }
            }
            
            // Add summary indicator - simple colored rectangle
            var summaryRect = new RectangleF(20, 20, 200, 30);
            ctx.Fill(Color.Black, summaryRect);
            ctx.Draw(Color.White, 2, summaryRect);
        });
        
        await image.SaveAsync(outputImagePath);
        Console.WriteLine($"Detection results saved to: {outputImagePath}");
        Console.WriteLine($"Found {detections.Count} faces with bounding boxes and keypoints");
    }
    
    private static Color GetConfidenceColor(float confidence)
    {
        // Color gradient from red (low confidence) to green (high confidence)
        if (confidence >= 0.8f)
            return Color.LimeGreen;
        else if (confidence >= 0.6f)
            return Color.Orange;
        else if (confidence >= 0.4f)
            return Color.Yellow;
        else
            return Color.Red;
    }
    
    private static void DrawFacialKeypoints(IImageProcessingContext ctx, List<Point2D> keypoints, Color color)
    {
        // Standard 5-point facial landmarks: left eye, right eye, nose, left mouth, right mouth
        var keypointColors = new[] { Color.Red, Color.Blue, Color.Green, Color.Yellow, Color.Magenta };
        
        for (int i = 0; i < Math.Min(keypoints.Count, 5); i++)
        {
            var point = keypoints[i];
            var keypointColor = i < keypointColors.Length ? keypointColors[i] : color;
            
            // Draw keypoint as filled circle
            var radius = 6f;
            var circle = new RectangleF(point.X - radius/2, point.Y - radius/2, radius, radius);
            ctx.Fill(keypointColor, circle);
            ctx.Draw(Color.White, 2, circle);
        }
    }
}