using Astra.Server;
using Astra.Server.Api;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Backtest;
using Astra.Server.Domain.Confluence;
using Astra.Server.Infrastructure;

// 이슈 #169: 측정 서브커맨드. 서버를 띄우지 않고 저장 봉만 재생해 가중치를 산출하고 종료한다.
if (args is [ConfluenceMeasureCommand.Name, ..])
{
    Environment.ExitCode = await ConfluenceMeasureCommand.RunAsync(args, Console.Out, TimeProvider.System,
        CancellationToken.None);
    return;
}
if (args is [ConfluenceBackfillCommand.Name, ..])
{
    using var http = new HttpClient { BaseAddress = new Uri("https://openapi.tossinvest.com/"), Timeout = TimeSpan.FromSeconds(30) };
    Environment.ExitCode = await ConfluenceBackfillCommand.RunAsync(args, Console.Out, TimeProvider.System,
        new TossHistoricalBarSource(new TossClient(http)), CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASTRA_URLS") ?? "http://127.0.0.1:5188");
builder.Services.AddSingleton<LocalStore>();
builder.Services.AddSingleton<ILocalStore>(x => x.GetRequiredService<LocalStore>());
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri("https://openapi.tossinvest.com/"), Timeout = TimeSpan.FromSeconds(12) });
builder.Services.AddSingleton<TossClient>(); builder.Services.AddSingleton<TossMarketDataGateway>(); builder.Services.AddSingleton<IMarketDataGateway>(x => x.GetRequiredService<TossMarketDataGateway>());
builder.Services.AddSingleton<IOrderBookGateway>(x => x.GetRequiredService<TossMarketDataGateway>());
builder.Services.AddSingleton<IMonitorDiagnostics, MonitorDiagnostics>();
builder.Services.AddSingleton<TickFlowTape>(); builder.Services.AddSingleton<TradeTapeFallbackService>();
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
builder.Services.AddSingleton<StructureObservationWriter>();
builder.Services.AddSingleton<RejectedPlanResearchService>(sp => {
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("Research:RejectedPlan");
    return new RejectedPlanResearchService(sp.GetRequiredService<ILocalStore>(), new RejectedPlanResearchOptions(cfg.GetValue("Enabled", false), Math.Clamp(cfg.GetValue("Limit", 500), 1, 5000)));
});
builder.Services.AddSingleton<StructureAnalysisService>();
builder.Services.AddSingleton<StructuralPendingEntryService>();
// 이슈 #41: 폴링 → 구조 엔진 호가 배선. 새 게이트웨이가 아니라 LiquidityQueryService 캐시를 공유한다.
builder.Services.AddSingleton<StructureLiquidityFeed>();
builder.Services.AddSingleton<MonitorRuntimeState>(); builder.Services.AddSingleton<MonitorPollingService>(); builder.Services.AddSingleton<MonitorService>(); builder.Services.AddSingleton<IMonitorSignals>(x => x.GetRequiredService<MonitorPollingService>());
builder.Services.AddSingleton<MonitorControlService>(); builder.Services.AddSingleton<MetricsQueryService>(); builder.Services.AddSingleton<LiquidityQueryService>(); builder.Services.AddSingleton<SimulationReportQueryService>(); builder.Services.AddSingleton<SimulationResetService>(); builder.Services.AddSingleton<ValidationQueryService>(); builder.Services.AddSingleton<CatalogQueryService>(); builder.Services.AddSingleton<PositionService>(); builder.Services.AddSingleton<StateQueryService>(); builder.Services.AddSingleton(TimeProvider.System);
// 이슈 #130: 실계좌 US 왕복 수수료와 StructurePolicy.RoundTripFeePercent 정합 확인.
builder.Services.AddSingleton<FeeRateCheckService>();
// 이슈 #213: 코드 기본 수수료 단일 출처와 설정 바인딩 결과가 갈라지면 기동 시 경고한다.
builder.Services.AddSingleton<TradingCostPolicyCheckService>();
// 이슈 #131: 실계좌 체결(읽기 전용) 수집과 v5 대조 보고서. 주문 생성·정정·취소는 호출하지 않는다.
builder.Services.AddSingleton<IRealFillStore, RealFillStore>();
builder.Services.AddSingleton<RealFillsService>(); builder.Services.AddSingleton<RealVsV5QueryService>();
// 이슈 #165: 컨플루언스 측정 파이프라인(K4)의 저장 봉 — 완료 1분봉 로컬 저장 + QQQ 벤치마크 폴링.
builder.Services.AddSingleton(_ => { var c = new ConfluenceOptions(); builder.Configuration.GetSection("Confluence").Bind(c); return c; });
builder.Services.AddSingleton<IBarStore, BarStore>();
builder.Services.AddSingleton<IReplayBarStoreFactory, ReplayBarStoreFactory>();
builder.Services.AddSingleton<IReplayWorkspace, ReplayWorkspace>();
builder.Services.AddSingleton<IHistoricalBarSource, TossHistoricalBarSource>();
builder.Services.AddSingleton<HistoricalReplayService>();
builder.Services.AddHostedService<HistoricalReplayLifetime>();
builder.Services.AddSingleton<BarStoreService>(); builder.Services.AddSingleton<BenchmarkPollingService>();
builder.Services.AddSingleton<IBenchmarkBarSource>(x => x.GetRequiredService<BenchmarkPollingService>());
// 이슈 #167: 컨플루언스 기법 신호·합산. 가중치는 전부 1.0(미검증)에서 시작하고 K4가 파일로 채운다.
builder.Services.AddSingleton(_ => ConfluencePolicy.Default);
// 이슈 #169: 측정 결과 가중치 파일을 기동 시 1회만 읽는다. 없거나 깨졌으면 전부 1.0으로 남는다.
builder.Services.AddSingleton(x =>
{
    var document = ConfluenceWeightsStore.Load(x.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
        out var error);
    if (error is not null)
        x.GetRequiredService<ILoggerFactory>().CreateLogger("Confluence").LogWarning("{Message}", error);
    return document;
});
builder.Services.AddSingleton(x => x.GetRequiredService<ConfluenceWeightsDocument>().ToWeights());
builder.Services.AddSingleton<IIntradayVolumeProfileSource>(x => new BarStoreVolumeProfiles(
    x.GetRequiredService<IBarStore>(),
    x.GetRequiredService<ConfluencePolicy>().DailyRelativeVolumeLookbackSessions));
