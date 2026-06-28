using System.Net.Http;

namespace DynamicIsland.Windows.Services.Vision;

/// <summary>Resolved on-disk paths for the detection models plus readiness flags.</summary>
public sealed record VisionModelPaths(
    string YoloCfg, string YoloWeights, string CocoNames,
    string FaceCascade, string Sface,
    bool PersonReady, bool FaceReady);

/// <summary>
/// Owns the model cache under %LOCALAPPDATA%/DynamicIsland.Windows/models. Models are NEVER fetched
/// automatically — <see cref="DownloadAsync"/> is only called from Settings after the user consents.
/// Users can also drop the files into the folder by hand (manual-placement fallback).
/// </summary>
public sealed class VisionModelManager(LoggingService log)
{
    private sealed record Asset(string FileName, string Url, bool RequiredForPerson, bool RequiredForFace);

    // Person detection (YOLOv4-tiny via Darknet) + face path (Haar detect + SFace ONNX embedding).
    private static readonly Asset[] Assets =
    [
        new("yolov4-tiny.cfg",
            "https://raw.githubusercontent.com/AlexeyAB/darknet/master/cfg/yolov4-tiny.cfg", true, false),
        new("yolov4-tiny.weights",
            "https://github.com/AlexeyAB/darknet/releases/download/yolov4/yolov4-tiny.weights", true, false),
        new("coco.names",
            "https://raw.githubusercontent.com/AlexeyAB/darknet/master/data/coco.names", true, false),
        new("haarcascade_frontalface_default.xml",
            "https://raw.githubusercontent.com/opencv/opencv/4.x/data/haarcascades/haarcascade_frontalface_default.xml", false, true),
        new("face_recognition_sface_2021dec.onnx",
            "https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx", false, true),
    ];

    public string ModelsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland.Windows", "models");

    public string OwnerFacePath => Path.Combine(ModelsDir, "owner_face.bin");

    private string PathFor(string fileName) => Path.Combine(ModelsDir, fileName);

    public VisionModelPaths Resolve()
    {
        bool personReady = Assets.Where(a => a.RequiredForPerson).All(a => File.Exists(PathFor(a.FileName)));
        bool faceReady = Assets.Where(a => a.RequiredForFace).All(a => File.Exists(PathFor(a.FileName)));
        return new VisionModelPaths(
            PathFor("yolov4-tiny.cfg"), PathFor("yolov4-tiny.weights"), PathFor("coco.names"),
            PathFor("haarcascade_frontalface_default.xml"), PathFor("face_recognition_sface_2021dec.onnx"),
            personReady, faceReady);
    }

    public bool AllPresent => Assets.All(a => File.Exists(PathFor(a.FileName)));

    /// <summary>Downloads any missing model files. Caller MUST have obtained user consent first.</summary>
    public async Task<bool> DownloadAsync(IProgress<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(ModelsDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland.Windows");

        var missing = Assets.Where(a => !File.Exists(PathFor(a.FileName))).ToArray();
        if (missing.Length == 0) { progress?.Report("All models already present."); return true; }

        for (var i = 0; i < missing.Length; i++)
        {
            var asset = missing[i];
            progress?.Report($"Downloading {asset.FileName} ({i + 1}/{missing.Length})…");
            try
            {
                var tmp = PathFor(asset.FileName) + ".tmp";
                await using (var response = await http.GetStreamAsync(asset.Url, token))
                await using (var file = File.Create(tmp))
                    await response.CopyToAsync(file, token);
                File.Move(tmp, PathFor(asset.FileName), true);
            }
            catch (Exception ex)
            {
                log.Error($"Failed to download vision model {asset.FileName}", ex);
                progress?.Report($"Failed to download {asset.FileName}: {ex.Message}");
                return false;
            }
        }

        progress?.Report(AllPresent ? "All models downloaded." : "Some models are still missing.");
        return AllPresent;
    }

    public float[]? LoadOwnerEmbedding()
    {
        try
        {
            if (!File.Exists(OwnerFacePath)) return null;
            var bytes = File.ReadAllBytes(OwnerFacePath);
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) return null;
            var values = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
        catch (Exception ex) { log.Error("Unable to read owner face embedding", ex); return null; }
    }

    public void SaveOwnerEmbedding(float[] embedding)
    {
        Directory.CreateDirectory(ModelsDir);
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        var tmp = OwnerFacePath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, OwnerFacePath, true);
    }

    public void DeleteOwnerEmbedding()
    {
        try { if (File.Exists(OwnerFacePath)) File.Delete(OwnerFacePath); }
        catch (Exception ex) { log.Error("Unable to remove owner face embedding", ex); }
    }
}
