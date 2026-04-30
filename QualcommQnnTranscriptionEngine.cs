using System.IO;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PrimeDictate;

internal sealed class QualcommQnnTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim syncRoot = new(1, 1);
    private MoonshineOrtTranscriber? transcriber;
    private string? loadedModelDirectory;
    private TranscriptionComputeInterface? loadedComputeInterface;
    private bool loadedStrictValidation;

    public TranscriptionBackendKind Backend => TranscriptionBackendKind.QualcommQnn;

    public string Name => "Qualcomm QNN (Experimental)";

    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        TranscriptionEngineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sampleCount = TranscriptionAudio.GetPcm16MonoSampleCount(audio, this.Name);
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await this.syncRoot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = new float[sampleCount];
            TranscriptionAudio.CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

            var transcriber = this.EnsureTranscriber(configuration);
            try
            {
                return await Task.Run(() => transcriber.Transcribe(samples), cancellationToken).ConfigureAwait(false);
            }
            catch (OnnxRuntimeException ex) when (transcriber.ActiveRuntime == QualcommQnnActiveRuntime.QnnHtp && !transcriber.StrictValidation)
            {
                AppLog.Info(
                    $"Qualcomm QNN runtime failed during inference and will fall back to CPU ORT. {transcriber.DiagnosticsSummary}. Error: {ex.Message}");

                this.ResetTranscriber();
                var cpuTranscriber = MoonshineOrtTranscriber.Create(
                    transcriber.ModelDirectory,
                    QualcommQnnActiveRuntime.Cpu,
                    transcriber.RuntimeOptions,
                    preferQnnArtifacts: false);
                this.AssignLoadedTranscriber(cpuTranscriber, transcriber.ModelDirectory, configuration.ComputeInterface, transcriber.StrictValidation);
                return await Task.Run(() => cpuTranscriber.Transcribe(samples), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            this.syncRoot.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.syncRoot.WaitAsync().ConfigureAwait(false);
        try
        {
            this.ResetTranscriber();
        }
        finally
        {
            this.syncRoot.Release();
            this.syncRoot.Dispose();
        }
    }

    private MoonshineOrtTranscriber EnsureTranscriber(TranscriptionEngineConfiguration configuration)
    {
        var modelDirectory = ResolveModelDirectoryOrThrow(configuration);
        var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory);
        var normalizedCompute = NormalizeCompute(configuration.ComputeInterface);

        if (this.transcriber is not null &&
            string.Equals(this.loadedModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase) &&
            this.loadedComputeInterface == normalizedCompute &&
            this.loadedStrictValidation == runtimeOptions.StrictValidation)
        {
            return this.transcriber;
        }

        this.ResetTranscriber();

        AppLog.Info($"Loaded transcription backend: {this.Name} from {modelDirectory}");

        MoonshineOrtTranscriber created;
        if (normalizedCompute == TranscriptionComputeInterface.Npu)
        {
            try
            {
                created = MoonshineOrtTranscriber.Create(
                    modelDirectory,
                    QualcommQnnActiveRuntime.QnnHtp,
                    runtimeOptions,
                    preferQnnArtifacts: true);
            }
            catch (Exception ex) when (!runtimeOptions.StrictValidation)
            {
                AppLog.Info(
                    $"Qualcomm QNN strict session creation failed and will fall back to CPU ORT. Error: {ex.Message}");
                created = MoonshineOrtTranscriber.Create(
                    modelDirectory,
                    QualcommQnnActiveRuntime.Cpu,
                    runtimeOptions,
                    preferQnnArtifacts: false);
            }
        }
        else
        {
            created = MoonshineOrtTranscriber.Create(
                modelDirectory,
                QualcommQnnActiveRuntime.Cpu,
                runtimeOptions,
                preferQnnArtifacts: false);
        }

        this.AssignLoadedTranscriber(created, modelDirectory, normalizedCompute, runtimeOptions.StrictValidation);
        return created;
    }

    private void AssignLoadedTranscriber(
        MoonshineOrtTranscriber created,
        string modelDirectory,
        TranscriptionComputeInterface computeInterface,
        bool strictValidation)
    {
        this.transcriber = created;
        this.loadedModelDirectory = modelDirectory;
        this.loadedComputeInterface = computeInterface;
        this.loadedStrictValidation = strictValidation;
        AppLog.Info($"Qualcomm QNN runtime diagnostics: {created.DiagnosticsSummary}");
    }

    private void ResetTranscriber()
    {
        this.transcriber?.Dispose();
        this.transcriber = null;
        this.loadedModelDirectory = null;
        this.loadedComputeInterface = null;
        this.loadedStrictValidation = false;
    }

    private static string ResolveModelDirectoryOrThrow(TranscriptionEngineConfiguration configuration)
    {
        if (MoonshineModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Qualcomm QNN expects a Moonshine model folder containing preprocess.onnx, encode.int8.onnx, uncached_decode.int8.onnx, cached_decode.int8.onnx, and tokens.txt.");
        }

        if (MoonshineModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            MoonshineModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in MoonshineModelCatalog.Options)
        {
            if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Qualcomm QNN requires a Moonshine model folder. Download the managed Moonshine model or browse to an extracted Moonshine folder before using this backend.");
    }

    private static TranscriptionComputeInterface NormalizeCompute(TranscriptionComputeInterface computeInterface)
    {
        if (computeInterface == TranscriptionComputeInterface.Gpu)
        {
            AppLog.Info("Qualcomm QNN backend does not expose a GPU path. Falling back to CPU ORT for this session.");
            return TranscriptionComputeInterface.Cpu;
        }

        return computeInterface;
    }
}

