using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace CasaTimo.Camera.Pipeline;

public record Detection(string Label, float Confidence, Rect Box);

/// <summary>
/// Wrapper per l'inferenza YOLOv8 via ONNX Runtime. Assume l'export ONNX
/// standard di ultralytics (`yolo export model=yolov8n.pt format=onnx`):
/// input NCHW [1,3,H,W] float32 RGB normalizzato 0-1, output [1, 4+classi, N]
/// (box cx,cy,w,h + score per classe, N anchor points) — NON verificato contro
/// un modello reale in questo ambiente (niente dotnet qui). Se il tuo export ha
/// un layout diverso (es. NMS incorporato con `--nms`, o output già trasposto),
/// va adattato `Postprocess` di conseguenza: ispeziona `session.OutputMetadata`
/// e la forma del tensore risultante al primo giro con un breakpoint/log.
/// </summary>
public class YoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly float _confThreshold;
    private readonly float _iouThreshold;

    public YoloDetector(string modelPath, float confThreshold = 0.4f, float iouThreshold = 0.45f)
    {
        _session = new InferenceSession(modelPath);
        _confThreshold = confThreshold;
        _iouThreshold = iouThreshold;

        var inputMeta = _session.InputMetadata.First();
        _inputName = inputMeta.Key;
        var dims = inputMeta.Value.Dimensions; // atteso [1,3,H,W]
        _inputHeight = dims.Length == 4 && dims[2] > 0 ? dims[2] : 640;
        _inputWidth = dims.Length == 4 && dims[3] > 0 ? dims[3] : 640;
    }

    /// <summary>Esegue l'inferenza su un ritaglio del frame (crop attorno a una
    /// finestra in movimento, o il frame intero) e ritorna le detection rilevanti
    /// per la sorveglianza, con le box riportate in coordinate del crop originale.</summary>
    public List<Detection> Detect(Mat crop)
    {
        var (tensor, scale, padX, padY) = Letterbox(crop);

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        return Postprocess(output, scale, padX, padY, crop.Width, crop.Height);
    }

    /// <summary>Ridimensiona il crop a (input x input) mantenendo l'aspect ratio,
    /// con padding grigio (114,114,114, convenzione ultralytics) sui lati corti,
    /// e lo converte in tensore NCHW RGB [0,1].</summary>
    private (DenseTensor<float> tensor, float scale, int padX, int padY) Letterbox(Mat crop)
    {
        var scale = Math.Min((float)_inputWidth / crop.Width, (float)_inputHeight / crop.Height);
        var newW = (int)Math.Round(crop.Width * scale);
        var newH = (int)Math.Round(crop.Height * scale);
        var padX = (_inputWidth - newW) / 2;
        var padY = (_inputHeight - newH) / 2;

        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(newW, newH), interpolation: InterpolationFlags.Linear);

        using var canvas = new Mat(_inputHeight, _inputWidth, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using (var roi = new Mat(canvas, new Rect(padX, padY, newW, newH)))
        {
            resized.CopyTo(roi);
        }

        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new DenseTensor<float>([1, 3, _inputHeight, _inputWidth]);
        for (var y = 0; y < _inputHeight; y++)
        {
            for (var x = 0; x < _inputWidth; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = pixel.Item0 / 255f;
                tensor[0, 1, y, x] = pixel.Item1 / 255f;
                tensor[0, 2, y, x] = pixel.Item2 / 255f;
            }
        }

        return (tensor, scale, padX, padY);
    }

    private List<Detection> Postprocess(Tensor<float> output, float scale, int padX, int padY, int cropWidth, int cropHeight)
    {
        // Output atteso: [1, 4+numClasses, numAnchors] (channels-first, layout
        // ultralytics standard) oppure [1, numAnchors, 4+numClasses] se l'export
        // è stato fatto con opzioni diverse. Rileviamo l'orientamento guardando
        // quale dimensione è compatibile con 4+80=84 (o comunque piccola).
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3) throw new InvalidOperationException(
            $"Output YOLO con {dims.Length} dimensioni, attese 3 — verifica l'export ONNX del modello.");

        int numAttrs, numAnchors;
        bool channelsFirst;
        if (dims[1] < dims[2]) { numAttrs = dims[1]; numAnchors = dims[2]; channelsFirst = true; }
        else { numAnchors = dims[1]; numAttrs = dims[2]; channelsFirst = false; }

        var numClasses = numAttrs - 4;
        if (numClasses <= 0 || numClasses > CocoClasses.Names.Length)
            throw new InvalidOperationException(
                $"Numero di classi dedotto dall'output ({numClasses}) non torna con le {CocoClasses.Names.Length} classi COCO attese.");

        float At(int attr, int anchor) => channelsFirst ? output[0, attr, anchor] : output[0, anchor, attr];

        var candidates = new List<Detection>();
        for (var a = 0; a < numAnchors; a++)
        {
            var bestScore = 0f;
            var bestClass = -1;
            for (var c = 0; c < numClasses; c++)
            {
                var score = At(4 + c, a);
                if (score > bestScore) { bestScore = score; bestClass = c; }
            }
            if (bestScore < _confThreshold || bestClass < 0) continue;

            var label = CocoClasses.Names[bestClass];
            if (!CocoClasses.SurveillanceRelevant.Contains(label)) continue;

            var cx = At(0, a);
            var cy = At(1, a);
            var w = At(2, a);
            var h = At(3, a);

            // Da coordinate modello (letterboxed _inputWidth x _inputHeight) a coordinate del crop originale
            var x1 = (cx - w / 2 - padX) / scale;
            var y1 = (cy - h / 2 - padY) / scale;
            var x2 = (cx + w / 2 - padX) / scale;
            var y2 = (cy + h / 2 - padY) / scale;

            x1 = Math.Clamp(x1, 0, cropWidth);
            y1 = Math.Clamp(y1, 0, cropHeight);
            x2 = Math.Clamp(x2, 0, cropWidth);
            y2 = Math.Clamp(y2, 0, cropHeight);
            if (x2 <= x1 || y2 <= y1) continue;

            var box = new Rect((int)x1, (int)y1, (int)(x2 - x1), (int)(y2 - y1));
            candidates.Add(new Detection(label, bestScore, box));
        }

        return Nms(candidates, _iouThreshold);
    }

    /// <summary>Non-Maximum Suppression greedy per classe: tiene la detection con
    /// confidenza più alta e scarta le altre che si sovrappongono troppo (stesso
    /// oggetto rilevato più volte).</summary>
    private static List<Detection> Nms(List<Detection> detections, float iouThreshold)
    {
        var result = new List<Detection>();
        foreach (var group in detections.GroupBy(d => d.Label))
        {
            var remaining = group.OrderByDescending(d => d.Confidence).ToList();
            while (remaining.Count > 0)
            {
                var best = remaining[0];
                result.Add(best);
                remaining.RemoveAt(0);
                remaining.RemoveAll(d => Iou(best.Box, d.Box) > iouThreshold);
            }
        }
        return result;
    }

    private static float Iou(Rect a, Rect b)
    {
        var intersection = a & b;
        var interArea = intersection.Width * intersection.Height;
        if (interArea <= 0) return 0f;
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea <= 0 ? 0f : (float)interArea / unionArea;
    }

    public void Dispose() => _session.Dispose();
}
