using BirdCam.Application.Abstractions;
using BirdCam.Application.Detection;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.Domain.ValueObjects;
using BirdCam.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace BirdCam.Infrastructure.Detection;

/// <summary>
/// Детектор на ONNX Runtime. Поддерживает стандартный экспорт YOLOv8/YOLO11
/// (выход [1, 4 + классы, N]), NMS и опционально DirectML.
/// </summary>
public sealed class OnnxBirdDetector : IDetector, IDisposable
{
    private const float NmsThreshold = 0.45f;

    private readonly ISettingsProvider _settings;
    private readonly StorageLayout _layout;
    private readonly ILogger<OnnxBirdDetector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InferenceSession? _session;
    private string? _inputName;
    private int _inputSize;
    private DenseTensor<float>? _tensor;
    private byte[]? _pixelBytes;
    private string _provider = "CPU";
    private IReadOnlyDictionary<int, string> _classNames = new Dictionary<int, string>();

    public OnnxBirdDetector(ISettingsProvider settings, StorageLayout layout, ILogger<OnnxBirdDetector> logger)
    {
        _settings = settings;
        _layout = layout;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    public string Status { get; private set; } = "не инициализирован";

    public int InputSize => _inputSize;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(InitializeCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DetectedBird>> DetectAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session;
            if (session is null || _inputName is null || _tensor is null)
            {
                return Array.Empty<DetectedBird>();
            }

            return await Task.Run(() => DetectCore(session, frame), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _gate.Dispose();
    }

    private void InitializeCore()
    {
        try
        {
            _session?.Dispose();
            _session = null;
            _tensor = null;
            _pixelBytes = null;
            _classNames = new Dictionary<int, string>();
            IsAvailable = false;
            Status = "загрузка модели…";

            var detection = _settings.Current.Detection;
            var path = ResolveModelPath(detection.ModelPath);

            if (!File.Exists(path))
            {
                Status = $"модель не найдена: {path}";
                _logger.LogWarning("Модель не найдена: {Path}", path);
                return;
            }

            _session = CreateSession(path, detection.UseGpu, out _provider);
            _classNames = ReadClassNames(_session);
            _inputName = _session.InputMetadata.Keys.First();
            var dimensions = _session.InputMetadata[_inputName].Dimensions;
            _inputSize = dimensions.Length >= 3 && dimensions[2] > 0 ? dimensions[2] : detection.InputSize;
            _inputSize = Math.Max(320, _inputSize);

            var pixelCount = _inputSize * _inputSize;
            _tensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });
            _pixelBytes = new byte[pixelCount * 3];

            IsAvailable = true;
            Status = $"готово: {Path.GetFileName(path)}, {_provider}, {_inputSize}px";
            _logger.LogInformation("Модель загружена: {Path} ({Provider}, {Size}px)", path, _provider, _inputSize);
        }
        catch (Exception exception)
        {
            Status = "ошибка модели: " + exception.Message;
            _logger.LogError(exception, "Не удалось загрузить ONNX-модель");
        }
    }

    private InferenceSession CreateSession(string path, bool useGpu, out string provider)
    {
        provider = "CPU";

        if (useGpu)
        {
            try
            {
                using var dmlOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                dmlOptions.AppendExecutionProvider_DML(0);

                var session = new InferenceSession(path, dmlOptions);
                provider = "DirectML";
                return session;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "DirectML недоступен, переключаюсь на CPU");
            }
        }

