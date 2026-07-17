using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace CasaTimo.Camera.Pipeline;

/// <summary>Un oggetto confermato da YOLO e attualmente seguito da un tracker CSRT,
/// senza richiamare YOLO ad ogni frame. Il track viene rimosso solo quando il
/// tracker lo perde per troppi frame consecutivi (vedi TrackManager.MissLimit).</summary>
public class Track
{
    public int Id { get; }
    public string Label { get; set; }
    public float Confidence { get; set; }
    public Rect2d BBox { get; set; }
    public int MissCount { get; set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    private readonly TrackerCSRT _tracker;

    public Track(int id, Mat frame, Rect box, string label, float confidence)
    {
        Id = id;
        Label = label;
        Confidence = confidence;
        var box2d = new Rect2d(box.X, box.Y, box.Width, box.Height);
        BBox = box2d;
        _tracker = TrackerCSRT.Create();
        _tracker.Init(frame, box2d);
    }

    /// <summary>Aggiorna la posizione del track sul frame corrente. Ritorna false
    /// se il tracker non riesce più a localizzare l'oggetto (miss).</summary>
    public bool Update(Mat frame)
    {
        var box = BBox;
        var ok = _tracker.Update(frame, ref box);
        if (ok)
        {
            BBox = box;
            MissCount = 0;
        }
        else
        {
            MissCount++;
        }
        return ok;
    }

    public Rect BBoxAsRect() => new(
        (int)Math.Round(BBox.X), (int)Math.Round(BBox.Y),
        (int)Math.Round(BBox.Width), (int)Math.Round(BBox.Height));
}
