using System.Collections.ObjectModel;
using BetterDns.Core.Configuration;
using BetterDns.Gui.Ipc;

namespace BetterDns.Gui.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ControlClient client = new();
    private readonly System.Windows.Threading.DispatcherTimer timer;
    private bool editorsLoaded;
    private bool isConnected;
    private bool isActive;
    private string connectionText = "Service unavailable";
    private string enforcementText = "Start the BetterDNS service to inspect enforcement.";
    private string statusMessage = "Connecting…";
    private string defaultChainId = string.Empty;
    private bool enforcementEnabled = true;
    private bool blockPlaintextDns = true;
    private int watchdogSeconds = 15;
    private UpstreamEditor? selectedUpstream;
    private ChainEditor? selectedChain;
    private RuleEditor? selectedRule;

    public MainViewModel()
    {
        ToggleProtectionCommand = new AsyncCommand(ToggleProtectionAsync, () => IsConnected);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SaveCommand = new AsyncCommand(SaveAsync, () => IsConnected);
        AddUpstreamCommand = new RelayCommand(() => Upstreams.Add(new()));
        RemoveUpstreamCommand = new RelayCommand(() => { if (SelectedUpstream is not null) Upstreams.Remove(SelectedUpstream); });
        AddChainCommand = new RelayCommand(() => Chains.Add(new()));
        RemoveChainCommand = new RelayCommand(() => { if (SelectedChain is not null) Chains.Remove(SelectedChain); });
        AddRuleCommand = new RelayCommand(() => Rules.Add(new()));
        RemoveRuleCommand = new RelayCommand(() => { if (SelectedRule is not null) Rules.Remove(SelectedRule); });
        timer = new() { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += OnTimerTick;
    }

    public IReadOnlyList<DnsProtocol> Protocols { get; } = Enum.GetValues<DnsProtocol>();
    public IReadOnlyList<DomainMatchKind> MatchKinds { get; } = Enum.GetValues<DomainMatchKind>();
    public IReadOnlyList<RuleAction> RuleActions { get; } = Enum.GetValues<RuleAction>();
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
    public string ProtectionText => IsActive ? "All Windows DNS is routed through BetterDNS." : "Protection is currently disabled.";
    public string ToggleButtonText => IsActive ? "Disable protection" : "Enable protection";
    public string EnforcementText { get => enforcementText; private set => Set(ref enforcementText, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string DefaultChainId { get => defaultChainId; set => Set(ref defaultChainId, value); }
    public bool EnforcementEnabled { get => enforcementEnabled; set => Set(ref enforcementEnabled, value); }
    public bool BlockPlaintextDns { get => blockPlaintextDns; set => Set(ref blockPlaintextDns, value); }
    public int WatchdogSeconds { get => watchdogSeconds; set => Set(ref watchdogSeconds, value); }
    public UpstreamEditor? SelectedUpstream { get => selectedUpstream; set => Set(ref selectedUpstream, value); }
    public ChainEditor? SelectedChain { get => selectedChain; set => Set(ref selectedChain, value); }
    public RuleEditor? SelectedRule { get => selectedRule; set => Set(ref selectedRule, value); }

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        timer.Start();
    }

    public void Dispose() => timer.Stop();

    private async void OnTimerTick(object? sender, EventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await client.SendAsync<ServiceSnapshot>("getState", false).ConfigureAwait(true);
            IsConnected = true;
            IsActive = snapshot.Configuration.Active;
            ConnectionText = $"Service {snapshot.Version}";
            EnforcementText = snapshot.Enforcement.LastError is null
                ? snapshot.Enforcement.Status
                : snapshot.Enforcement.Status + ": " + snapshot.Enforcement.LastError;
            StatusMessage = "Configuration synchronized with the service.";

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
            ConnectionText = "Service unavailable";
            StatusMessage = "BetterDNS service is not installed or not running: " + error.Message;
        }
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
            StatusMessage = "Configuration saved. New queries use it immediately.";
        }
        catch (Exception error)
        {
            StatusMessage = "Could not save: " + error.Message;
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

    private sealed record ServiceSnapshot(
        string Version,
        BetterDnsConfiguration Configuration,
        IReadOnlyList<UpstreamStatus> Upstreams,
        IReadOnlyList<QueryLogEntry> Queries,
        EnforcementSnapshot Enforcement);

    private sealed record EnforcementSnapshot(bool Active, string Status, string? LastError, DateTimeOffset? LastChecked, bool DriverReady);
}
