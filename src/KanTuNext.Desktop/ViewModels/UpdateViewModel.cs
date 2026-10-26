using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core.Settings;
using KanTuNext.Core.Timeline;
using KanTuNext.Desktop.Services;
using KanTuNext.Desktop.Services.Updates;

namespace KanTuNext.Desktop.ViewModels;

/// <summary>The stages the update window walks through.</summary>
public enum UpdateStage
{
    Checking,
    Available,
    UpToDate,
    Failed,
    Downloading,
    Extracting,
    ReadyToInstall,
}

public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private CancellationTokenSource? _cts;
    private string? _staged;
    private ReleaseInfo? _release;

    public UpdateViewModel(AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrompt), nameof(IsProgress), nameof(StageText), nameof(IsIndeterminate),
        nameof(ShowCancel), nameof(ShowRestart), nameof(ShowDismiss))]
    public partial UpdateStage Stage { get; private set; } = UpdateStage.Checking;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string DetailText { get; private set; } = "";

    [ObservableProperty]
    public partial string TitleText { get; private set; } = "";

    [ObservableProperty]
    public partial string NotesText { get; private set; } = "";

    public bool InstallAutomatically
    {
        get => _settings.InstallUpdatesAutomatically;
        set
        {
            _settings.InstallUpdatesAutomatically = value;
            _save();
        }
    }

    /// <summary>A deb/rpm install can't replace itself: the prompt offers the download page instead.</summary>
    public bool CanInstall => UpdateAssetSelector.CanReplaceSelf;

    public bool IsPrompt => Stage == UpdateStage.Available;
    public bool IsProgress => !IsPrompt;
    public bool IsIndeterminate => Stage == UpdateStage.Checking;
    public bool ShowCancel => Stage is UpdateStage.Checking or UpdateStage.Downloading;
    public bool ShowRestart => Stage == UpdateStage.ReadyToInstall;
    public bool ShowDismiss => Stage is UpdateStage.UpToDate or UpdateStage.Failed;

    public string StageText => Stage switch
    {
        UpdateStage.Checking => "正在检查更新…",
        UpdateStage.Downloading => "正在下载更新…",
        UpdateStage.Extracting => "正在解压更新…",
        UpdateStage.ReadyToInstall => "更新已准备好",
        UpdateStage.UpToDate => "已是最新版本",
        UpdateStage.Failed => "更新失败",
        _ => "",
    };

    /// <summary>「检查更新…」: the window already shows the checking stage.</summary>
    public async Task CheckAsync()
    {
        var cts = ResetCancellation();
        Stage = UpdateStage.Checking;
        try
        {
            using var service = new UpdateService();
            if (await service.FindNewerAsync(cts.Token) is { } release) ShowAvailable(release);
            else
            {
                DetailText = $"看图 {UpdateService.CurrentVersion.ToString(3)} 是目前最新的版本。";
                Stage = UpdateStage.UpToDate;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Fail("无法连接到 GitHub，请检查网络后再试。");
        }
    }

    /// <summary>The launch-time check: a newer release or null, with any failure kept quiet.</summary>
    internal async Task<ReleaseInfo?> FindNewerQuietlyAsync()
    {
        try
        {
            using var service = new UpdateService();
            return await service.FindNewerAsync();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal void ShowAvailable(ReleaseInfo release)
    {
        _release = release;
        TitleText = $"看图 {release.VersionText} 可以更新了，你现在用的是 {UpdateService.CurrentVersion.ToString(3)}。";
        NotesText = PlainNotes(release.Body);
        Stage = UpdateStage.Available;
    }

    /// <summary>Downloads and unpacks the release; true when it is ready to install.</summary>
    internal async Task<bool> DownloadAndPrepareAsync(ReleaseInfo release)
    {
        _release = release;
        var cts = ResetCancellation();
        try
        {
            var asset = UpdateAssetSelector.Select(release) ?? throw new InvalidOperationException("没有找到适合这台电脑的安装包。");
            Stage = UpdateStage.Downloading;
            Progress = 0;
            DetailText = "";
            var dir = Path.Combine(Path.GetTempPath(), "KanTuNext", "updates", release.VersionText);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, asset.Name);

            using (var service = new UpdateService())
                await service.DownloadAsync(asset, file, new Progress<(long Received, long Total)>(p =>
                {
                    Progress = p.Total > 0 ? (double)p.Received / p.Total : 0;
                    DetailText = p.Total > 0 ? $"{TimelineText.FileSize(p.Received)} / {TimelineText.FileSize(p.Total)}" : TimelineText.FileSize(p.Received);
                }), cts.Token);

            Stage = UpdateStage.Extracting;
            Progress = 0;
            DetailText = "";
            _staged = await UpdateApplier.PrepareAsync(file, new Progress<double>(p => Progress = p), cts.Token);
            Progress = 1;
            DetailText = "重新启动看图即可完成更新。";
            Stage = UpdateStage.ReadyToInstall;
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException e)
        {
            Fail(e.Message);
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidDataException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            Fail($"下载或解压没有成功：{e.Message}");
            return false;
        }
    }

    public Task InstallAsync() => _release is null ? Task.CompletedTask : DownloadAndPrepareAsync(_release);

    public void Skip()
    {
        if (_release is not null)
        {
            _settings.SkippedVersion = _release.TagName;
            _save();
        }
        CloseRequested?.Invoke();
    }

    public void OpenReleasePage() => Opener.Open(_release?.HtmlUrl is { Length: > 0 } url ? url : UpdateService.ReleasesPage);

    public void Restart()
    {
        if (_staged is null || !UpdateApplier.TryInstallAndRelaunch(_staged))
        {
            Fail("无法启动安装，请到下载页手动更新。");
            return;
        }
        // The installer script waits for this process to exit before replacing files
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public void Cancel() => _cts?.Cancel();

    private CancellationTokenSource ResetCancellation()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return _cts;
    }

    private void Fail(string message)
    {
        DetailText = message;
        Stage = UpdateStage.Failed;
    }

    /// <summary>Release notes are short Markdown lists; show them as plain text (bullets, no emphasis marks).</summary>
    internal static string PlainNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "（没有更新说明）";
        var text = Regex.Replace(markdown.Replace("\r", ""), @"\*\*|__|`", "");
        text = Regex.Replace(text, @"^\s*[-*] ", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^#+\s*", "", RegexOptions.Multiline);
        return text.Trim();
    }
}
