using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCRDFInference
{
    public static class ImageVisualizer
    {
        public static async Task SaveDetectionResults(
            string inputImagePath,
            IList<FaceDetection> detections,
            string outputImagePath)
        {
            if (detections == null || detections.Count == 0)
                throw new ArgumentException("No detections to visualize.", nameof(detections));

            var best = detections
                .OrderByDescending(d => d.Confidence)
                .First();

            using var bmp = (Bitmap)Image.FromFile(inputImagePath);
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(Color.Red, 2);
            using var font = new Font("Arial", 16f, FontStyle.Bold);
            using var brush = new SolidBrush(Color.Yellow);

            var box = best.BoundingBox;
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

            string label = best.Confidence.ToString("F2");
            var textSize = g.MeasureString(label, font);
            float textX = box.X;
            float textY = box.Y - textSize.Height;
            if (textY < 0) textY = box.Y + 1;
            g.DrawString(label, font, brush, textX, textY);

            var dir = Path.GetDirectoryName(outputImagePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bmp.Save(outputImagePath, ImageFormat.Jpeg);
            Console.WriteLine($"[INFO] Visualization saved to: {outputImagePath}");

            await Task.CompletedTask;
        }
    }
}
