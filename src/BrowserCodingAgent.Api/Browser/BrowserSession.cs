using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Providers.M365;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BrowserCodingAgent.Api.Browser;

public interface IBrowserSession : IAsyncDisposable
{
    IPage? ActivePage { get; }
    bool IsRunning { get; }
    string? LastError { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<(bool Ready, string? Reason)> CheckReadyAsync(M365ChatAdapter adapter);
}

public class BrowserSession : IBrowserSession, IHostedService
{
    private readonly BrowserOptions _browserOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<BrowserSession> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const int ProfileInUseAttempts = 30; // a stopping instance can hold the profile while it finishes an in-flight turn
    private static readonly TimeSpan ProfileInUseRetryDelay = TimeSpan.FromSeconds(1);

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _activePage;
    private bool _isClosing;
    private bool _isDisposed;

    public BrowserSession(
        IOptions<BrowserOptions> browserOptions,
        IHostEnvironment environment,
        ILogger<BrowserSession> logger)
    {
        _browserOptions = browserOptions.Value;
        _environment = environment;
        _logger = logger;
    }

    public IPage? ActivePage => _activePage;
    public bool IsRunning => _context != null && !_isClosing && _activePage != null && !_activePage.IsClosed;
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_browserOptions.AutoStart)
        {
            _logger.LogInformation("Browser AutoStart is disabled in configuration.");
            return;
        }

        await EnsureStartedAsync(cancellationToken);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                return;

            _logger.LogInformation("Launching browser channel: {Channel}", _browserOptions.Channel);
            _playwright ??= await Playwright.CreateAsync();

            var userDataDir = _browserOptions.ResolveUserDataDirectory(_environment.ContentRootPath);
            Directory.CreateDirectory(userDataDir);

            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = _browserOptions.Headless,
                Channel = string.IsNullOrWhiteSpace(_browserOptions.Channel) ? null : _browserOptions.Channel,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            };

            _context = await LaunchPersistentContextAsync(_playwright, userDataDir, options, cancellationToken);
            _context.Close += (_, _) =>
            {
                _logger.LogWarning("Browser context was closed.");
                _activePage = null;
            };

            _context.Page += (_, newPage) =>
            {
                newPage.Close += (_, _) =>
                {
                    if (_activePage == newPage)
                    {
                        _logger.LogWarning("Bound active page closed.");
                        _activePage = null;
                    }
                };
            };

            var targetHost = GetTargetHost();
            var candidatePages = _context.Pages
                .Where(p => !p.IsClosed && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) &&
                            string.Equals(u.Host, targetHost, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidatePages.Count == 1)
            {
                _activePage = candidatePages[0];
            }
            else if (candidatePages.Count > 1)
            {
                _logger.LogWarning("Multiple open tabs match target host '{TargetHost}'. Binding to the first.", targetHost);
                _activePage = candidatePages[0];
            }
            else
            {
                _activePage = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
                if (!string.IsNullOrWhiteSpace(_browserOptions.StartUrl) &&
                    (_activePage.Url == "about:blank" || string.IsNullOrEmpty(_activePage.Url)))
                {
                    _logger.LogInformation("Navigating to start URL: {StartUrl}", _browserOptions.StartUrl);
                    try
                    {
                        await _activePage.GotoAsync(_browserOptions.StartUrl, new PageGotoOptions { Timeout = 30000 });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed initial navigation to {StartUrl}", _browserOptions.StartUrl);
                        LastError = ex.Message;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to launch browser context: {Message}", ex.Message);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<(bool Ready, string? Reason)> CheckReadyAsync(M365ChatAdapter adapter)
    {
        if (_context == null || _isClosing)
            return (false, "Browser is not running.");

        if (_activePage == null || _activePage.IsClosed)
        {
            var targetHost = GetTargetHost();
            var candidates = _context.Pages
                .Where(p => !p.IsClosed && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) &&
                            string.Equals(u.Host, targetHost, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1)
            {
                _activePage = candidates[0];
            }
            else if (candidates.Count > 1)
            {
                return (false, $"Multiple candidate browser tabs match '{targetHost}'. Exactly one chat tab must be open.");
            }
            else
            {
                var loginPage = _context.Pages.FirstOrDefault(p =>
                    !p.IsClosed && (p.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                                   p.Url.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase) ||
                                   p.Url.Contains("signin", StringComparison.OrdinalIgnoreCase)));
                if (loginPage != null)
                {
                    _activePage = loginPage;
                    return (false, "Sign-in required in browser window.");
                }

                return (false, $"No open browser tab matches target host '{targetHost}'.");
            }
        }

        var url = _activePage.Url;
        if (url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("signin", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Sign-in required in browser window.");
        }

        return await adapter.CheckPageReadyAsync(_activePage);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isClosing && _context == null && _playwright == null)
            return;

        _isClosing = true;
        try
        {
            await _initLock.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_context != null)
            {
                _logger.LogInformation("Closing browser context...");
                await _context.CloseAsync();
                _context = null;
                _activePage = null;
            }

            if (_playwright != null)
            {
                _playwright.Dispose();
                _playwright = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping browser session: {Message}", ex.Message);
        }
        finally
        {
            try
            {
                _initLock.Release();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        await StopAsync(CancellationToken.None);
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // Chrome allows one process per profile. A previous run's browser (e.g. debugger restart) may still be exiting.
    private async Task<IBrowserContext> LaunchPersistentContextAsync(
        IPlaywright playwright,
        string userDataDir,
        BrowserTypeLaunchPersistentContextOptions options,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, options);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("existing browser session", StringComparison.OrdinalIgnoreCase))
            {
                if (attempt >= ProfileInUseAttempts)
                    throw new InvalidOperationException(DescribeProfileInUse(userDataDir), ex);

                _logger.LogWarning("Browser profile {UserDataDir} is in use by another browser process; retrying ({Attempt}/{MaxAttempts}).",
                    userDataDir, attempt, ProfileInUseAttempts);
                await Task.Delay(ProfileInUseRetryDelay, cancellationToken);
            }
        }
    }

    private static string DescribeProfileInUse(string userDataDir)
    {
        // macOS/Linux: SingletonLock is a symlink to "<hostname>-<pid>"
        string? owner = null;
        try
        {
            var target = new FileInfo(Path.Combine(userDataDir, "SingletonLock")).LinkTarget;
            if (target != null && int.TryParse(target[(target.LastIndexOf('-') + 1)..], out var pid))
                owner = $" (PID {pid})";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return $"Browser profile '{userDataDir}' is already in use by another browser process{owner}. " +
               "Close that browser or stop the other browser-coding-agent instance, " +
               "or set Browser:UserDataDirectory to a different profile.";
    }

    private string GetTargetHost()
    {
        if (Uri.TryCreate(_browserOptions.StartUrl, UriKind.Absolute, out var uri))
            return uri.Host;
        return "m365.cloud.microsoft";
    }
}
