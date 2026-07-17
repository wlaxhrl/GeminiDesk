using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace GeminiDesk;

public sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/wlaxhrl/GeminiDesk";
    private readonly UpdateManager _manager = new(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;

    public bool IsPortable => _manager.IsPortable;

    public bool IsSetupInstalled => IsInstalled && !IsPortable;

    public string CurrentVersion
    {
        get
        {
            if (_manager.CurrentVersion is not null)
            {
                return _manager.CurrentVersion.ToString();
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null
                ? "0.1.0"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        return _manager.CheckForUpdatesAsync();
    }

    public Task DownloadUpdateAsync(
        UpdateInfo update,
        Action<int> progress,
        CancellationToken cancellationToken = default)
    {
        return _manager.DownloadUpdatesAsync(update, progress, cancellationToken);
    }

    public void ApplyUpdateAndRestart(UpdateInfo update)
    {
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
