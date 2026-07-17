using OpenCvSharp;

namespace CasaTimo.Camera.Pipeline;

/// <summary>
/// Motion filter basato su background subtractor MOG2. Individua le regioni
/// dell'immagine cambiate rispetto al modello di sfondo e le raggruppa in
/// bounding box "grezze" — quelle finestre vengono poi passate a YOLO per
/// la conferma (persona/veicolo/animale) invece di fidarsi del solo motion.
/// </summary>
public class MotionDetector : IDisposable
{
    private readonly BackgroundSubtractorMOG2 _mog2;
    private readonly int _minBlobArea;
    private readonly int _dilateIterations;

    public MotionDetector(int history = 500, double varThreshold = 16, int minBlobArea = 800, int dilateIterations = 2)
    {
        _mog2 = BackgroundSubtractorMOG2.Create(history: history, varThreshold: varThreshold, detectShadows: true);
        _minBlobArea = minBlobArea;
        _dilateIterations = dilateIterations;
    }

    /// <summary>
    /// Applica il motion filter al frame e ritorna le bounding box (in coordinate
    /// del frame) delle regioni con movimento, già unite se sovrapposte/vicine.
    /// </summary>
    public List<Rect> Detect(Mat frame)
    {
        using var fgMask = new Mat();
        // learningRate negativo = automatico (default MOG2)
        _mog2.Apply(frame, fgMask, -1);

        // I pixel "ombra" vengono marcati da MOG2 con valore 127: li scartiamo,
        // teniamo solo il vero foreground (255).
        using var fgMaskNoShadow = new Mat();
        Cv2.Threshold(fgMask, fgMaskNoShadow, 250, 255, ThresholdTypes.Binary);

        // Chiusura morfologica: salda blob vicini (es. braccia/gambe separate dal
        // rumore) e rimuove puntini isolati.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        using var cleaned = new Mat();
        Cv2.Dilate(fgMaskNoShadow, cleaned, kernel, iterations: _dilateIterations);

        Cv2.FindContours(cleaned, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var boxes = contours
            .Select(Cv2.BoundingRect)
            .Where(r => r.Width * r.Height >= _minBlobArea)
            .ToList();

        return MergeOverlapping(boxes, frame.Width, frame.Height);
    }

    /// <summary>Unisce bounding box sovrapposte o molto vicine (evita di generare
    /// N finestre YOLO separate per lo stesso oggetto in movimento) e le allarga
    /// leggermente per dare un po' di contesto attorno al blob.</summary>
    private static List<Rect> MergeOverlapping(List<Rect> boxes, int frameWidth, int frameHeight, int padding = 15)
    {
        var padded = boxes.Select(b => Inflate(b, padding, frameWidth, frameHeight)).ToList();

        bool mergedAny;
        do
        {
            mergedAny = false;
            for (var i = 0; i < padded.Count && !mergedAny; i++)
            {
                for (var j = i + 1; j < padded.Count; j++)
                {
                    if (!Overlaps(padded[i], padded[j])) continue;
                    padded[i] = padded[i] | padded[j]; // union dei due rettangoli
                    padded.RemoveAt(j);
                    mergedAny = true;
                    break;
                }
            }
        } while (mergedAny);

        return padded;
    }

    private static bool Overlaps(Rect a, Rect b) => (a & b).Width > 0 && (a & b).Height > 0;

    private static Rect Inflate(Rect r, int padding, int frameWidth, int frameHeight)
    {
        var x = Math.Max(0, r.X - padding);
        var y = Math.Max(0, r.Y - padding);
        var right = Math.Min(frameWidth, r.X + r.Width + padding);
        var bottom = Math.Min(frameHeight, r.Y + r.Height + padding);
        return new Rect(x, y, right - x, bottom - y);
    }

    public void Dispose() => _mog2.Dispose();
}
