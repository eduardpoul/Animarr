namespace Animarr.Shared.Models;

/// <summary>
/// Status of one hardware encoder family (VAAPI / NVENC / QSV). Used by the
/// Settings → Hardware tab to tell the user why a backend is or isn't
/// available, with vendor + driver hints to debug Docker passthrough.
/// </summary>
public sealed record EncoderStatusDto(
    bool Available,
    string? Vendor,       // "AMD" / "Intel" / "NVIDIA"
    string? DeviceName,
    string? DriverInfo,
    string? Detail);

/// <summary>
/// Result of the host-capability probe performed at app startup. The
/// Settings → Hardware tab subscribes to changes via a manual rescan; this
/// DTO is the single source of truth the UI consumes.
/// </summary>
public sealed record HardwareReportDto(
    EncoderStatusDto Vaapi,
    EncoderStatusDto Nvenc,
    EncoderStatusDto Qsv,
    string[] HwAccels,
    string[] HwEncoders,
    DateTime ProbedAt);