internal sealed record MoonshineOrtArtifacts(
    string ModelDirectory,
    string PreprocessPath,
    string EncodePath,
    string UncachedDecodePath,
    string CachedDecodePath,
    string TokensPath,
    bool UsingQnnArtifacts)
{
    public string Describe() =>
        $"modelDirectory={this.ModelDirectory}, preprocess={Path.GetFileName(this.PreprocessPath)}, encode={Path.GetFileName(this.EncodePath)}, uncachedDecode={Path.GetFileName(this.UncachedDecodePath)}, cachedDecode={Path.GetFileName(this.CachedDecodePath)}, qnnArtifacts={this.UsingQnnArtifacts}";
}

internal sealed class MoonshineOrtTranscriber : IDisposable
{
    private readonly InferenceSession preprocessSession;
    private readonly InferenceSession encodeSession;
    private readonly InferenceSession uncachedDecodeSession;
    private readonly InferenceSession cachedDecodeSession;
    private readonly MoonshineTokenizer tokenizer;
    private readonly MoonshineOrtArtifacts artifacts;

    private MoonshineOrtTranscriber(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        MoonshineOrtArtifacts artifacts,
        InferenceSession preprocessSession,
        InferenceSession encodeSession,
        InferenceSession uncachedDecodeSession,
        InferenceSession cachedDecodeSession,
        MoonshineTokenizer tokenizer)
    {
        this.ModelDirectory = modelDirectory;
        this.ActiveRuntime = activeRuntime;
        this.RuntimeOptions = runtimeOptions;
        this.artifacts = artifacts;
        this.preprocessSession = preprocessSession;
        this.encodeSession = encodeSession;
        this.uncachedDecodeSession = uncachedDecodeSession;
        this.cachedDecodeSession = cachedDecodeSession;
        this.tokenizer = tokenizer;
    }

    public string ModelDirectory { get; }

    public QualcommQnnActiveRuntime ActiveRuntime { get; }

    public QualcommQnnRuntimeOptions RuntimeOptions { get; }

    public bool StrictValidation => this.RuntimeOptions.StrictValidation;

    public string DiagnosticsSummary =>
        $"activeRuntime={this.ActiveRuntime}, artifacts=({this.artifacts.Describe()}), runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(this.ActiveRuntime, this.RuntimeOptions, contextFilePath: "<per-session>")})";

