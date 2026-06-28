using System.Runtime.InteropServices;
using DynamicIsland.Windows.Models;
using OpenCvSharp;
using OpenCvSharp.Dnn;
// UseWindowsForms pulls in System.Drawing globally; pin the geometry types to OpenCV's.
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;

namespace DynamicIsland.Windows.Services.Vision;

public readonly record struct DetectionResult(int PeopleCount, int[] OwnerIndexes, bool DetectorReady, byte[]? Preview);

/// <summary>
/// Owns every OpenCV object (camera + nets). No threading or WPF here — <see cref="VisionService"/>
/// drives it from a single background thread. Person detection is YOLOv4-tiny (Darknet) with a
/// HOG + Haar fallback; owner recognition is a Haar face detector + SFace ONNX embedding (cosine
/// match), since this OpenCvSharp build does not ship the FaceDetectorYN/FaceRecognizerSF wrappers.
/// </summary>
internal sealed class VisionDetector : IDisposable
{
    private const int FrameWidth = 640;       // detection working size (downscaled)
    private const int FrameHeight = 360;
    private const int PreviewWidth = 960;      // preview is encoded from the full-res frame, not detection size
    private const int PreviewHeight = 540;
    private const int BlobSize = 416;
    private const float PersonConfidence = 0.32f;
    private const float NmsThreshold = 0.42f;
    private const int MinBoxArea = 800;
    private const int EmbeddingSize = 128;

    private readonly LoggingService _log;
    private readonly int _cameraIndex;

    private VideoCapture? _capture;
    private Net? _yolo;
    private string[] _yoloOutputs = [];
    private int _personClassId = -1;
    private HOGDescriptor? _hog;
    private CascadeClassifier? _upperBody;
    private CascadeClassifier? _face;
    private Net? _sface;
    private List<Rect> _lastPeople = [];   // cached between detection ticks so the preview can draw them every frame
    private int[] _lastOwners = [];

    public bool DetectorReady { get; private set; }
    public bool FaceReady { get; private set; }

    public VisionDetector(LoggingService log, int cameraIndex)
    {
        _log = log;
        _cameraIndex = cameraIndex;
    }

    /// <summary>Opens the camera and loads whatever models are present. Returns false if the camera fails.</summary>
    public bool Initialize(VisionModelPaths models)
    {
        _capture = new VideoCapture(_cameraIndex);
        if (!_capture.IsOpened())
        {
            _log.Error($"Vision camera index {_cameraIndex} could not be opened", null);
            return false;
        }
        // MJPG lets most webcams deliver 720p/1080p at full frame rate (raw YUY2 is capped low), so the
        // preview looks like the camera's real quality instead of a blurry low-res fallback.
        _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
        _capture.Set(VideoCaptureProperties.FrameHeight, 720);
        _capture.Set(VideoCaptureProperties.Fps, 30);

        TryLoadPerson(models);
        TryLoadFace(models);
        return true;
    }