        using var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        return new InferenceSession(path, cpuOptions);
    }

    private IReadOnlyList<DetectedBird> DetectCore(InferenceSession session, VideoFrame frame)
    {
        var detection = _settings.Current.Detection;
        var threshold = Math.Clamp(detection.ConfidenceThreshold, 0.01d, 0.99d);
        var classIds = detection.ParseClassIds();

        using var source = OpenCvFrameConverter.ToMat(frame);
        var transform = LetterboxTransform.Create(source.Width, source.Height, _inputSize);

        var scaledWidth = (int)MathF.Round(source.Width * transform.Scale);
        var scaledHeight = (int)MathF.Round(source.Height * transform.Scale);

        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(scaledWidth, scaledHeight), 0, 0, InterpolationFlags.Linear);

        using var canvas = new Mat(
            new Size(_inputSize, _inputSize),
            MatType.CV_8UC3,
            Scalar.All(114));

        using (var roi = new Mat(canvas, new Rect(transform.PadX, transform.PadY, scaledWidth, scaledHeight)))
        {
            resized.CopyTo(roi);
        }

        Cv2.CvtColor(canvas, canvas, ColorConversionCodes.BGR2RGB);

        var pixelBytes = _pixelBytes!;
        var pixelCount = _inputSize * _inputSize;
        System.Runtime.InteropServices.Marshal.Copy(canvas.Data, pixelBytes, 0, pixelBytes.Length);

        var span = _tensor!.Buffer.Span;
        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * 3;
            span[i] = pixelBytes[offset] / 255f;
            span[pixelCount + i] = pixelBytes[offset + 1] / 255f;
            span[2 * pixelCount + i] = pixelBytes[offset + 2] / 255f;
        }

        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, _tensor)
        };

        using var results = session.Run(inputs);
        var output = results.FirstOrDefault()?.AsTensor<float>();
        if (output is null)
        {
            return Array.Empty<DetectedBird>();
        }

        if (output is not DenseTensor<float> denseOutput)
        {
            _logger.LogWarning("Неожиданный тип выхода модели: {Type}", output.GetType().Name);
            return Array.Empty<DetectedBird>();
        }

        var dimensions = denseOutput.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1)
        {
            _logger.LogWarning("Неожиданная форма выхода модели: [{Dims}]", string.Join(", ", dimensions));
            return Array.Empty<DetectedBird>();
        }

        var rowMajor = dimensions[1] < dimensions[2];
        var channels = rowMajor ? dimensions[1] : dimensions[2];
        var count = rowMajor ? dimensions[2] : dimensions[1];
        var classes = channels - 4;

        if (classes <= 0 || count <= 0)
        {
            return Array.Empty<DetectedBird>();
        }

        var values = denseOutput.Buffer.Span;
        var candidates = new List<(BoundingBox Box, double Confidence, int ClassId)>();
        for (var index = 0; index < count; index++)
        {
            var bestScore = 0f;
            var bestClassId = -1;

            foreach (var classId in classIds)
            {
                if (classId < 0 || classId >= classes)
                {
                    continue;
                }

                var channel = 4 + classId;
                var score = rowMajor
                    ? values[channel * count + index]
                    : values[index * channels + channel];

                if (score > bestScore)
                {
                    bestScore = score;
                    bestClassId = classId;
                }
            }

            if (bestClassId < 0 || bestScore < threshold)
            {
                continue;
            }

            var centerX = rowMajor ? values[index] : values[index * channels];
            var centerY = rowMajor ? values[count + index] : values[index * channels + 1];
            var boxWidth = rowMajor ? values[2 * count + index] : values[index * channels + 2];
            var boxHeight = rowMajor ? values[3 * count + index] : values[index * channels + 3];

            if (boxWidth <= 0 || boxHeight <= 0)
            {
                continue;
            }

            var box = transform.ToSource(centerX, centerY, boxWidth, boxHeight);
            if (box.Width * box.Height < 0.00005d)
            {
                continue;
            }

            candidates.Add((box, bestScore, bestClassId));
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<DetectedBird>();
        }

        var boxes = candidates.Select(candidate => candidate.Box).ToList();
        var scores = candidates.Select(candidate => candidate.Confidence).ToList();
        var kept = Nms.Select(boxes, scores, NmsThreshold);

        return kept
            .Select(index =>
            {
                var candidate = candidates[index];
                return new DetectedBird(
                    ResolveLabel(candidate.ClassId),
                    candidate.Confidence,
                    candidate.Box);
            })
            .OrderByDescending(bird => bird.Confidence)
            .ToList();
    }

    private string ResolveLabel(int classId)
    {
        if (!_classNames.TryGetValue(classId, out var name) || string.IsNullOrWhiteSpace(name))
        {
            return "птица";
        }

        name = name.Trim();
        return string.Equals(name, "bird", StringComparison.OrdinalIgnoreCase) ? "птица" : name;
    }

    private static IReadOnlyDictionary<int, string> ReadClassNames(InferenceSession session)
    {
        try
        {
            if (session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names))
            {
                return YoloLabels.ParseNames(names);
            }
        }
        catch (Exception)
        {
            // metadata необязательна
        }

        return new Dictionary<int, string>();
    }

    private string ResolveModelPath(string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        var target = Path.Combine(_layout.Models, configured);
        if (File.Exists(target))
        {
            return target;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "models", configured);
        if (File.Exists(bundled))
        {
            Directory.CreateDirectory(_layout.Models);
            File.Copy(bundled, target, overwrite: false);
            _logger.LogInformation("Модель скопирована в хранилище: {Path}", target);
        }

        return target;
    }
}

