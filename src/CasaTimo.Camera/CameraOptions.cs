namespace CasaTimo.Camera;

public class CameraOptions
{
    public string ModelPath { get; set; } = "models/yolov8n.onnx";
    public string NasBasePath { get; set; } = "/mnt/nas/casatimo/cameras";
    public int RetentionDays { get; set; } = 14;
    public float ConfThreshold { get; set; } = 0.4f;
    public float IouThreshold { get; set; } = 0.45f;
    public int MinMotionArea { get; set; } = 800;
    public int TrackMissLimit { get; set; } = 15;
    public int ReverifyEveryFrames { get; set; } = 10;
    public double SnapshotIntervalSeconds { get; set; } = 0.5; // 2 fps durante un track attivo
    public int StatusPublishIntervalSeconds { get; set; } = 30;
}
