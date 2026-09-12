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
// 이슈 #130: appsettings.json의 "StructurePolicy" 섹션으로 필드(예: RoundTripFeePercent)를 덮어쓸 수 있다.
// Default 자체는 건드리지 않도록 복사본에 바인딩한다 — PolicyHash 리터럴 테스트가 참조하는 값을 바꾸지 않는다.
builder.Services.AddSingleton(_ => { var policy = Astra.Server.Domain.Structure.StructurePolicy.Default with { }; builder.Configuration.GetSection("StructurePolicy").Bind(policy); return policy; });
builder.Services.AddSingleton(_ => StructureEngineOptions.Parse(builder.Configuration["StructureEngineMode"]));
builder.Services.AddSingleton<IStructureObservationStore, StructureObservationStore>();
// D6(§18): active에서 v5 계획을 실제 시뮬 거래로 커밋하는 유일한 저장 접점. off/shadow에서는 호출되지 않는다.
builder.Services.AddSingleton<IStructuralTradeEntries, StructuralTradeEntryService>();
// 이슈 #26: v5 알림 이벤트 발행자 — active gate 안 commit 지점에서만 발행되고 /api/state가 소비한다.
builder.Services.AddSingleton<StructureAlertPublisher>();
// 이슈 #132: 종목 메타(`/stocks`) 세션 캐시 — 구조 게이트와 회전율 지표가 공유한다.
builder.Services.AddSingleton<SymbolMetadataService>();
builder.Services.AddSingleton<StructureObservationWriter>(); builder.Services.AddSingleton<StructureAnalysisService>();
// 이슈 #41: 폴링 → 구조 엔진 호가 배선. 새 게이트웨이가 아니라 LiquidityQueryService 캐시를 공유한다.
builder.Services.AddSingleton<StructureLiquidityFeed>();
builder.Services.AddSingleton<MonitorRuntimeState>(); builder.Services.AddSingleton<MonitorPollingService>(); builder.Services.AddSingleton<MonitorService>(); builder.Services.AddSingleton<IMonitorSignals>(x => x.GetRequiredService<MonitorPollingService>());
builder.Services.AddSingleton<MonitorControlService>(); builder.Services.AddSingleton<MetricsQueryService>(); builder.Services.AddSingleton<LiquidityQueryService>(); builder.Services.AddSingleton<SimulationReportQueryService>(); builder.Services.AddSingleton<ValidationQueryService>(); builder.Services.AddSingleton<CatalogQueryService>(); builder.Services.AddSingleton<PositionService>(); builder.Services.AddSingleton<StateQueryService>(); builder.Services.AddSingleton(TimeProvider.System);
// 이슈 #130: 실계좌 US 왕복 수수료와 StructurePolicy.RoundTripFeePercent 정합 확인.
builder.Services.AddSingleton<FeeRateCheckService>();
builder.Services.AddHostedService(x => x.GetRequiredService<MonitorService>());
var app = builder.Build();
var clientRoot = Environment.GetEnvironmentVariable("ASTRA_CLIENT_ROOT") ?? Path.Combine(app.Environment.ContentRootPath, "..", "client"); var clientDist = Path.GetFullPath(Path.Combine(clientRoot, "dist"));
if (Directory.Exists(clientDist)) { var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(clientDist); app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files }); app.UseStaticFiles(new StaticFileOptions { FileProvider = files }); }
app.MapAstraApi();
if (Directory.Exists(clientDist)) app.MapFallback(async context => { context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.SendFileAsync(Path.Combine(clientDist, "index.html")); });
app.Run();
public partial class Program { }
