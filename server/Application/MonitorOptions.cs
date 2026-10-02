namespace Astra.Server.Application;

/// <summary>`Monitor` 설정 섹션. AutoStart 기본 false (#324).</summary>
public sealed class MonitorOptions
{
    public bool AutoStart { get; set; }
}
