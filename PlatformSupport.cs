using System.Runtime.InteropServices;

namespace PrimeDictate;

internal static class PlatformSupport
{
    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    public static bool IsWindowsArm64 =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static bool SupportsWhisperNetOpenVino =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public static bool SupportsQualcommQnnHtp => QnnRuntimeSupport.GetAvailability().SupportsQnnHtp;

    public static bool ShouldOfferQualcommQnnBackend => IsWindowsArm64;

    public static string WhisperNetRuntimeSummary => SupportsWhisperNetOpenVino
        ? "Whisper.net GGML can use CPU and OpenVINO on this machine."
        : IsWindowsArm64
            ? "Whisper.net GGML runs natively on ARM64 with CPU. OpenVINO NPU acceleration currently requires an x64 build."
            : $"Whisper.net GGML can use CPU, but OpenVINO acceleration is unavailable on {RuntimeInformation.ProcessArchitecture}.";

    public static string QualcommQnnRuntimeSummary => QnnRuntimeSupport.GetAvailability().Summary;
}