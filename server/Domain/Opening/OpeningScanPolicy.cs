using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Server.Domain.Opening;

/// <summary>개장 초반 스캔 임계값(#371 §3.1). StructurePolicy·PolicyHash와 완전히 분리된 자체 정책이며 진입 판정에 쓰이지 않는다.</summary>
public sealed record OpeningScanPolicy
{
    public bool Enabled { get; set; } = true;
    public int LookbackSessions { get; set; } = 20;
    public int MinimumSessions { get; set; } = 5;
    public int WindowMinutes { get; set; } = 30;
    public int RvolShortSessions { get; set; } = 3;
    public int RvolMediumSessions { get; set; } = 5;
    public int GradeMinimumSessions { get; set; } = 3;
    public double VolumeStrongRatio { get; set; } = 2.0;
    public double PriceUpPercent { get; set; } = 0.5;
    public bool RequireAboveVwap { get; set; } = true;
    public int SampleCountForFull { get; set; } = 20;
    public string Version { get; set; } = "opening-scan.1";

    public static OpeningScanPolicy Default => new();

    /// <summary>임계값만 담은 canonical JSON. 운영 전용 필드(Enabled)와 파생 값은 제외한다(§16A).</summary>
    public string CanonicalJson
    {
        get
        {
            var builder = new StringBuilder("{");
            builder.Append("\"GradeMinimumSessions\":").Append(Number(GradeMinimumSessions)).Append(',');
            builder.Append("\"LookbackSessions\":").Append(Number(LookbackSessions)).Append(',');
            builder.Append("\"MinimumSessions\":").Append(Number(MinimumSessions)).Append(',');
            builder.Append("\"PriceUpPercent\":").Append(Number(PriceUpPercent)).Append(',');
            builder.Append("\"RequireAboveVwap\":").Append(RequireAboveVwap ? "true" : "false").Append(',');
            builder.Append("\"RvolMediumSessions\":").Append(Number(RvolMediumSessions)).Append(',');
            builder.Append("\"RvolShortSessions\":").Append(Number(RvolShortSessions)).Append(',');
            builder.Append("\"SampleCountForFull\":").Append(Number(SampleCountForFull)).Append(',');
            builder.Append("\"Version\":").Append('"').Append(Version).Append('"').Append(',');
            builder.Append("\"VolumeStrongRatio\":").Append(Number(VolumeStrongRatio)).Append(',');
            builder.Append("\"WindowMinutes\":").Append(Number(WindowMinutes));
            return builder.Append('}').ToString();
        }
    }

    /// <summary>canonical JSON의 SHA-256 소문자 hex. Structure.PolicyHash와 무관한 자체 해시다.</summary>
    public string PolicyHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));

    static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