builder.Services.AddSingleton<ConfluenceService>();
// 이슈 #151: 뉴스 감성(선택 기능). News:Enabled 기본 false이며 false면 피드·Ollama를 호출하지 않는다.
builder.Services.AddSingleton(_ => { var news = new NewsOptions(); builder.Configuration.GetSection("News").Bind(news); return news; });
builder.Services.AddSingleton<INewsStore, NewsStore>();
builder.Services.AddSingleton<INewsFeed>(x =>
{
    var options = x.GetRequiredService<NewsOptions>();
    return options.UseSaveTicker
        ? new SaveTickerNewsFeed(options)
        : new MarketauxNewsFeed(options);
});
builder.Services.AddSingleton<INewsClassifier>(x => new OllamaNewsClassifier(x.GetRequiredService<NewsOptions>()));
builder.Services.AddSingleton<INewsTranslator>(x => new PapagoNewsTranslator(x.GetRequiredService<NewsOptions>()));
builder.Services.AddSingleton<NewsRuntimeState>();
builder.Services.AddSingleton<NewsTranslationQueue>();
builder.Services.AddSingleton<NewsFeedService>(); builder.Services.AddSingleton<NewsQueryService>();
builder.Services.AddHostedService(x => x.GetRequiredService<MonitorService>());
builder.Services.AddHostedService<NewsService>();
builder.Services.AddHostedService<NewsTranslationService>();
var app = builder.Build();
app.Services.GetRequiredService<TradingCostPolicyCheckService>();
var clientRoot = Environment.GetEnvironmentVariable("ASTRA_CLIENT_ROOT") ?? Path.Combine(app.Environment.ContentRootPath, "..", "client"); var clientDist = Path.GetFullPath(Path.Combine(clientRoot, "dist"));
if (Directory.Exists(clientDist)) { var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(clientDist); app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files }); app.UseStaticFiles(new StaticFileOptions { FileProvider = files }); }
app.MapAstraApi();
if (Directory.Exists(clientDist)) app.MapFallback(async context => { context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.SendFileAsync(Path.Combine(clientDist, "index.html")); });
app.Run();
public partial class Program { }
sealed class HistoricalReplayLifetime(HistoricalReplayService service) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => service.StopAsync(cancellationToken);
}
