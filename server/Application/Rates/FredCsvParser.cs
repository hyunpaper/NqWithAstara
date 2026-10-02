using System.Globalization;

namespace Astra.Server.Application.Rates;

/// <summary>FRED `fredgraph.csv` 파서. 결측(`.`)·빈 값은 건너뛰고 날짜 오름차순으로 돌려준다(#316).</summary>
public static class FredCsvParser
{
    public static IReadOnlyList<DailyRatePoint> Parse(string csv, string series)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentException.ThrowIfNullOrWhiteSpace(series);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) throw new FormatException("CSV가 비어 있습니다.");
        var header = lines[0].TrimStart('\uFEFF').Split(',');
        var column = Array.FindIndex(header, x => string.Equals(x.Trim(), series, StringComparison.OrdinalIgnoreCase));
        if (header.Length < 2 || !header[0].Contains("date", StringComparison.OrdinalIgnoreCase) || column < 1)
            throw new FormatException($"헤더에 {series} 열이 없습니다.");

        var points = new List<DailyRatePoint>(lines.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            if (cells.Length <= column) continue;
            if (!DateOnly.TryParseExact(cells[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var date))
                continue;
            var raw = cells[column].Trim();
            if (raw.Length == 0 || raw == ".") continue;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
                continue;
            points.Add(new DailyRatePoint(date, value));
        }
        points.Sort((a, b) => a.Date.CompareTo(b.Date));
        return points;
    }
}
