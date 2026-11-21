using Velopack;
using Velopack.Sources;

namespace LLMPulse.App.Services;

/// <summary>
/// Velopack 자동 업데이트. 설치판(Setup.exe)에서만 동작하고, zip 으로 푼 휴대용 실행에서는 아무것도 하지 않는다.
/// 기본 피드는 GitHub Releases. LLMPULSE_UPDATE_FEED(폴더 또는 URL)로 바꿔 로컬 피드로 시험할 수 있다.
/// </summary>
public static class AppUpdater
{
    public const string RepoUrl = "https://github.com/mhbae-semi-lab/llm-pulse";
    public const string SettingAuto = "auto_update";

    private static UpdateManager Create() =>
        Environment.GetEnvironmentVariable("LLMPULSE_UPDATE_FEED") is { Length: > 0 } feed
            ? new UpdateManager(feed)
            : new UpdateManager(new GithubSource(RepoUrl, null, false));

    public static bool IsInstalled
    {
        get { try { return Create().IsInstalled; } catch (Exception) { return false; } }
    }

    /// <summary>새 버전이 있으면 그 버전 문자열, 없으면 null. 설치판이 아니면 null.</summary>
    public static async Task<string?> CheckAsync()
    {
        var mgr = Create();
        if (!mgr.IsInstalled) return null;
        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        return info?.TargetFullRelease.Version.ToString();
    }

    /// <summary>받아 두기만 한다. 다음 실행 때 Velopack 이 적용한다 (VelopackApp 기본 동작).</summary>
    public static async Task<string?> DownloadInBackgroundAsync()
    {
        var mgr = Create();
        if (!mgr.IsInstalled) return null;
        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;
        await mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
        return info.TargetFullRelease.Version.ToString();
    }

    /// <summary>받아서 바로 적용하고 다시 시작한다 (정보 화면의 "업데이트 설치").</summary>
    public static async Task ApplyNowAsync()
    {
        var mgr = Create();
        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return;
        await mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
        mgr.ApplyUpdatesAndRestart(info);
    }
}
