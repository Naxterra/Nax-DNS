using System.Collections.ObjectModel;
using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using BetterDns.Gui.Localization;

namespace BetterDns.Gui.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ControlClient client = new();
    private readonly System.Windows.Threading.DispatcherTimer timer;
    private bool editorsLoaded;
    private bool isConnected;
    private bool isActive;
    private bool protectionConfirmed;
    private bool refreshing;
    private string connectionText;
    private string enforcementText;
    private string statusMessage;
    private string selectedLanguage;
    private string? serviceVersion;
    private string? lastConnectionError;
    private EnforcementSnapshot? lastEnforcement;
    private string defaultChainId = string.Empty;
    private bool enforcementEnabled = true;
    private bool blockPlaintextDns = true;
    private int watchdogSeconds = 15;
    private UpstreamEditor? selectedUpstream;
    private ChainEditor? selectedChain;
    private RuleEditor? selectedRule;

    public MainViewModel()
    {
        selectedLanguage = LocalizationManager.CurrentLanguage;
        connectionText = LocalizationManager.Get("Connection.Unavailable");
        enforcementText = LocalizationManager.Get("Status.DriverStarting");
        statusMessage = LocalizationManager.Get("Status.Connecting");
        ToggleProtectionCommand = new AsyncCommand(ToggleProtectionAsync, () => IsConnected);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SaveCommand = new AsyncCommand(SaveAsync, () => IsConnected);
        AddUpstreamCommand = new RelayCommand(() => Upstreams.Add(new() { Name = LocalizationManager.Get("Editor.NewServer") }));
        RemoveUpstreamCommand = new RelayCommand(() => { if (SelectedUpstream is not null) Upstreams.Remove(SelectedUpstream); });
        AddChainCommand = new RelayCommand(() => Chains.Add(new() { Name = LocalizationManager.Get("Editor.NewChain") }));
        RemoveChainCommand = new RelayCommand(() => { if (SelectedChain is not null) Chains.Remove(SelectedChain); });
        AddRuleCommand = new RelayCommand(() => Rules.Add(new() { Name = LocalizationManager.Get("Editor.NewRule") }));
        RemoveRuleCommand = new RelayCommand(() => { if (SelectedRule is not null) Rules.Remove(SelectedRule); });
        timer = new() { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += OnTimerTick;
    }

    public IReadOnlyList<DnsProtocol> Protocols { get; } = Enum.GetValues<DnsProtocol>();
    public IReadOnlyList<DomainMatchKind> MatchKinds { get; } = Enum.GetValues<DomainMatchKind>();
    public IReadOnlyList<RuleAction> RuleActions { get; } = Enum.GetValues<RuleAction>();
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("de", "Deutsch"),
        new("en", "English")
    ];
    public ObservableCollection<UpstreamEditor> Upstreams { get; } = [];
    public ObservableCollection<ChainEditor> Chains { get; } = [];
    public ObservableCollection<RuleEditor> Rules { get; } = [];
    public ObservableCollection<UpstreamStatusView> UpstreamStatuses { get; } = [];
    public ObservableCollection<QueryView> Queries { get; } = [];

    public AsyncCommand ToggleProtectionCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand AddUpstreamCommand { get; }
    public RelayCommand RemoveUpstreamCommand { get; }
    public RelayCommand AddChainCommand { get; }
    public RelayCommand RemoveChainCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }

    public bool IsConnected { get => isConnected; private set { if (Set(ref isConnected, value)) { ToggleProtectionCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged(); } } }
    public bool IsActive { get => isActive; private set { if (Set(ref isActive, value)) { Raise(nameof(ProtectionText)); Raise(nameof(ToggleButtonText)); } } }
    public string ConnectionText { get => connectionText; private set => Set(ref connectionText, value); }
    public string ProtectionText => IsActive && !protectionConfirmed
        ? LocalizationManager.Get("Status.DriverUnavailable")
        : LocalizationManager.Get(IsActive ? "Protection.Active" : "Protection.Disabled");
    public string ToggleButtonText => LocalizationManager.Get(IsActive ? "Action.Disable" : "Action.Enable");
    public string EnforcementText { get => enforcementText; private set => Set(ref enforcementText, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string DefaultChainId { get => defaultChainId; set => Set(ref defaultChainId, value); }
    public bool EnforcementEnabled { get => enforcementEnabled; set => Set(ref enforcementEnabled, value); }
    public bool BlockPlaintextDns { get => blockPlaintextDns; set => Set(ref blockPlaintextDns, value); }
    public int WatchdogSeconds { get => watchdogSeconds; set => Set(ref watchdogSeconds, value); }
    public UpstreamEditor? SelectedUpstream { get => selectedUpstream; set => Set(ref selectedUpstream, value); }
    public ChainEditor? SelectedChain { get => selectedChain; set => Set(ref selectedChain, value); }
    public RuleEditor? SelectedRule { get => selectedRule; set => Set(ref selectedRule, value); }
    public string SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            if (Set(ref selectedLanguage, value))
            {
                LocalizationManager.Apply(value);
                RefreshLocalizedText();
            }
        }
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        timer.Start();
    }

    public void Dispose() => timer.Stop();

    private async void OnTimerTick(object? sender, EventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var snapshot = await client.SendAsync<ServiceSnapshot>("getState", false).ConfigureAwait(true);
            IsConnected = true;
            IsActive = snapshot.Configuration.Active;
            protectionConfirmed = snapshot.Enforcement.Active && snapshot.Enforcement.DriverReady && snapshot.Enforcement.LastError is null;
            Raise(nameof(ProtectionText));
            serviceVersion = snapshot.Version;
            lastConnectionError = null;
            lastEnforcement = snapshot.Enforcement;
            ConnectionText = LocalizationManager.Get("Connection.Version", snapshot.Version);
            EnforcementText = TranslateEnforcement(snapshot.Enforcement);
            StatusMessage = LocalizationManager.Get("Status.Synchronized");

            UpstreamStatuses.Clear();
            foreach (var upstream in snapshot.Upstreams)
            {
                UpstreamStatuses.Add(new(
                    upstream.Name,
                    upstream.Available,
                    upstream.LastLatencyMilliseconds is null ? "—" : $"{upstream.LastLatencyMilliseconds:0} ms",
                    upstream.LastError));
            }

            Queries.Clear();
            foreach (var query in snapshot.Queries)
            {
                Queries.Add(new(query.Timestamp.ToLocalTime().ToString("HH:mm:ss"), query.Domain, query.UpstreamName, query.Result));
            }

            if (!editorsLoaded)
            {
                LoadEditors(snapshot.Configuration);
                editorsLoaded = true;
            }
        }
        catch (Exception error)
        {
            IsConnected = false;
            protectionConfirmed = false;
            Raise(nameof(ProtectionText));
            lastConnectionError = error.Message;
            ConnectionText = LocalizationManager.Get("Connection.Unavailable");
            StatusMessage = LocalizationManager.Get("Status.ServiceUnavailable", error.Message);
        }
        finally { refreshing = false; }
    }

    private async Task ToggleProtectionAsync()
    {
        try
        {
            await client.SendAsync<BetterDnsConfiguration>("setActive", !IsActive).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var configuration = new BetterDnsConfiguration
            {
                Active = IsActive,
                DefaultChainId = DefaultChainId.Trim(),
                Upstreams = Upstreams.Select(static value => value.ToModel()).ToArray(),
                Chains = Chains.Select(static value => value.ToModel()).ToArray(),
                Rules = Rules.Select(static value => value.ToModel()).ToArray(),
                Enforcement = new()
                {
                    Enabled = EnforcementEnabled,
                    BlockPlaintextDns = BlockPlaintextDns,
                    WatchdogSeconds = WatchdogSeconds
                }
            };
            await client.SendAsync<BetterDnsConfiguration>("saveConfiguration", configuration).ConfigureAwait(true);
            StatusMessage = LocalizationManager.Get("Status.Saved");
        }
        catch (Exception error)
        {
            StatusMessage = LocalizationManager.Get("Status.SaveFailed", error.Message);
        }
    }

    private void LoadEditors(BetterDnsConfiguration configuration)
    {
        Upstreams.Clear();
        foreach (var value in configuration.Upstreams) Upstreams.Add(UpstreamEditor.From(value));
        Chains.Clear();
        foreach (var value in configuration.Chains) Chains.Add(ChainEditor.From(value));
        Rules.Clear();
        foreach (var value in configuration.Rules) Rules.Add(RuleEditor.From(value));
        DefaultChainId = configuration.DefaultChainId;
        EnforcementEnabled = configuration.Enforcement.Enabled;
        BlockPlaintextDns = configuration.Enforcement.BlockPlaintextDns;
        WatchdogSeconds = configuration.Enforcement.WatchdogSeconds;
    }

    private void RefreshLocalizedText()
    {
        ConnectionText = IsConnected && serviceVersion is not null
            ? LocalizationManager.Get("Connection.Version", serviceVersion)
            : LocalizationManager.Get("Connection.Unavailable");
        EnforcementText = lastEnforcement is null
            ? LocalizationManager.Get("Status.DriverStarting")
            : TranslateEnforcement(lastEnforcement);
        StatusMessage = IsConnected
            ? LocalizationManager.Get("Status.Synchronized")
            : lastConnectionError is null
                ? LocalizationManager.Get("Status.Connecting")
                : LocalizationManager.Get("Status.ServiceUnavailable", lastConnectionError);
        Raise(nameof(ProtectionText));
        Raise(nameof(ToggleButtonText));
    }

    private static string TranslateEnforcement(EnforcementSnapshot snapshot)
    {
        var version = ExtractWinDivertVersion(snapshot.Status);
        var translated = snapshot.Status switch
        {
            var status when status.Contains("unavailable", StringComparison.OrdinalIgnoreCase) =>
                LocalizationManager.Get("Status.DriverUnavailable"),
            var status when status.Contains("starting", StringComparison.OrdinalIgnoreCase) =>
                LocalizationManager.Get("Status.DriverStarting"),
            var status when status.Contains("WinDivert", StringComparison.OrdinalIgnoreCase) && snapshot.Active =>
                LocalizationManager.Get("Status.DriverActive", version),
            var status when status.Contains("WinDivert", StringComparison.OrdinalIgnoreCase) =>
                LocalizationManager.Get("Status.DriverReady", version),
            _ => snapshot.Status
        };

        return snapshot.LastError is null
            ? translated
            : LocalizationManager.Get("Status.RawError", translated, snapshot.LastError);
    }

    private static string ExtractWinDivertVersion(string status)
    {
        const string marker = "WinDivert ";
        var start = status.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        var remainder = status[(start + marker.Length)..];
        var end = remainder.IndexOf(' ');
        return end < 0 ? remainder : remainder[..end];
    }

    private sealed record ServiceSnapshot(
        string Version,
        BetterDnsConfiguration Configuration,
        IReadOnlyList<UpstreamStatus> Upstreams,
        IReadOnlyList<QueryLogEntry> Queries,
        EnforcementSnapshot Enforcement);

    private sealed record EnforcementSnapshot(bool Active, string Status, string? LastError, DateTimeOffset? LastChecked, bool DriverReady);
}
