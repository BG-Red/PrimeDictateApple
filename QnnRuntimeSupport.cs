using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace PrimeDictate;

internal enum QualcommQnnActiveRuntime
{
    Cpu = 0,
    QnnHtp = 1
}

internal sealed record QualcommQnnRuntimeOptions(
    bool StrictValidation,
    bool EnableContextCache,
    bool EmbedContextCache,
    string? ProfilingLevel,
    string? ProfilingOutputDirectory,
    string BackendLibraryPath,
    string ProvidersLibraryPath,
    string SystemLibraryPath)
{
    public bool EnableProfiling => !string.IsNullOrWhiteSpace(this.ProfilingLevel) &&
        !string.Equals(this.ProfilingLevel, "off", StringComparison.OrdinalIgnoreCase);
}

internal sealed record QualcommQnnAvailability(
    bool IsArm64Process,
    string Summary,
    string? BackendLibraryPath,
    string? ProvidersLibraryPath,
    string? SystemLibraryPath)
{
    public bool SupportsQnnHtp =>
        this.IsArm64Process &&
        !string.IsNullOrWhiteSpace(this.BackendLibraryPath) &&
        !string.IsNullOrWhiteSpace(this.ProvidersLibraryPath) &&
        !string.IsNullOrWhiteSpace(this.SystemLibraryPath);
}

internal static class QnnRuntimeSupport
{
    private const string QnnProviderName = "QNN";
    private const string QnnBackendLibrary = "QnnHtp.dll";
    private const string QnnProvidersLibrary = "onnxruntime_providers_qnn.dll";
    private const string QnnSystemLibrary = "QnnSystem.dll";
    private const string DisableCpuFallbackKey = "session.disable_cpu_ep_fallback";
    private const string ContextEnableKey = "ep.context_enable";
    private const string ContextFilePathKey = "ep.context_file_path";
    private const string ContextEmbedModeKey = "ep.context_embed_mode";
    private const string StrictValidationEnvVar = "PRIMEDICTATE_QNN_STRICT";
    private const string ContextCacheEnvVar = "PRIMEDICTATE_QNN_CONTEXT_CACHE";
    private const string ContextEmbedEnvVar = "PRIMEDICTATE_QNN_CONTEXT_EMBED";
    private const string ProfilingLevelEnvVar = "PRIMEDICTATE_QNN_PROFILE";
    private const string ProfilingDirectoryEnvVar = "PRIMEDICTATE_QNN_PROFILE_DIR";

    public static QualcommQnnAvailability GetAvailability()
    {
        var isArm64Process = OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        var backendPath = TryResolveRuntimeAsset(QnnBackendLibrary);
        var providersPath = TryResolveRuntimeAsset(QnnProvidersLibrary);
        var systemPath = TryResolveRuntimeAsset(QnnSystemLibrary);

        string summary;
        if (!OperatingSystem.IsWindows())
        {
            summary = "Qualcomm QNN HTP requires Windows.";
        }
        else if (!isArm64Process)
        {
            summary = $"Qualcomm QNN HTP requires a native Windows ARM64 process. Current process architecture: {RuntimeInformation.ProcessArchitecture}.";
        }
        else if (backendPath is null || providersPath is null || systemPath is null)
        {
            summary = "ONNX Runtime QNN native assets are not present in this build. Publish or run the native win-arm64 build to enable Qualcomm QNN HTP.";
        }
        else
        {
            summary = "Qualcomm QNN HTP runtime assets are present. PrimeDictate can attempt a strict NPU path with CPU fallback disabled, and will fall back to CPU only when strict validation is off.";
        }

        return new QualcommQnnAvailability(
            isArm64Process,
            summary,
            backendPath,
            providersPath,
            systemPath);
    }

    public static QualcommQnnRuntimeOptions GetRuntimeOptions(string modelDirectory, bool? strictValidationOverride = null)
    {
        var availability = GetAvailability();
        var diagnosticsDirectory = Path.Combine(modelDirectory, ".qnn-diagnostics");
        Directory.CreateDirectory(diagnosticsDirectory);

        return new QualcommQnnRuntimeOptions(
            StrictValidation: strictValidationOverride ?? GetBooleanEnvironmentVariable(StrictValidationEnvVar, defaultValue: false),
            EnableContextCache: GetBooleanEnvironmentVariable(ContextCacheEnvVar, defaultValue: true),
            EmbedContextCache: GetBooleanEnvironmentVariable(ContextEmbedEnvVar, defaultValue: true),
            ProfilingLevel: GetProfilingLevel(),
            ProfilingOutputDirectory: GetProfilingOutputDirectory(diagnosticsDirectory),
            BackendLibraryPath: availability.BackendLibraryPath ?? QnnBackendLibrary,
            ProvidersLibraryPath: availability.ProvidersLibraryPath ?? QnnProvidersLibrary,
            SystemLibraryPath: availability.SystemLibraryPath ?? QnnSystemLibrary);
    }

