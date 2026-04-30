using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace PrimeDictate;

internal sealed record WhisperQnnArtifacts(
    string ModelDirectory,
    string EncoderPath,
    string DecoderPath,
    string TokensPath,
    bool UsingQnnArtifacts)
{
    public string Describe() =>
        $"modelDirectory={this.ModelDirectory}, encoder={Path.GetFileName(this.EncoderPath)}, decoder={Path.GetFileName(this.DecoderPath)}, tokens={Path.GetFileName(this.TokensPath)}, qnnArtifacts={this.UsingQnnArtifacts}";
}

internal static class QualcommQnnWhisperValidationHarness
{
    public static string RunBackendSmokeValidation(string modelDirectory, string computeInterface)
    {
        var requestedRuntime = string.Equals(computeInterface, TranscriptionComputeInterface.Npu.ToString(), StringComparison.OrdinalIgnoreCase)
            ? QualcommQnnActiveRuntime.QnnHtp
            : QualcommQnnActiveRuntime.Cpu;
        var strictValidation = requestedRuntime == QualcommQnnActiveRuntime.QnnHtp;
        return RunValidation(modelDirectory, requestedRuntime, strictValidation);
    }

    public static string RunSmokeValidation(string modelDirectory, bool strictValidation)
    {
        return RunValidation(modelDirectory, QualcommQnnActiveRuntime.QnnHtp, strictValidation);
    }

    private static string RunValidation(
        string modelDirectory,
        QualcommQnnActiveRuntime requestedRuntime,
        bool strictValidation)
    {
        var normalizedModelDirectory = Path.GetFullPath(modelDirectory);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "Whisper ONNX QNN Probe",
            ["requestedRuntime"] = requestedRuntime.ToString(),
            ["modelDirectory"] = normalizedModelDirectory,
            ["strictValidation"] = strictValidation,
            ["validationScope"] = "session-creation-only",
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["limitations"] = "This harness validates direct encoder/decoder session creation only. It does not run Whisper feature extraction or autoregressive decoding in managed code."
        };

        try
        {
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(normalizedModelDirectory, strictValidationOverride: strictValidation) with
            {
                EnableContextCache = false
            };
            var artifacts = ResolveArtifacts(normalizedModelDirectory, preferQnnArtifacts: requestedRuntime == QualcommQnnActiveRuntime.QnnHtp);

            using var encoderSession = CreateSession("QnnWhisperEncoder", artifacts.EncoderPath, requestedRuntime, runtimeOptions, normalizedModelDirectory);
            using var decoderSession = CreateSession("QnnWhisperDecoder", artifacts.DecoderPath, requestedRuntime, runtimeOptions, normalizedModelDirectory);

            result["artifacts"] = artifacts.Describe();
            result["encoderSessionCreated"] = true;
            result["decoderSessionCreated"] = true;
            result["encoderInputs"] = encoderSession.InputNames;
            result["encoderOutputs"] = encoderSession.OutputNames;
            result["decoderInputs"] = decoderSession.InputNames;
            result["decoderOutputs"] = decoderSession.OutputNames;
            result["runSucceeded"] = true;
            result["proofStatus"] = requestedRuntime == QualcommQnnActiveRuntime.QnnHtp && strictValidation
                ? "QNN HTP strict validation passed for Whisper encoder/decoder session creation."
                : "Whisper encoder/decoder session creation was not strictly validated on QNN HTP.";
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP strict validation failed for Whisper session creation.";
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static WhisperQnnArtifacts ResolveArtifacts(string modelDirectory, bool preferQnnArtifacts)
    {
        if (!WhisperModelCatalog.TryResolveModelFiles(modelDirectory, out var modelFiles))
        {
            throw new FileNotFoundException(
                "Whisper ONNX model folder is incomplete. Expected encoder, decoder, and tokens files.");
        }

        var files = modelFiles.Value;
        var qnnDirectory = Path.Combine(modelDirectory, "qnn");
        var qnnEncoder = preferQnnArtifacts ? ResolveOptionalQnnArtifact(qnnDirectory, files.Encoder) : null;
        var qnnDecoder = preferQnnArtifacts ? ResolveOptionalQnnArtifact(qnnDirectory, files.Decoder) : null;

        return new WhisperQnnArtifacts(
            modelDirectory,
            qnnEncoder ?? files.Encoder,
            qnnDecoder ?? files.Decoder,
            files.Tokens,
            qnnEncoder is not null || qnnDecoder is not null);
    }

    private static string? ResolveOptionalQnnArtifact(string qnnDirectory, string baseModelPath)
    {
        if (!Directory.Exists(qnnDirectory))
        {
            return null;
        }

        var baseFileName = Path.GetFileName(baseModelPath);
        var candidates = new[]
        {
            baseFileName,
            baseFileName.Replace(".int8.onnx", ".qdq.onnx", StringComparison.OrdinalIgnoreCase),
            baseFileName.Replace(".onnx", ".qdq.onnx", StringComparison.OrdinalIgnoreCase),
            baseFileName.Replace(".onnx", ".qnn.onnx", StringComparison.OrdinalIgnoreCase)
        };

        foreach (var candidateFileName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidatePath = Path.Combine(qnnDirectory, candidateFileName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static InferenceSession CreateSession(
        string sessionName,
        string modelPath,
        QualcommQnnActiveRuntime runtime,
        QualcommQnnRuntimeOptions runtimeOptions,
        string modelDirectory)
    {
        var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
        Directory.CreateDirectory(contextDirectory);
        var contextPath = runtime == QualcommQnnActiveRuntime.QnnHtp
            ? Path.Combine(contextDirectory, $"{sessionName}_{Guid.NewGuid():N}_ctx.onnx")
            : null;

        using var options = QnnRuntimeSupport.CreateSessionOptions(
            runtime,
            runtimeOptions,
            sessionTag: $"PrimeDictate.{sessionName}",
            contextFilePath: contextPath);
        return new InferenceSession(modelPath, options);
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