using OpenCvSharp;

namespace CasaTimo.Camera.Pipeline;

public class TrackManager
{
    private readonly List<Track> _tracks = [];
    private readonly int _missLimit;
    private readonly float _iouMatchThreshold;
    private int _nextId = 1;

    public IReadOnlyList<Track> Tracks => _tracks;

    public TrackManager(int missLimit = 15, float iouMatchThreshold = 0.3f)
    {
        _missLimit = missLimit;
        _iouMatchThreshold = iouMatchThreshold;
    }

    /// <summary>Aggiorna tutti i track esistenti sul frame corrente (CSRT.Update,
    /// nessuna chiamata a YOLO). Rimuove i track persi da troppi frame consecutivi.
    /// Ritorna gli id dei track appena persi in questo giro, per loggare/chiudere
    /// l'evento MQTT corrispondente.</summary>
    public List<int> UpdateAll(Mat frame)
    {
        var lost = new List<int>();
        foreach (var track in _tracks)
        {
            track.Update(frame);
        }
        var toRemove = _tracks.Where(t => t.MissCount > _missLimit).ToList();
        foreach (var t in toRemove)
        {
            lost.Add(t.Id);
            _tracks.Remove(t);
        }
        return lost;
    }

    /// <summary>Vero se la box è già coperta da un track attivo (evita di far
    /// analizzare da YOLO una finestra di motion che è solo l'oggetto già tracciato
    /// che continua a muoversi).</summary>
    public bool IsCoveredByExistingTrack(Rect motionBox)
    {
        return _tracks.Any(t => Iou(t.BBoxAsRect(), motionBox) > _iouMatchThreshold);
    }

    /// <summary>Registra un nuovo track a partire da una detection YOLO confermata.</summary>
    public Track StartTrack(Mat frame, Detection detection)
    {
        var track = new Track(_nextId++, frame, detection.Box, detection.Label, detection.Confidence);
        _tracks.Add(track);
        return track;
    }

    /// <summary>Prova ad associare una detection di ri-verifica a un track esistente
    /// (per aggiornarne l'etichetta/confidenza); ritorna true se trovato un match.</summary>
    public bool TryReconcile(Track track, Detection? confirmedDetection)
    {
        if (confirmedDetection is null)
        {
            track.MissCount++;
            return false;
        }
        track.Label = confirmedDetection.Label;
        track.Confidence = confirmedDetection.Confidence;
        track.MissCount = 0;
        return true;
    }

    private static float Iou(Rect a, Rect b)
    {
        var intersection = a & b;
        var interArea = intersection.Width * intersection.Height;
        if (interArea <= 0) return 0f;
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea <= 0 ? 0f : (float)interArea / unionArea;
    }
}
