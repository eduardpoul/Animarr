using Animarr.Shared.Models;
using Animarr.Web.Services;

namespace Animarr.Web.Mapping;

internal static class HardwareMappings
{
    public static EncoderStatusDto ToDto(this HardwareInfoService.EncoderStatus es)
        => new(es.Available, es.Vendor, es.DeviceName, es.DriverInfo, es.Detail);

    public static HardwareReportDto ToDto(this HardwareInfoService.HardwareReport r)
        => new(
            r.Vaapi.ToDto(),
            r.Nvenc.ToDto(),
            r.Qsv.ToDto(),
            r.HwAccels,
            r.HwEncoders,
            r.ProbedAt);
}
