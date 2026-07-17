namespace CasaTimo.Camera.Pipeline;

/// <summary>Le 80 classi COCO nell'ordine standard usato dai modelli YOLO pre-addestrati.</summary>
public static class CocoClasses
{
    public static readonly string[] Names =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
        "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator",
        "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
    ];

    /// <summary>Sottoinsieme rilevante per la videosorveglianza domestica — filtra il rumore
    /// delle altre 70+ classi COCO che non interessano (es. "toaster", "wine glass").</summary>
    public static readonly HashSet<string> SurveillanceRelevant =
    [
        "person", "bicycle", "car", "motorcycle", "bus", "truck", "dog", "cat", "bird"
    ];
}
