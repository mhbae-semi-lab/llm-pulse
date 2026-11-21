using System.Runtime.InteropServices;
using System.Windows;
using LLMPulse.App.Services;
using LLMPulse.App.ViewModels;
using LLMPulse.Core;
using LLMPulse.Core.Scoring;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMPulse.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _single;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Hook(this);
        try { await StartAsync(e).ConfigureAwait(true); }
        catch (Exception ex)
        {
            CrashLog.Write("startup", ex);
            CrashLog.Show(ex);
            Shutdown(1);
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        var args = e.Args;
        if (args.Contains("--demo") && Environment.GetEnvironmentVariable("LLMPULSE_DATA_DIR") is not { Length: > 0 })
            Environment.SetEnvironmentVariable("LLMPULSE_DATA_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llmpulse-demo"));

        if (args.Contains("--force")) Environment.SetEnvironmentVariable("LLMPULSE_NO_CACHE", "1"); // 캐시 무시하고 AA 재호출
        var headless = args.Any(a => a is "--refresh" or "--set-api-key");
        var interactive = !headless && !args.Contains("--demo") && !args.Contains("--screenshot");
        if (interactive)
        {
            _single = new SingleInstance();
            if (!_single.IsFirst)
            {
                _single.SignalExisting(); // 이미 실행 중이면 그 창을 앞으로
                Shutdown(0);
                return;
            }
        }

        if (headless)
        {
            AttachConsole(-1);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        _host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddSingleton(sp => new Database(
                    AppPaths.DatabasePath, sp.GetRequiredService<ILogger<Database>>()));
                services.AddSingleton<SqliteStore>();
                services.AddSingleton(_ => LLMPulse.Core.Plans.PlanCatalog.Load(
                    System.IO.Path.Combine(AppPaths.DataDirectory, LLMPulse.Core.Plans.PlanCatalog.UserFileName),
                    System.IO.Path.Combine(AppPaths.DataDirectory, LLMPulse.Core.Plans.PlanCatalog.RemoteFileName)));
                services.AddHttpClient<LLMPulse.Core.Plans.PlanCatalogUpdater>();
                services.AddSingleton(Catalog.LoadEmbedded());
                services.AddSingleton<ScoringService>();
                services.AddSingleton<IApiKeyStore, DpapiApiKeyStore>();
                services.AddSingleton(new ResponseCache(
                    System.IO.Path.Combine(AppPaths.DataDirectory, "aa-cache.json"), TimeSpan.FromHours(6)));
                services.AddHttpClient<ArtificialAnalysisAdapter>();
                services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<ArtificialAnalysisAdapter>());
                services.AddHttpClient<AiderAdapter>();
                services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<AiderAdapter>());
                services.AddHttpClient<OpenRouterAdapter>();
                services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<OpenRouterAdapter>());
                services.AddHttpClient<LmArenaAdapter>();
                services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<LmArenaAdapter>());
                services.AddTransient<RefreshService>();
                services.AddSingleton<AppState>();
                services.AddSingleton<TaskSchedulerService>();
                services.AddSingleton<AutoRefreshService>();
                services.AddSingleton<TrayService>();
                services.AddSingleton<RefreshCoordinator>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<LeaderboardViewModel>();
                services.AddSingleton<MetricsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<CategoryDetailViewModel>();
                services.AddSingleton<CompareViewModel>();
                services.AddSingleton<ModelDetailViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<MatchingViewModel>();
                services.AddSingleton<PlansViewModel>();
                services.AddSingleton<WizardViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.Services.GetRequiredService<Database>().Migrate();

        if (args.Contains("--set-api-key"))
        {
            var code = SetApiKey(args);
            Shutdown(code);
            return;
        }

        if (args.Contains("--refresh"))
        {
            var code = await RefreshAsync().ConfigureAwait(true);
            Shutdown(code);
            return;
        }

        if (args.Contains("--demo"))
        {
            var store = _host.Services.GetRequiredService<SqliteStore>();
            if (store.Counts().Models == 0)
            {
                DemoData.Seed(store, DateTimeOffset.UtcNow);

                // 데모: 사용자 정의 플랜 파일 기능을 보여주기 위해 가짜 플랜을 만들고 선택해 둔다
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppPaths.DataDirectory, LLMPulse.Core.Plans.PlanCatalog.UserFileName),
                    """
                    [
                      { "id": "demo_plan", "monthlyUsd": 20, "vendors": ["Borealis", "Cobalt"], "reviewed": "2026-11-19",
                        "name": { "en": "Demo subscription", "ko": "데모 구독" },
                        "note": { "en": "Fake plan for the demo: Borealis and Cobalt models.", "ko": "데모용 가짜 플랜: Borealis, Cobalt 모델." } },
                      { "id": "demo_api", "all": true, "reviewed": "2026-11-19",
                        "name": { "en": "Demo API", "ko": "데모 API" } }
                    ]
                    """);
                store.SetSetting(AppState.SettingPlans, "demo_plan");
                store.SetPlanOverride("demo_plan", "Cobalt Demo 17", LLMPulse.Core.Plans.PlanAccess.Limited);
                store.SavePlanLimit("demo_plan", 500, "month", "Fast requests");
                store.AddPlanUsage("demo_plan", 410, DateTimeOffset.UtcNow);
                store.SetSetting(AppState.SettingOnboarded, "1");

                // 데모: 어제 순위를 살짝 흔들어 "무엇이 바뀌었나"가 보이게 한다
                var scoring = _host.Services.GetRequiredService<ScoringService>();
                var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
                var demoSnap = scoring.Build(todayUtc);
                scoring.Persist(demoSnap, todayUtc);
                // 1위 교체는 두 분야만: 어제 스냅샷에서 오늘 1위의 지표 값을 2위보다 낮춰 "바뀐 이유"가 실제 숫자로 나오게 한다
                var yesterday = todayUtc.AddDays(-1).ToString("yyyy-MM-dd");
                var swapped = new HashSet<string> { "overall", "arena_coding" };
                static int Before(bool swap, int i) => i switch { 0 => swap ? 2 : 1, 1 => swap ? 1 : 2, 4 => 9, 7 => 12, _ => i + 1 };
                store.SaveCategoryScores(yesterday,
                    demoSnap.Categories.SelectMany(c => c.Scores
                        .Where(s => s.Score is not null).OrderBy(s => s.Rank)
                        .Select((s, i) => s with { Rank = Before(swapped.Contains(c.Category.Id), i) })));
                foreach (var c in demoSnap.Categories.Where(c => swapped.Contains(c.Category.Id)))
                {
                    var top2 = c.Scores.Where(s => s.Score is not null).OrderBy(s => s.Rank).Take(2).ToList();
                    var metricId = c.Category.Metrics[0].Id;
                    if (top2.Count == 2 && demoSnap.Models[top2[1].ModelId].Metrics.TryGetValue(metricId, out var second))
                        store.SaveSnapshotValue(yesterday, top2[0].ModelId, metricId, Math.Round(second - (second > 200 ? 9 : 1.2), 1));
                }
                // 데모: 지난 6일치 점수를 살짝 흔들어 7일 추이(스파크라인)가 보이게 한다
                for (var d = 2; d <= 6; d++)
                {
                    var day = todayUtc.AddDays(-d).ToString("yyyy-MM-dd");
                    store.SaveCategoryScores(day, demoSnap.Categories.SelectMany(c => c.Scores
                        .Where(s => s.Score is not null)
                        .Select(s => s with { Score = s.Score!.Value * (1 + 0.012 * Math.Sin(d * 1.3 + s.ModelId * 0.9) - 0.004 * d) })));
                }

                // 데모: 어제 가격을 바꿔서 가격 변동 피드(인하/인상)가 보이게 한다
                var priced = demoSnap.Models.Values.Where(m => m.BlendedPrice is > 0).OrderBy(m => m.Id).Take(12).ToList();
                for (var k = 0; k < priced.Count; k++)
                {
                    var price = priced[k].BlendedPrice!.Value;
                    store.SavePriceHistory(todayUtc.ToString("yyyy-MM-dd"), priced[k].Id, price);
                    store.SavePriceHistory(todayUtc.AddDays(-1).ToString("yyyy-MM-dd"), priced[k].Id,
                        k % 4 == 0 ? price * 1.3 : k % 4 == 1 ? price * 0.8 : price);
                }

                // 데모: 즐겨찾기 두 개 (코딩 1위, 5위)
                var coding = demoSnap.Categories.FirstOrDefault(c => c.Category.Id == "coding");
                var favIds = coding?.Scores.Where(s => s.Score is not null).OrderBy(s => s.Rank).Select(s => s.ModelId).ToList() ?? [];
                if (favIds.Count > 4) store.SetSetting(AppState.SettingFavorites, $"{favIds[0]},{favIds[4]}");
            }
        }

        _host.Services.GetRequiredService<AppState>().Reload();
        var window = _host.Services.GetRequiredService<MainWindow>();
        // --background(Windows 시작 시 자동 실행): 창 없이 트레이로 시작해 알아서 갱신한다
        if (!(interactive && args.Contains("--background"))) window.Show();

        if (interactive)
        {
            var tray = _host.Services.GetRequiredService<TrayService>();
            tray.Attach(window);
            _single!.ListenForShow(() => Dispatcher.Invoke(tray.ShowWindow));
            _host.Services.GetRequiredService<AutoRefreshService>().Start();
            _host.Services.GetRequiredService<WizardViewModel>().OpenIfFirstRun();

            // 설치판 자동 업데이트: 1분 뒤 조용히 받아 두고 다음 실행 때 적용 (설정에서 끌 수 있음)
            if (_host.Services.GetRequiredService<AppState>().GetFlag(AppUpdater.SettingAuto, true))
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                    try { await AppUpdater.DownloadInBackgroundAsync().ConfigureAwait(false); }
                    catch (Exception ex) { CrashLog.Write("auto-update", ex); }
                });
        }

        // 개발용: --screenshot <dir> 이면 모든 페이지를 en/ko 로 PNG 저장 후 종료
        var shotIdx = Array.IndexOf(args, "--screenshot");
        if (shotIdx >= 0 && shotIdx + 1 < args.Length)
            await ScreenshotRunner.RunAsync(window, args[shotIdx + 1]).ConfigureAwait(true);
    }

    private int SetApiKey(string[] args)
    {
        var keys = _host!.Services.GetRequiredService<IApiKeyStore>();
        string? key = null;

        var idx = Array.IndexOf(args, "--set-api-key");
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
            key = args[idx + 1];

        key ??= Environment.GetEnvironmentVariable("AA_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("Usage: set AA_API_KEY then run --set-api-key, or --set-api-key <key>");
            return 1;
        }

        keys.Set(ArtificialAnalysisAdapter.Source, key);
        Console.WriteLine("API key saved (DPAPI).");
        return 0;
    }

    private async Task<int> RefreshAsync()
    {
        var log = _host!.Services.GetRequiredService<ILogger<App>>();
        try
        {
            var summary = await _host.Services.GetRequiredService<RefreshService>()
                .RefreshAsync().ConfigureAwait(true);

            foreach (var s in summary.Sources)
            {
                if (s.Error is null)
                    Console.WriteLine($"OK  {s.SourceId}: {s.ModelCount} models");
                else
                    Console.Error.WriteLine($"ERR {s.SourceId}: {s.Error}");
            }

            Console.WriteLine(
                $"DB totals — models={summary.TotalModels} metrics={summary.TotalMetrics} prices={summary.TotalPrices}");
            return summary.AllSucceeded ? 0 : 2;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Refresh failed");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Services.GetService<TrayService>()?.Dispose();
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
