using Astra.Server;
using Astra.Server.Api;
using Astra.Server.Application;
using Astra.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASTRA_URLS") ?? "http://127.0.0.1:5188");
builder.Services.AddSingleton<LocalStore>();
builder.Services.AddSingleton<ILocalStore>(x => x.GetRequiredService<LocalStore>());
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri("https://openapi.tossinvest.com/"), Timeout = TimeSpan.FromSeconds(12) });
builder.Services.AddSingleton<TossClient>(); builder.Services.AddSingleton<TossMarketDataGateway>(); builder.Services.AddSingleton<IMarketDataGateway>(x => x.GetRequiredService<TossMarketDataGateway>());
builder.Services.AddSingleton<IOrderBookGateway>(x => x.GetRequiredService<TossMarketDataGateway>());
builder.Services.AddSingleton<IMonitorDiagnostics, MonitorDiagnostics>();
builder.Services.AddSingleton<TossStreamService>(); builder.Services.AddSingleton<IRealtimeMarketStream>(x => x.GetRequiredService<TossStreamService>());
// v5 구조 엔진(설계 §18): 설정 StructureEngineMode=off|shadow|active, 기본 off. off는 계산을 유발하지 않는다.
builder.Services.AddSingleton(Astra.Server.Domain.Structure.StructurePolicy.Default);
builder.Services.AddSingleton(_ => StructureEngineOptions.Parse(builder.Configuration["StructureEngineMode"]));
builder.Services.AddSingleton<IStructureObservationStore, StructureObservationStore>();
// D6(§18): active에서 v5 계획을 실제 시뮬 거래로 커밋하는 유일한 저장 접점. off/shadow에서는 호출되지 않는다.
builder.Services.AddSingleton<IStructuralTradeEntries, StructuralTradeEntryService>();
builder.Services.AddSingleton<StructureObservationWriter>(); builder.Services.AddSingleton<StructureAnalysisService>();
builder.Services.AddSingleton<MonitorRuntimeState>(); builder.Services.AddSingleton<MonitorPollingService>(); builder.Services.AddSingleton<MonitorService>(); builder.Services.AddSingleton<IMonitorSignals>(x => x.GetRequiredService<MonitorPollingService>());
builder.Services.AddSingleton<MonitorControlService>(); builder.Services.AddSingleton<MetricsQueryService>(); builder.Services.AddSingleton<LiquidityQueryService>(); builder.Services.AddSingleton<SimulationReportQueryService>(); builder.Services.AddSingleton<CatalogQueryService>(); builder.Services.AddSingleton<PositionService>(); builder.Services.AddSingleton<StateQueryService>(); builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService(x => x.GetRequiredService<MonitorService>());
var app = builder.Build();
var clientRoot = Environment.GetEnvironmentVariable("ASTRA_CLIENT_ROOT") ?? Path.Combine(app.Environment.ContentRootPath, "..", "client"); var clientDist = Path.GetFullPath(Path.Combine(clientRoot, "dist"));
if (Directory.Exists(clientDist)) { var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(clientDist); app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files }); app.UseStaticFiles(new StaticFileOptions { FileProvider = files }); }
app.MapAstraApi();
if (Directory.Exists(clientDist)) app.MapFallback(async context => { context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.SendFileAsync(Path.Combine(clientDist, "index.html")); });
app.Run();
public partial class Program { }