    private void TryLoadPerson(VisionModelPaths models)
    {
        try
        {
            if (models.PersonReady)
            {
                _yolo = CvDnn.ReadNetFromDarknet(models.YoloCfg, models.YoloWeights);
                _yolo.SetPreferableBackend(Backend.OPENCV);
                _yolo.SetPreferableTarget(Target.CPU);
                _yoloOutputs = _yolo.GetUnconnectedOutLayersNames()!.OfType<string>().ToArray();
                var names = File.ReadAllLines(models.CocoNames)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                _personClassId = Array.FindIndex(names, n => n.Equals("person", StringComparison.OrdinalIgnoreCase));
                DetectorReady = _yoloOutputs.Length > 0 && _personClassId >= 0;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load YOLOv4-tiny; falling back to degraded person detection", ex);
            _yolo?.Dispose();
            _yolo = null;
            DetectorReady = false;
        }

        if (!DetectorReady)
        {
            // Degraded mode: HOG full-body + upper-body Haar (face boxes are added during processing).
            try
            {
                _hog = new HOGDescriptor();
                _hog.SetSVMDetector(HOGDescriptor.GetDefaultPeopleDetector());
            }
            catch (Exception ex) { _log.Error("HOG people detector unavailable", ex); _hog = null; }
        }
    }

    private void TryLoadFace(VisionModelPaths models)
    {
        if (!models.FaceReady) return;
        try
        {
            _face = new CascadeClassifier(models.FaceCascade);
            _sface = CvDnn.ReadNetFromOnnx(models.Sface);
            _sface.SetPreferableBackend(Backend.OPENCV);
            _sface.SetPreferableTarget(Target.CPU);
            FaceReady = !_face.Empty();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load face models; owner recognition disabled", ex);
            _face?.Dispose(); _face = null;
            _sface?.Dispose(); _sface = null;
            FaceReady = false;
        }
    }

    /// <summary>Grabs one frame and center-crops it to 16:9 at the camera's full resolution. Caller disposes.</summary>
    private Mat? GrabCropped()
    {
        if (_capture is null) return null;
        var raw = new Mat();
        try
        {
            if (!_capture.Read(raw) || raw.Empty()) { raw.Dispose(); return null; }
            var targetAspect = (double)FrameWidth / FrameHeight;
            var aspect = (double)raw.Width / raw.Height;
            Rect crop;
            if (aspect > targetAspect)
            {
                var w = (int)Math.Round(raw.Height * targetAspect);
                crop = new Rect((raw.Width - w) / 2, 0, w, raw.Height);
            }
            else
            {
                var h = (int)Math.Round(raw.Width / targetAspect);
                crop = new Rect(0, (raw.Height - h) / 2, raw.Width, h);
            }
            return new Mat(raw, crop).Clone(); // own buffer (the view into raw would dangle once raw is freed)
        }
        catch (Exception ex) { _log.Error("Vision frame grab failed", ex); return null; }
        finally { raw.Dispose(); }
    }

    private static Rect Scale(Rect r, double sx, double sy) =>
        new((int)(r.X * sx), (int)(r.Y * sy), (int)(r.Width * sx), (int)(r.Height * sy));

    /// <summary>
    /// Grabs a frame, optionally runs (the relatively expensive) detection, and optionally encodes a crisp
    /// preview from the full-resolution frame. Detection results are cached so the preview can be produced
    /// every frame even when detection is throttled to a lower rate.
    /// </summary>
    public DetectionResult? Process(float[]? ownerEmbedding, float threshold, bool runDetection, bool wantPreview)
    {
        using var full = GrabCropped();
        if (full is null) return null;
        double fx = full.Width / (double)FrameWidth, fy = full.Height / (double)FrameHeight;

        if (runDetection)
        {
            using var det = new Mat();
            Cv2.Resize(full, det, new Size(FrameWidth, FrameHeight));
            var people = DetectPeople(det);

            var owners = new List<int>();
            if (FaceReady && ownerEmbedding is not null && people.Count > 0)
            {
                foreach (var faceRect in DetectFaces(det))
                {
                    var embedding = EmbedFace(full, Scale(faceRect, fx, fy)); // embed from full-res for fidelity
                    if (embedding is null) continue;
                    if (Cosine(embedding, ownerEmbedding) < threshold) continue;
                    var index = AssociateFace(faceRect, people);
                    if (index >= 0) owners.Add(index);
                }
            }
            _lastPeople = people;
            _lastOwners = owners.Distinct().ToArray();
        }

        var preview = wantPreview ? EncodePreview(full, _lastPeople, _lastOwners) : null;
        return new DetectionResult(_lastPeople.Count, _lastOwners, DetectorReady, preview);
    }

    // Crisp JPEG preview encoded from the full-res frame, with detection boxes drawn (boxes are in 640×360
    // detection space, scaled up to the preview size). Green = recognised owner, red = person.
    private byte[]? EncodePreview(Mat full, List<Rect> people, int[] owners)
    {
        try
        {
            using var small = new Mat();
            Cv2.Resize(full, small, new Size(PreviewWidth, PreviewHeight));
            double sx = PreviewWidth / (double)FrameWidth, sy = PreviewHeight / (double)FrameHeight;
            for (var i = 0; i < people.Count; i++)
            {
                var colour = owners.Contains(i) ? new Scalar(88, 209, 48) : new Scalar(48, 59, 255);
                Cv2.Rectangle(small, Scale(people[i], sx, sy), colour, 3);
            }
            Cv2.ImEncode(".jpg", small, out var buffer, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
            return buffer;
        }
        catch (Exception ex) { _log.Error("Preview encode failed", ex); return null; }
    }

    /// <summary>
    /// One enrollment step: grabs a frame, returns the largest-face embedding (or null if no face), plus a
    /// crisp preview with the detected face highlighted so the live UI keeps updating during enrollment.
    /// </summary>
    public (float[]? Embedding, bool FaceFound, byte[]? Preview) CaptureFaceForEnroll(bool wantPreview)
    {
        using var full = GrabCropped();
        if (full is null) return (null, false, null);
        double fx = full.Width / (double)FrameWidth, fy = full.Height / (double)FrameHeight;

        Rect? faceBox = null;
        float[]? embedding = null;
        if (FaceReady)
        {
            using var det = new Mat();
            Cv2.Resize(full, det, new Size(FrameWidth, FrameHeight));
            var faces = DetectFaces(det);
            if (faces.Length > 0)
            {
                faceBox = faces.OrderByDescending(f => f.Width * f.Height).First();
                embedding = EmbedFace(full, Scale(faceBox.Value, fx, fy));
            }
        }

        byte[]? preview = null;
        if (wantPreview)
        {
            try
            {
                using var small = new Mat();
                Cv2.Resize(full, small, new Size(PreviewWidth, PreviewHeight));
                if (faceBox is { } fb)
                {
                    double sx = PreviewWidth / (double)FrameWidth, sy = PreviewHeight / (double)FrameHeight;
                    Cv2.Rectangle(small, Scale(fb, sx, sy), new Scalar(88, 209, 48), 3);
                }
                Cv2.ImEncode(".jpg", small, out var buf, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
                preview = buf;
            }
            catch { preview = null; }
        }
        return (embedding, faceBox is not null, preview);
    }

    private List<Rect> DetectPeople(Mat frame)
    {
        if (DetectorReady && _yolo is not null) return DetectPeopleYolo(frame);
        return DetectPeopleFallback(frame);
    }

    private List<Rect> DetectPeopleYolo(Mat frame)
    {
        var boxes = new List<Rect>();
        var scores = new List<float>();
        using var blob = CvDnn.BlobFromImage(frame, 1 / 255.0, new Size(BlobSize, BlobSize),
            new Scalar(), swapRB: true, crop: false);
        _yolo!.SetInput(blob);
        var outs = _yoloOutputs.Select(_ => new Mat()).ToArray();
        try
        {
            _yolo.Forward(outs, _yoloOutputs);
            foreach (var output in outs)
            {
                int rows = output.Rows, cols = output.Cols;
                var row = new float[cols];
                for (var r = 0; r < rows; r++)
                {
                    Marshal.Copy(output.Ptr(r), row, 0, cols);
                    var confidence = row[5 + _personClassId];
                    if (confidence < PersonConfidence) continue;
                    var cx = row[0] * FrameWidth;
                    var cy = row[1] * FrameHeight;
                    var w = row[2] * FrameWidth;
                    var h = row[3] * FrameHeight;
                    boxes.Add(new Rect((int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h));
                    scores.Add(confidence);
                }
            }
        }
        finally { foreach (var o in outs) o.Dispose(); }

        if (boxes.Count == 0) return [];
        CvDnn.NMSBoxes(boxes, scores, PersonConfidence, NmsThreshold, out int[] keep);
        return keep.Select(i => boxes[i]).Where(b => b.Width * b.Height >= MinBoxArea).ToList();
    }

    private List<Rect> DetectPeopleFallback(Mat frame)
    {
        var found = new List<Rect>();
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);
        try { if (_hog is not null) found.AddRange(_hog.DetectMultiScale(frame, 0, new Size(8, 8), new Size(8, 8), 1.05, 2)); }
        catch (Exception ex) { _log.Error("HOG detection failed", ex); }
        try { if (_upperBody is not null) found.AddRange(_upperBody.DetectMultiScale(gray, 1.1, 3, HaarDetectionTypes.ScaleImage, new Size(40, 40))); }
        catch { }
        // Faces imply people too, in degraded mode.
        if (_face is not null)
            try { found.AddRange(_face.DetectMultiScale(gray, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(36, 36))); }
            catch { }
        return DedupeByIou(found.Where(b => b.Width * b.Height >= MinBoxArea).ToList());
    }

    private Rect[] DetectFaces(Mat frame)
    {
        if (_face is null) return [];
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);
            return _face.DetectMultiScale(gray, 1.1, 5, HaarDetectionTypes.ScaleImage, new Size(40, 40));
        }
        catch (Exception ex) { _log.Error("Face detection failed", ex); return []; }
    }

    private float[]? EmbedFace(Mat frame, Rect faceRect)
    {
        if (_sface is null) return null;
        try
        {
            var clamped = faceRect & new Rect(0, 0, frame.Width, frame.Height);
            if (clamped.Width < 20 || clamped.Height < 20) return null;
            using var face = new Mat(frame, clamped);
            using var resized = new Mat();
            Cv2.Resize(face, resized, new Size(112, 112));
            using var blob = CvDnn.BlobFromImage(resized, 1.0, new Size(112, 112), new Scalar(), swapRB: false, crop: false);
            _sface.SetInput(blob);
            using var output = _sface.Forward();
            var embedding = new float[EmbeddingSize];
            Marshal.Copy(output.Ptr(0), embedding, 0, EmbeddingSize);
            Normalize(embedding);
            return embedding;
        }
        catch (Exception ex) { _log.Error("Face embedding failed", ex); return null; }
    }

    // Owner face -> person index: prefer the box that contains the face centre, else the nearest box.
    private static int AssociateFace(Rect face, List<Rect> people)
    {
        var center = new Point(face.X + face.Width / 2, face.Y + face.Height / 2);
        for (var i = 0; i < people.Count; i++)
            if (people[i].Contains(center)) return i;

        var best = -1;
        double bestDist = double.MaxValue;
        for (var i = 0; i < people.Count; i++)
        {
            var pc = new Point(people[i].X + people[i].Width / 2, people[i].Y + people[i].Height / 2);
            var dist = Math.Pow(pc.X - center.X, 2) + Math.Pow(pc.Y - center.Y, 2);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    private static List<Rect> DedupeByIou(List<Rect> boxes)
    {
        var ordered = boxes.OrderByDescending(b => b.Width * b.Height).ToList();
        var kept = new List<Rect>();
        foreach (var b in ordered)
            if (!kept.Any(k => Iou(k, b) > 0.45 || Contains(k, b)))
                kept.Add(b);
        return kept;
    }

    private static bool Contains(Rect outer, Rect inner)
    {
        var i = outer & inner;
        return i.Width * i.Height >= 0.8 * inner.Width * inner.Height;
    }

    private static double Iou(Rect a, Rect b)
    {
        var inter = (a & b);
        var interArea = (double)inter.Width * inter.Height;
        if (interArea <= 0) return 0;
        return interArea / (a.Width * (double)a.Height + b.Width * (double)b.Height - interArea);
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm < 1e-6) return;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    private static float Cosine(float[] a, float[] b)
    {
        // Both are L2-normalised, so cosine == dot product.
        var n = Math.Min(a.Length, b.Length);
        float dot = 0;
        for (var i = 0; i < n; i++) dot += a[i] * b[i];
        return dot;
    }

    public void Dispose()
    {
        _capture?.Release();
        _capture?.Dispose();
        _yolo?.Dispose();
        _hog?.Dispose();
        _upperBody?.Dispose();
        _face?.Dispose();
        _sface?.Dispose();
    }
}