    public static MoonshineOrtTranscriber Create(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        bool preferQnnArtifacts)
    {
        var artifacts = ResolveArtifacts(modelDirectory, preferQnnArtifacts);
        var tokenizer = MoonshineTokenizer.Load(artifacts.TokensPath);

        InferenceSession CreateSession(string sessionName, string modelPath)
        {
            var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
            Directory.CreateDirectory(contextDirectory);
            var contextPath = activeRuntime == QualcommQnnActiveRuntime.QnnHtp
                ? Path.Combine(contextDirectory, $"{sessionName}_ctx.onnx")
                : null;

            using var options = QnnRuntimeSupport.CreateSessionOptions(
                activeRuntime,
                runtimeOptions,
                sessionTag: $"PrimeDictate.{sessionName}",
                contextFilePath: contextPath);
            return new InferenceSession(modelPath, options);
        }

        return new MoonshineOrtTranscriber(
            modelDirectory,
            activeRuntime,
            runtimeOptions,
            artifacts,
            CreateSession("QnnMoonshinePreprocess", artifacts.PreprocessPath),
            CreateSession("QnnMoonshineEncode", artifacts.EncodePath),
            CreateSession("QnnMoonshineUncachedDecode", artifacts.UncachedDecodePath),
            CreateSession("QnnMoonshineCachedDecode", artifacts.CachedDecodePath),
            tokenizer);
    }

    public string Transcribe(float[] samples)
    {
        var audioTensor = CreateTensor(samples, 1, samples.Length);
        var features = RunSingleOutput(
            this.preprocessSession,
            NamedOnnxValue.CreateFromTensor(this.preprocessSession.InputNames[0], audioTensor));
        var featuresLength = features.Dimensions[1];
        var featuresLengthTensor = CreateTensor(new[] { featuresLength }, 1);
        var encoderOut = RunSingleOutput(
            this.encodeSession,
            NamedOnnxValue.CreateFromTensor(this.encodeSession.InputNames[0], features),
            NamedOnnxValue.CreateFromTensor(this.encodeSession.InputNames[1], featuresLengthTensor));

        var tokens = new List<int>();
        var seqLen = 1;
        var currentToken = this.tokenizer.SosTokenId;

        var decodeResult = RunDecoder(this.uncachedDecodeSession, currentToken, seqLen, encoderOut, states: null);
        var maxLen = Math.Max(1, (int)Math.Ceiling(encoderOut.Dimensions[1] * 384d / 16_000d * 6d));

        for (var i = 0; i < maxLen; i++)
        {
            var nextToken = ArgMax(decodeResult.Logits);
            if (nextToken == this.tokenizer.EosTokenId)
            {
                break;
            }

            tokens.Add(nextToken);
            seqLen += 1;
            decodeResult = RunDecoder(this.cachedDecodeSession, nextToken, seqLen, encoderOut, decodeResult.States);
        }

        return this.tokenizer.Decode(tokens);
    }

    public void Dispose()
    {
        this.cachedDecodeSession.Dispose();
        this.uncachedDecodeSession.Dispose();
        this.encodeSession.Dispose();
        this.preprocessSession.Dispose();
    }

    private static MoonshineOrtArtifacts ResolveArtifacts(string modelDirectory, bool preferQnnArtifacts)
    {
        var qnnDirectory = Path.Combine(modelDirectory, "qnn");
        var useQnnDirectory = preferQnnArtifacts && Directory.Exists(qnnDirectory);
        var qnnPreprocess = ResolveOptionalQnnArtifact(qnnDirectory, "preprocess.qdq.onnx", "preprocess.qnn.onnx");
        var qnnEncode = ResolveOptionalQnnArtifact(qnnDirectory, "encode.qdq.onnx", "encode.qnn.onnx");
        var qnnUncached = ResolveOptionalQnnArtifact(qnnDirectory, "uncached_decode.qdq.onnx", "uncached_decode.qnn.onnx");
        var qnnCached = ResolveOptionalQnnArtifact(qnnDirectory, "cached_decode.qdq.onnx", "cached_decode.qnn.onnx");

        return new MoonshineOrtArtifacts(
            modelDirectory,
            useQnnDirectory && qnnPreprocess is not null ? qnnPreprocess : Path.Combine(modelDirectory, "preprocess.onnx"),
            useQnnDirectory && qnnEncode is not null ? qnnEncode : Path.Combine(modelDirectory, "encode.int8.onnx"),
            useQnnDirectory && qnnUncached is not null ? qnnUncached : Path.Combine(modelDirectory, "uncached_decode.int8.onnx"),
            useQnnDirectory && qnnCached is not null ? qnnCached : Path.Combine(modelDirectory, "cached_decode.int8.onnx"),
            Path.Combine(modelDirectory, "tokens.txt"),
            useQnnDirectory && qnnPreprocess is not null && qnnEncode is not null && qnnUncached is not null && qnnCached is not null);
    }

