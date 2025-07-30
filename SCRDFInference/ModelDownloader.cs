using System.Net.Http;

namespace SCRDFInference;

public static class ModelDownloader
{
    private static readonly HttpClient _httpClient = new();
    
    // SCRFD model URLs - using GitHub LFS media URLs
    private static readonly Dictionary<string, string> ModelUrls = new()
    {
        ["scrfd_500m"] = "https://media.githubusercontent.com/media/cysin/scrfd_onnx/main/scrfd_500m_bnkps.onnx",
        ["scrfd_1g"] = "https://media.githubusercontent.com/media/cysin/scrfd_onnx/main/scrfd_1g_bnkps.onnx",
        ["scrfd_2.5g"] = "https://media.githubusercontent.com/media/cysin/scrfd_onnx/main/scrfd_2.5g_bnkps.onnx",
        ["scrfd_10g"] = "https://media.githubusercontent.com/media/cysin/scrfd_onnx/main/scrfd_10g_bnkps.onnx"
    };
    
    public static async Task<string> DownloadModel(string modelName = "scrfd_500m", string? customPath = null)
    {
        if (!ModelUrls.ContainsKey(modelName))
        {
            throw new ArgumentException($"Unknown model: {modelName}. Available models: {string.Join(", ", ModelUrls.Keys)}");
        }
        
        var url = ModelUrls[modelName];
        var fileName = $"{modelName}_bnkps.onnx";
        var filePath = customPath ?? Path.Combine("models", fileName);
        
        // Create models directory if it doesn't exist
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        // Check if model already exists
        if (File.Exists(filePath))
        {
            Console.WriteLine($"Model already exists: {filePath}");
            return filePath;
        }
        
        Console.WriteLine($"Downloading {modelName} model from {url}...");
        Console.WriteLine($"Source: https://github.com/cysin/scrfd_onnx");
        Console.WriteLine($"This may take a few minutes depending on your internet connection.");
        
        // Check if model is available first
        if (!await CheckModelAvailability(modelName))
        {
            Console.WriteLine($"Warning: Model {modelName} may not be available at the expected URL.");
            Console.WriteLine("Attempting download anyway...");
        }
        
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var downloadedBytes = 0L;
            
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            
            var buffer = new byte[8192];
            int bytesRead;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                downloadedBytes += bytesRead;
                
                if (totalBytes > 0)
                {
                    var progress = (double)downloadedBytes / totalBytes * 100;
                    Console.Write($"\rProgress: {progress:F1}% ({downloadedBytes / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB)");
                }
            }
            
            Console.WriteLine($"\nModel downloaded successfully: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError downloading model: {ex.Message}");
            throw;
        }
    }
    
    public static void ListAvailableModels()
    {
        Console.WriteLine("Available SCRFD models from cysin/scrfd_onnx:");
        Console.WriteLine("- scrfd_500m: Lightweight model");
        Console.WriteLine("- scrfd_1g: Balanced model");
        Console.WriteLine("- scrfd_2.5g: High accuracy model");
        Console.WriteLine("- scrfd_10g: Highest accuracy model");
        Console.WriteLine();
        Console.WriteLine("Usage: --download-model scrfd_500m");
        Console.WriteLine("Source: https://github.com/cysin/scrfd_onnx");
    }
    
    public static async Task<bool> CheckModelAvailability(string modelName)
    {
        if (!ModelUrls.ContainsKey(modelName))
            return false;
            
        try
        {
            var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, ModelUrls[modelName]));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}