    public static SessionOptions CreateSessionOptions(
        QualcommQnnActiveRuntime runtime,
        QualcommQnnRuntimeOptions runtimeOptions,
        string sessionTag,
        string? contextFilePath = null)
    {
        var sessionOptions = new SessionOptions
        {
            LogId = sessionTag,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        if (runtime == QualcommQnnActiveRuntime.QnnHtp)
        {
            sessionOptions.AddSessionConfigEntry(DisableCpuFallbackKey, "1");

            if (runtimeOptions.EnableContextCache && !string.IsNullOrWhiteSpace(contextFilePath))
            {
                sessionOptions.AddSessionConfigEntry(ContextEnableKey, "1");
                sessionOptions.AddSessionConfigEntry(ContextFilePathKey, contextFilePath);
                sessionOptions.AddSessionConfigEntry(ContextEmbedModeKey, runtimeOptions.EmbedContextCache ? "1" : "0");
            }

            sessionOptions.AppendExecutionProvider(QnnProviderName, BuildProviderOptions(runtimeOptions, contextFilePath));
        }
        else
        {
            sessionOptions.AppendExecutionProvider_CPU();
        }

        return sessionOptions;
    }

    public static string DescribeRuntimePlan(
        QualcommQnnActiveRuntime runtime,
        QualcommQnnRuntimeOptions runtimeOptions,
        string? contextFilePath)
    {
        var provider = runtime == QualcommQnnActiveRuntime.QnnHtp ? "QNN HTP" : "CPU";
        var strictValidation = runtime == QualcommQnnActiveRuntime.QnnHtp ? runtimeOptions.StrictValidation : false;
        var profiling = runtimeOptions.EnableProfiling ? runtimeOptions.ProfilingLevel : "off";
        return $"provider={provider}, backendPath={runtimeOptions.BackendLibraryPath}, cpuFallbackDisabled={runtime == QualcommQnnActiveRuntime.QnnHtp}, strictValidation={strictValidation}, contextCache={runtimeOptions.EnableContextCache}, contextFilePath={contextFilePath ?? "<none>"}, contextEmbed={runtimeOptions.EmbedContextCache}, profiling={profiling}";
    }

    public static bool TryCreateValidationSessionOptions(out SessionOptions? sessionOptions, out string diagnostic)
    {
        try
        {
            var runtimeOptions = GetRuntimeOptions(AppContext.BaseDirectory, strictValidationOverride: true);
            sessionOptions = CreateSessionOptions(
                QualcommQnnActiveRuntime.QnnHtp,
                runtimeOptions,
                sessionTag: "PrimeDictate.QNNProbe",
                contextFilePath: null);
            diagnostic = DescribeRuntimePlan(QualcommQnnActiveRuntime.QnnHtp, runtimeOptions, contextFilePath: null);
            return true;
        }
        catch (Exception ex)
        {
            sessionOptions = null;
            diagnostic = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, string> BuildProviderOptions(
        QualcommQnnRuntimeOptions runtimeOptions,
        string? contextFilePath)
    {
        var providerOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["backend_path"] = runtimeOptions.BackendLibraryPath,
            ["htp_performance_mode"] = "default"
        };

        if (runtimeOptions.EnableProfiling)
        {
            providerOptions["profiling_level"] = runtimeOptions.ProfilingLevel!;
            if (!string.IsNullOrWhiteSpace(runtimeOptions.ProfilingOutputDirectory))
            {
                Directory.CreateDirectory(runtimeOptions.ProfilingOutputDirectory);
                var fileName = $"{Path.GetFileNameWithoutExtension(contextFilePath ?? "session")}.csv";
                providerOptions["profiling_file_path"] = Path.Combine(runtimeOptions.ProfilingOutputDirectory, fileName);
            }
        }

        return providerOptions;
    }

    private static string? TryResolveRuntimeAsset(string fileName)
    {
        foreach (var candidateDirectory in EnumerateRuntimeAssetDirectories())
        {
            var candidate = Path.Combine(candidateDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRuntimeAssetDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-arm64", "native");
        yield return Path.Combine(AppContext.BaseDirectory, "win-arm64");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-arm64", "native");
    }

    private static bool GetBooleanEnvironmentVariable(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(raw, out var parsed) ? parsed : defaultValue
        };
    }

    private static string? GetProfilingLevel()
    {
        var raw = Environment.GetEnvironmentVariable(ProfilingLevelEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "basic" => "basic",
            "detailed" => "detailed",
            "optrace" => "optrace",
            _ => null
        };
    }

    private static string GetProfilingOutputDirectory(string defaultDirectory)
    {
        var raw = Environment.GetEnvironmentVariable(ProfilingDirectoryEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultDirectory;
        }

        return Path.GetFullPath(raw);
    }
}