    private static string? ResolveOptionalQnnArtifact(string qnnDirectory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var candidate = Path.Combine(qnnDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private DecoderStepResult RunDecoder(
        InferenceSession session,
        int token,
        int tokenLength,
        DenseTensor<float> encoderOut,
        IReadOnlyList<DenseTensor<float>>? states)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], CreateTensor(new[] { token }, 1, 1)),
            NamedOnnxValue.CreateFromTensor(session.InputNames[1], encoderOut),
            NamedOnnxValue.CreateFromTensor(session.InputNames[2], CreateTensor(new[] { tokenLength }, 1))
        };

        if (states is not null)
        {
            for (var i = 3; i < session.InputNames.Count && (i - 3) < states.Count; i++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(session.InputNames[i], states[i - 3]));
            }
        }

        using var outputs = session.Run(inputs, session.OutputNames);
        var logits = CopyFloatTensor(outputs[0]);
        var nextStates = new List<DenseTensor<float>>(Math.Max(0, outputs.Count - 1));
        for (var i = 1; i < outputs.Count; i++)
        {
            nextStates.Add(CopyFloatTensor(outputs[i]));
        }

        return new DecoderStepResult(logits, nextStates);
    }

    private static DenseTensor<float> RunSingleOutput(InferenceSession session, params NamedOnnxValue[] inputs)
    {
        using var outputs = session.Run(inputs, [session.OutputNames[0]]);
        return CopyFloatTensor(outputs[0]);
    }

    private static DenseTensor<float> CopyFloatTensor(DisposableNamedOnnxValue value)
    {
        if (value.ElementType != TensorElementType.Float)
        {
            throw new InvalidOperationException($"Expected float tensor output from {value.Name}, but received {value.ElementType}.");
        }

        var tensor = value.AsTensor<float>();
        return new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static DenseTensor<float> CreateTensor(float[] values, params int[] dimensions) =>
        new DenseTensor<float>(values, dimensions);

    private static DenseTensor<int> CreateTensor(int[] values, params int[] dimensions) =>
        new DenseTensor<int>(values, dimensions);

    private static int ArgMax(DenseTensor<float> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty)
        {
            return 0;
        }

        var bestIndex = 0;
        var bestValue = span[0];
        for (var i = 1; i < span.Length; i++)
        {
            if (span[i] > bestValue)
            {
                bestValue = span[i];
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private sealed record DecoderStepResult(DenseTensor<float> Logits, IReadOnlyList<DenseTensor<float>> States);
}

internal sealed class MoonshineTokenizer
{
    private readonly Dictionary<int, string> idToToken;

    private MoonshineTokenizer(Dictionary<int, string> idToToken, int sosTokenId, int eosTokenId)
    {
        this.idToToken = idToToken;
        this.SosTokenId = sosTokenId;
        this.EosTokenId = eosTokenId;
    }

    public int SosTokenId { get; }

    public int EosTokenId { get; }

    public static MoonshineTokenizer Load(string tokensPath)
    {
        var idToToken = new Dictionary<int, string>();
        int? sos = null;
        int? eos = null;

        foreach (var line in File.ReadLines(tokensPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedLine = line.TrimEnd();
            var separatorIndex = trimmedLine.LastIndexOfAny(['\t', ' ']);
            if (separatorIndex <= 0 ||
                !int.TryParse(trimmedLine[(separatorIndex + 1)..].Trim(), out var tokenId))
            {
                continue;
            }

            var token = trimmedLine[..separatorIndex];
            idToToken[tokenId] = token;
            if (string.Equals(token, "<s>", StringComparison.Ordinal))
            {
                sos = tokenId;
            }
            else if (string.Equals(token, "</s>", StringComparison.Ordinal))
            {
                eos = tokenId;
            }
        }

        if (sos is null || eos is null)
        {
            throw new InvalidOperationException("Moonshine tokens.txt is missing the <s> or </s> token required for greedy decoding.");
        }

        return new MoonshineTokenizer(idToToken, sos.Value, eos.Value);
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var pieces = new List<string>();
        foreach (var tokenId in tokenIds)
        {
            if (this.idToToken.TryGetValue(tokenId, out var token))
            {
                pieces.Add(token);
            }
        }

        return string.Concat(pieces).Replace("\u2581", " ", StringComparison.Ordinal).Trim();
    }
}

internal static class QualcommQnnValidationHarness
{
    public static string RunBackendSmokeValidation(string modelDirectory, string computeInterface)
    {
        var normalizedModelDirectory = Path.GetFullPath(modelDirectory);
        var requestedRuntime = string.Equals(computeInterface, TranscriptionComputeInterface.Npu.ToString(), StringComparison.OrdinalIgnoreCase)
            ? QualcommQnnActiveRuntime.QnnHtp
            : QualcommQnnActiveRuntime.Cpu;
        var strictValidation = requestedRuntime == QualcommQnnActiveRuntime.QnnHtp;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "QualcommQnn",
            ["requestedRuntime"] = requestedRuntime.ToString(),
            ["modelDirectory"] = normalizedModelDirectory,
            ["strictValidation"] = strictValidation,
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory
        };

        try
        {
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(normalizedModelDirectory, strictValidationOverride: strictValidation);
            using var transcriber = MoonshineOrtTranscriber.Create(
                normalizedModelDirectory,
                requestedRuntime,
                runtimeOptions,
                preferQnnArtifacts: requestedRuntime == QualcommQnnActiveRuntime.QnnHtp);

            var silence = new float[16_000];
            var transcript = transcriber.Transcribe(silence);
            result["runtime"] = transcriber.ActiveRuntime.ToString();
            result["diagnostics"] = transcriber.DiagnosticsSummary;
            result["runSucceeded"] = true;
            result["transcriptLength"] = transcript.Length;
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string RunSmokeValidation(string modelDirectory, bool strictValidation)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "QualcommQnn",
            ["modelDirectory"] = Path.GetFullPath(modelDirectory),
            ["strictValidation"] = strictValidation,
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory
        };

        try
        {
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory, strictValidationOverride: strictValidation);
            using var transcriber = MoonshineOrtTranscriber.Create(
                modelDirectory,
                QualcommQnnActiveRuntime.QnnHtp,
                runtimeOptions,
                preferQnnArtifacts: true);

            result["runtime"] = transcriber.ActiveRuntime.ToString();
            result["diagnostics"] = transcriber.DiagnosticsSummary;

            var silence = new float[16_000];
            var transcript = transcriber.Transcribe(silence);
            result["runSucceeded"] = true;
            result["transcriptLength"] = transcript.Length;
            result["proofStatus"] = transcriber.ActiveRuntime == QualcommQnnActiveRuntime.QnnHtp && strictValidation
                ? "QNN HTP strict validation passed for session creation and inference run."
                : "QNN HTP was not strictly validated.";
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP strict validation failed.";
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<string> CaptureRuntimeModuleSnapshot()
    {
        try
        {
            var snapshot = new List<string>();
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (!module.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                    && !module.ModuleName.Contains("qnn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                snapshot.Add($"{module.ModuleName} => {module.FileName}");
            }

            snapshot.Sort(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
        catch (Exception ex)
        {
            return new[] { $"<module snapshot unavailable>: {ex.Message}" };
        }
    }
}
