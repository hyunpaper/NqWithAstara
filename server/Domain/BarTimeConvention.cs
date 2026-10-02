using System.Text.Json;

namespace Astra.Server.Domain;

/// <summary>저장 1분봉의 시각 라벨 규약(#332). 레코드 필드 <c>tc</c>가 없으면 v0(Toss 종료 라벨을 시작으로 오해한 봉)이다.</summary>
public static class BarTimeConvention
{
    public const string RecordField = "tc";
    public const string Legacy = "toss-end-label.0";
    public const string BarStart = "bar-start.1";
    public const string Current = BarStart;
    public const string MixedRejection = "BAR_TIME_CONVENTION_MIXED";

    public static string Of(JsonElement record) =>
        record.TryGetProperty(RecordField, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } convention ? convention : Legacy;

    public static string Of(string line) => Of(JsonDocument.Parse(line).RootElement);
}

/// <summary>Toss 1분봉 <c>timestamp</c>는 봉 종료 시각이다(감사 2026-10-02). 시작 시각으로 바꿔 쓴다(#332).</summary>
public static class TossBarTime
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public static DateTimeOffset StartOf(DateTimeOffset label) => label - Interval;
    public static DateTimeOffset LabelOf(DateTimeOffset start) => start + Interval;
}
