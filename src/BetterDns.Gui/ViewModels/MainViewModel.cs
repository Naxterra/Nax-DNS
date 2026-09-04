using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using BetterDns.Gui.Localization;

namespace BetterDns.Gui.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IControlClient client;
    private readonly System.Windows.Threading.DispatcherTimer timer;
    private readonly HashSet<ObservableObject> trackedEditors = [];
    private bool loaded, loading, refreshing, isConnected, isActive, isBusy, protectionConfirmed, routeEdited, hasUnsavedChanges;
    private string selectedLanguage = LocalizationManager.CurrentLanguage;
    private string defaultChainId = string.Empty;
    private bool enforcementEnabled = true, blockPlaintextDns = true;
    private int watchdogSeconds = 15, selectedTabIndex;
    private string statusMessage = LocalizationManager.Get("Status.Connecting");
    private string? actionError, noticeKey;
    private BetterDnsConfiguration? savedConfiguration;
    private ServiceSnapshot? lastSnapshot;
    private DateTimeOffset monitorSince = DateTimeOffset.MinValue;
    private UpstreamEditor? selectedUpstream, primaryProvider, backupProvider, selectedExtraFallback, extraFallbackCandidate;
    private ChainEditor? selectedChain;
    private RuleEditor? selectedRule;

    public MainViewModel() : this(new ControlClient()) { }

    public MainViewModel(IControlClient client)
    {
        this.client = client;
        ToggleProtectionCommand = new AsyncCommand(ToggleProtectionAsync, () => IsConnected && !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => IsConnected && !IsBusy);
        AddUpstreamCommand = new RelayCommand(() => EditProviderRequested?.Invoke(null));
        EditUpstreamCommand = new RelayCommand(() => { if (SelectedUpstream is not null) EditProviderRequested?.Invoke(SelectedUpstream); });
        RemoveUpstreamCommand = new RelayCommand(RemoveSelectedProvider);
        UsePrimaryCommand = new RelayCommand(() => { if (SelectedUpstream is not null) { PrimaryProvider = SelectedUpstream; SelectedTabIndex = 0; } });
        UseBackupCommand = new RelayCommand(() => { if (SelectedUpstream is not null) { BackupProvider = SelectedUpstream; SelectedTabIndex = 0; } });
        ClearBackupCommand = new RelayCommand(() => BackupProvider = null);
        AddExtraFallbackCommand = new RelayCommand(() => {
            if (ExtraFallbackCandidate == PrimaryProvider || ExtraFallbackCandidate == BackupProvider) { SetError("Setup.Duplicate"); return; }
            if (ExtraFallbackCandidate is not null && !AdditionalFallbacks.Contains(ExtraFallbackCandidate))
                AdditionalFallbacks.Add(ExtraFallbackCandidate);
        });
        RemoveExtraFallbackCommand = new RelayCommand(() => { if (SelectedExtraFallback is not null) AdditionalFallbacks.Remove(SelectedExtraFallback); });
        MoveExtraUpCommand = new RelayCommand(() => MoveExtra(-1));
        MoveExtraDownCommand = new RelayCommand(() => MoveExtra(1));
        AddChainCommand = new RelayCommand(() => {
            var chain = new ChainEditor { Id = "chain-" + Guid.NewGuid().ToString("N"), Name = LocalizationManager.Get("Editor.NewChain"), UpstreamIds = PrimaryProvider?.Id ?? Upstreams.FirstOrDefault()?.Id ?? "" };
            Chains.Add(chain); SelectedChain = chain;
        });
        RemoveChainCommand = new RelayCommand(() => {
            if (SelectedChain is null) return;
            if (SelectedChain.Id == DefaultChainId || Rules.Any(rule => rule.ChainId == SelectedChain.Id)) { SetError("Setup.ChainInUse"); return; }
            Chains.Remove(SelectedChain);
        });
        AddRuleCommand = new RelayCommand(() => {
            var rule = new RuleEditor { Name = LocalizationManager.Get("Editor.NewRule"), ChainId = DefaultChainId };
            Rules.Add(rule); SelectedRule = rule;
        });
        RemoveRuleCommand = new RelayCommand(() => { if (SelectedRule is not null) Rules.Remove(SelectedRule); });
        Upstreams.CollectionChanged += EditorsChanged;
        Chains.CollectionChanged += EditorsChanged;
        Rules.CollectionChanged += EditorsChanged;
        AdditionalFallbacks.CollectionChanged += (_, _) => RouteChanged();
        timer = new() { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += OnTimerTick;
    }

    public event Action<UpstreamEditor?>? EditProviderRequested;
    public bool IsProviderEditorOpen { get; set; }
    public IReadOnlyList<DnsProtocol> Protocols { get; } = Enum.GetValues<DnsProtocol>();
    public IReadOnlyList<DomainMatchKind> MatchKinds { get; } = Enum.GetValues<DomainMatchKind>();
    public IReadOnlyList<RuleAction> RuleActions { get; } = Enum.GetValues<RuleAction>();
    public IReadOnlyList<LanguageOption> Languages { get; } = [new("de", "Deutsch"), new("en", "English")];
    public ObservableCollection<UpstreamEditor> Upstreams { get; } = [];
    public ObservableCollection<ChainEditor> Chains { get; } = [];
    public ObservableCollection<RuleEditor> Rules { get; } = [];
    public ObservableCollection<UpstreamEditor> AdditionalFallbacks { get; } = [];
    public ObservableCollection<UpstreamStatusView> UpstreamStatuses { get; } = [];
    public ObservableCollection<QueryView> Queries { get; } = [];
    public AsyncCommand ToggleProtectionCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand AddUpstreamCommand { get; }
    public RelayCommand EditUpstreamCommand { get; }
    public RelayCommand RemoveUpstreamCommand { get; }
    public RelayCommand UsePrimaryCommand { get; }
    public RelayCommand UseBackupCommand { get; }
    public RelayCommand ClearBackupCommand { get; }
    public RelayCommand AddExtraFallbackCommand { get; }
    public RelayCommand RemoveExtraFallbackCommand { get; }
    public RelayCommand MoveExtraUpCommand { get; }
    public RelayCommand MoveExtraDownCommand { get; }
    public RelayCommand AddChainCommand { get; }
    public RelayCommand RemoveChainCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }

    public bool IsConnected { get => isConnected; private set { Set(ref isConnected, value); NotifyCommands(); } }
    public bool IsActive { get => isActive; private set => Set(ref isActive, value); }
    public bool IsBusy { get => isBusy; private set { Set(ref isBusy, value); Raise(nameof(CanEdit)); NotifyCommands(); } }
    public bool CanEdit => !IsBusy;
    public bool HasUnsavedChanges { get => hasUnsavedChanges; private set => Set(ref hasUnsavedChanges, value); }
    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string ConnectionText => IsConnected && lastSnapshot is not null ? L("Connection.Version", lastSnapshot.Version) : L("Connection.Unavailable");
    public string ProtectionText => !IsConnected ? L("Connection.Unavailable") : IsActive
        ? L(protectionConfirmed ? "Protection.Active" : "Status.DriverUnavailable") : L("Protection.Disabled");
    public string ToggleButtonText => L(IsActive ? "Action.Disable" : "Setup.SaveEnable");
    public string EnforcementText => lastSnapshot is null ? L("Status.DriverStarting") :
        lastSnapshot.Enforcement.LastError ?? L(lastSnapshot.Enforcement.DriverReady
            ? (protectionConfirmed ? "Setup.DriverActive" : "Setup.DriverReady") : "Status.DriverUnavailable");
    public string RouteSummary => string.Join("  →  ", OrderedProviders().Select(provider => provider.Name));
    public string ExtraFallbackSummary => L("Setup.ExtraCount", AdditionalFallbacks.Count);
    public string ConfiguredRouteText => HasUnsavedChanges ? L("Setup.NotApplied") : L(IsActive ? "Setup.Applied" : "Setup.SavedOff");
    public string PrimaryDetails => ProviderDetails(PrimaryProvider);
    public string BackupDetails => ProviderDetails(BackupProvider);
    public string RulesSummary => L("Setup.RulesCount", Rules.Count(rule => rule.Enabled));
    public string LiveResolverText { get; private set; } = LocalizationManager.Get("Setup.RoutingOff");
    public string LiveRouteDetail { get; private set; } = string.Empty;
    public bool LastResponseUsedBackup { get; private set; }
    public int SelectedTabIndex { get => selectedTabIndex; set => Set(ref selectedTabIndex, value); }
    public string DefaultChainId { get => defaultChainId; set { if (Set(ref defaultChainId, value) && !loading) { LoadRouteFromEditors(); DraftChanged(); } } }
    public bool EnforcementEnabled { get => enforcementEnabled; set { if (Set(ref enforcementEnabled, value)) DraftChanged(); } }
    public bool BlockPlaintextDns { get => blockPlaintextDns; set { if (Set(ref blockPlaintextDns, value)) DraftChanged(); } }
    public int WatchdogSeconds { get => watchdogSeconds; set { if (Set(ref watchdogSeconds, value)) DraftChanged(); } }
    public UpstreamEditor? SelectedUpstream { get => selectedUpstream; set => Set(ref selectedUpstream, value); }
    public ChainEditor? SelectedChain { get => selectedChain; set => Set(ref selectedChain, value); }
    public RuleEditor? SelectedRule { get => selectedRule; set => Set(ref selectedRule, value); }
    public UpstreamEditor? PrimaryProvider
    {
        get => primaryProvider;
        set
        {
            var previous = primaryProvider;
            if (!Set(ref primaryProvider, value)) return;
            if (!loading && value is not null)
            {
                if (backupProvider == value) { backupProvider = previous; Raise(nameof(BackupProvider)); }
                SwapExtra(value, previous);
            }
            RouteChanged();
        }
    }
    public UpstreamEditor? BackupProvider
    {
        get => backupProvider;
        set
        {
            var previous = backupProvider;
            if (!Set(ref backupProvider, value)) return;
            if (!loading && value is not null)
            {
                if (primaryProvider == value) { primaryProvider = previous; Raise(nameof(PrimaryProvider)); }
                SwapExtra(value, previous);
            }
            RouteChanged();
        }
    }
    public UpstreamEditor? SelectedExtraFallback { get => selectedExtraFallback; set => Set(ref selectedExtraFallback, value); }
    public UpstreamEditor? ExtraFallbackCandidate { get => extraFallbackCandidate; set => Set(ref extraFallbackCandidate, value); }
    public string SelectedLanguage
    {
        get => selectedLanguage;
        set { if (Set(ref selectedLanguage, value)) { LocalizationManager.Apply(value); UpdatePresentation(); } }
    }

    public async Task InitializeAsync() { await RefreshAsync(); timer.Start(); }
    public void Dispose() { timer.Stop(); foreach (var editor in trackedEditors) editor.PropertyChanged -= EditorChanged; }
    private async void OnTimerTick(object? sender, EventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        if (refreshing || IsBusy || IsProviderEditorOpen) return;
        refreshing = true;
        try
        {
            var snapshot = await client.SendAsync<ServiceSnapshot>("getState", false);
            // Incoming state must not wipe unsaved edits. A save checks for external conflicts.
            if (!loaded || (!HasUnsavedChanges && savedConfiguration is not null && Fingerprint(savedConfiguration) != Fingerprint(snapshot.Configuration)))
            {
                if (loaded) monitorSince = DateTimeOffset.UtcNow;
                LoadEditors(snapshot.Configuration);
            }
            if (loaded && !IsActive && snapshot.Configuration.Active) monitorSince = DateTimeOffset.UtcNow;
            lastSnapshot = snapshot;
            IsConnected = true;
            IsActive = snapshot.Configuration.Active;
            protectionConfirmed = IsActive && snapshot.Enforcement.Active && snapshot.Enforcement.DriverReady && snapshot.Enforcement.LastError is null;
            loaded = true;
            UpdatePresentation();
        }
        catch (Exception error)
        {
            IsConnected = false;
            protectionConfirmed = false;
            StatusMessage = L("Status.ServiceUnavailable", error.Message);
            UpdateLiveRoute();
            Raise(nameof(ConnectionText)); Raise(nameof(ProtectionText));
        }
        finally { refreshing = false; }
    }

    public Task SaveAsync() => RunActionAsync(async () => { await PersistAsync(); noticeKey = "Status.Saved"; });

    public Task ToggleProtectionAsync() => RunActionAsync(async () =>
    {
        if (IsActive)
        {
            // Turning off must work even when the user's draft is invalid.
            await client.SendAsync<BetterDnsConfiguration>("setActive", false);
            noticeKey = "Setup.Disabled";
        }
        else
        {
            await PersistAsync();
            await client.SendAsync<BetterDnsConfiguration>("setActive", true);
            monitorSince = DateTimeOffset.UtcNow;
            noticeKey = "Setup.Enabled";
        }
    });

    private async Task RunActionAsync(Func<Task> action)
    {
        if (IsBusy || !IsConnected) return;
        IsBusy = true;
        actionError = null;
        StatusMessage = L("Setup.Working");
        try { await action(); }
        catch (Exception error) { actionError = error.Message; }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private async Task PersistAsync()
    {
        var current = await client.SendAsync<ServiceSnapshot>("getState", false);
        if (savedConfiguration is not null && Fingerprint(current.Configuration) != Fingerprint(savedConfiguration))
            throw new InvalidOperationException(L("Setup.Conflict"));
        var configuration = BuildConfiguration(current.Configuration.Active);
        var saved = await client.SendAsync<BetterDnsConfiguration>("saveConfiguration", configuration);
        if (savedConfiguration is null || Fingerprint(savedConfiguration) != Fingerprint(saved)) monitorSince = DateTimeOffset.UtcNow;
        LoadEditors(saved);
    }

    public BetterDnsConfiguration BuildConfiguration(bool active)
    {
        if (PrimaryProvider is null) throw new InvalidOperationException(L("Setup.ChoosePrimary"));
        var providers = OrderedProviders().ToArray();
        if (providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != providers.Length)
            throw new InvalidOperationException(L("Setup.Duplicate"));
        if (providers.Any(provider => !provider.Enabled || !Upstreams.Contains(provider)))
            throw new InvalidOperationException(L("Setup.DisabledProvider"));
        foreach (var provider in providers)
            if (ProviderValidation.Problem(provider.ToModel()) is { } key)
                throw new InvalidOperationException(L("Setup.ProviderProblem", provider.Name, L(key)));

        var configuration = new BetterDnsConfiguration
        {
            SchemaVersion = savedConfiguration?.SchemaVersion ?? 1,
            Active = active,
            DefaultChainId = DefaultChainId.Trim(),
            Upstreams = Upstreams.Select(value => value.ToModel()).ToArray(),
            Chains = Chains.Select(value => value.ToModel()).ToArray(),
            Rules = Rules.Select(value => value.ToModel()).ToArray(),
            Enforcement = new() { Enabled = EnforcementEnabled, BlockPlaintextDns = BlockPlaintextDns, WatchdogSeconds = WatchdogSeconds }
        };
        return routeEdited ? DefaultRouteEditor.Apply(configuration, providers.Select(provider => provider.Id).ToArray()) : configuration;
    }

    public void AcceptProvider(UpstreamEditor edited, string useAs)
    {
        var existing = Upstreams.FirstOrDefault(provider => provider.Id == edited.Id);
        if (existing is null) { Upstreams.Add(edited); existing = edited; }
        else
        {
            existing.Name = edited.Name; existing.Endpoint = edited.Endpoint; existing.Protocol = edited.Protocol;
            existing.BootstrapAddresses = edited.BootstrapAddresses; existing.TimeoutMilliseconds = edited.TimeoutMilliseconds;
            existing.Enabled = edited.Enabled;
        }
        SelectedUpstream = existing;
        if (useAs == "primary") { PrimaryProvider = existing; SelectedTabIndex = 0; }
        else if (useAs == "backup") { BackupProvider = existing; SelectedTabIndex = 0; }
        else SelectedTabIndex = 1;
        DraftChanged();
    }

    private void RemoveSelectedProvider()
    {
        if (SelectedUpstream is null) return;
        var id = SelectedUpstream.Id;
        if (OrderedProviders().Contains(SelectedUpstream) || Chains.Any(chain => chain.ToModel().UpstreamIds.Contains(id)))
        { SetError("Setup.ProviderInUse"); return; }
        Upstreams.Remove(SelectedUpstream);
    }

    private void MoveExtra(int direction)
    {
        if (SelectedExtraFallback is null) return;
        var index = AdditionalFallbacks.IndexOf(SelectedExtraFallback);
        var next = index + direction;
        if (next >= 0 && next < AdditionalFallbacks.Count) AdditionalFallbacks.Move(index, next);
    }

    private void SwapExtra(UpstreamEditor promoted, UpstreamEditor? previous)
    {
        var index = AdditionalFallbacks.IndexOf(promoted);
        if (index < 0) return;
        if (previous is null) AdditionalFallbacks.RemoveAt(index);
        else AdditionalFallbacks[index] = previous;
    }

    private void LoadEditors(BetterDnsConfiguration configuration)
    {
        loading = true;
        var selectedId = SelectedUpstream?.Id;
        try
        {
            Upstreams.Clear(); foreach (var item in configuration.Upstreams) Upstreams.Add(UpstreamEditor.From(item));
            Chains.Clear(); foreach (var item in configuration.Chains) Chains.Add(ChainEditor.From(item));
            Rules.Clear(); foreach (var item in configuration.Rules) Rules.Add(RuleEditor.From(item));
            DefaultChainId = configuration.DefaultChainId;
            EnforcementEnabled = configuration.Enforcement.Enabled;
            BlockPlaintextDns = configuration.Enforcement.BlockPlaintextDns;
            WatchdogSeconds = configuration.Enforcement.WatchdogSeconds;
            LoadRouteFromEditors();
            SelectedUpstream = Upstreams.FirstOrDefault(provider => provider.Id == selectedId);
            savedConfiguration = configuration;
            HasUnsavedChanges = false;
            routeEdited = false;
        }
        finally { loading = false; }
        TrackEditors();
    }

    private void LoadRouteFromEditors()
    {
        var wasLoading = loading;
        loading = true;
        try
        {
            var ids = Chains.FirstOrDefault(chain => chain.Id == DefaultChainId)?.ToModel().UpstreamIds ?? [];
            PrimaryProvider = Upstreams.FirstOrDefault(provider => provider.Id == ids.FirstOrDefault());
            BackupProvider = Upstreams.FirstOrDefault(provider => provider.Id == ids.Skip(1).FirstOrDefault());
            AdditionalFallbacks.Clear();
            foreach (var id in ids.Skip(2))
                if (Upstreams.FirstOrDefault(provider => provider.Id == id) is { } provider) AdditionalFallbacks.Add(provider);
            routeEdited = false;
        }
        finally { loading = wasLoading; }
        NotifyRoute();
    }

    private void EditorsChanged(object? sender, NotifyCollectionChangedEventArgs e) { TrackEditors(); DraftChanged(); }
    private void TrackEditors()
    {
        var current = Upstreams.Cast<ObservableObject>().Concat(Chains).Concat(Rules).ToHashSet();
        foreach (var item in trackedEditors.Except(current).ToArray()) { item.PropertyChanged -= EditorChanged; trackedEditors.Remove(item); }
        foreach (var item in current.Except(trackedEditors).ToArray()) { item.PropertyChanged += EditorChanged; trackedEditors.Add(item); }
    }
    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ChainEditor chain && chain.Id == DefaultChainId && e.PropertyName == nameof(ChainEditor.UpstreamIds) && !loading) LoadRouteFromEditors();
        DraftChanged();
    }
    private void RouteChanged() { if (!loading) { routeEdited = true; DraftChanged(); } NotifyRoute(); }
    private void DraftChanged()
    {
        if (loading) return;
        HasUnsavedChanges = true;
        actionError = null; noticeKey = null;
        StatusMessage = L("Setup.Unsaved");
        NotifyRoute();
    }
    private void NotifyRoute()
    {
        Raise(nameof(RouteSummary)); Raise(nameof(ExtraFallbackSummary)); Raise(nameof(ConfiguredRouteText));
        Raise(nameof(PrimaryDetails)); Raise(nameof(BackupDetails)); Raise(nameof(RulesSummary));
    }
    private IEnumerable<UpstreamEditor> OrderedProviders()
    {
        if (PrimaryProvider is not null) yield return PrimaryProvider;
        if (BackupProvider is not null) yield return BackupProvider;
        foreach (var provider in AdditionalFallbacks) yield return provider;
    }
    private static string Fingerprint(BetterDnsConfiguration configuration) => JsonSerializer.Serialize(configuration with { Active = false }, JsonSettings.Wire);
    private static string L(string key, params object?[] args) => LocalizationManager.Get(key, args);
    private static string ProviderDetails(UpstreamEditor? provider) => provider is null ? L("Setup.NoneSelected") :
        $"{ProtocolName(provider.Protocol)} · {L(provider.Enabled ? "Setup.ProviderEnabled" : "Setup.ProviderDisabled")}";
    public static string ProtocolName(DnsProtocol protocol) => protocol switch
    { DnsProtocol.Doh3 => "DoH3", DnsProtocol.Doh => "DoH", DnsProtocol.Dot => "DoT", DnsProtocol.Doq => "DoQ", _ => protocol.ToString() };
    private void SetError(string key) { actionError = L(key); StatusMessage = actionError; }
    private void NotifyCommands() { ToggleProtectionCommand?.RaiseCanExecuteChanged(); SaveCommand?.RaiseCanExecuteChanged(); RefreshCommand?.RaiseCanExecuteChanged(); }

    private void UpdatePresentation()
    {
        StatusMessage = actionError ?? (HasUnsavedChanges ? L("Setup.Unsaved") : L(noticeKey ?? "Status.Synchronized"));
        UpstreamStatuses.Clear(); Queries.Clear();
        if (lastSnapshot is { } snapshot)
        {
            foreach (var upstream in snapshot.Upstreams)
            {
                var enabled = snapshot.Configuration.Upstreams.FirstOrDefault(provider => provider.Id == upstream.Id)?.Enabled == true;
                var key = !enabled ? "Health.Disabled" : upstream.LastError is not null ? "Health.Failed" :
                    upstream.LastLatencyMilliseconds is null ? "Health.Untested" : "Health.Answered";
                UpstreamStatuses.Add(new(upstream.Name, L(key), upstream.LastLatencyMilliseconds is { } latency ? $"{latency:0} ms" : "—", upstream.LastError));
            }
            foreach (var query in snapshot.Queries)
                Queries.Add(new(query.Timestamp.ToLocalTime().ToString("HH:mm:ss"), query.Domain, query.UpstreamName, query.Result));
        }
        Raise(nameof(ConnectionText)); Raise(nameof(ProtectionText)); Raise(nameof(ToggleButtonText)); Raise(nameof(EnforcementText));
        NotifyRoute(); UpdateLiveRoute();
    }

    private void UpdateLiveRoute()
    {
        LastResponseUsedBackup = false;
        LiveResolverText = L(!IsConnected ? "Connection.Unavailable" : !IsActive ? "Setup.RoutingOff" : !protectionConfirmed ? "Status.DriverUnavailable" : "Setup.Waiting");
        LiveRouteDetail = L("Setup.LiveHint");
        if (IsConnected && protectionConfirmed && lastSnapshot is { } snapshot)
        {
            var configuration = snapshot.Configuration;
            var last = snapshot.Queries.FirstOrDefault(query => query.ChainId == configuration.DefaultChainId && query.Timestamp >= monitorSince);
            if (last is not null)
            {
                if (last.UpstreamId is null) { LiveResolverText = L("Setup.RouteFailed"); LiveRouteDetail = last.Result; }
                else
                {
                    var primaryId = configuration.Chains.First(chain => chain.Id == configuration.DefaultChainId).UpstreamIds.FirstOrDefault();
                    LastResponseUsedBackup = last.UpstreamId != primaryId;
                    LiveResolverText = $"{last.UpstreamName} · {(last.Protocol is { } protocol ? ProtocolName(protocol) : "—")}";
                    LiveRouteDetail = L(LastResponseUsedBackup ? "Setup.BackupUsed" : "Setup.PrimaryUsed", last.Timestamp.ToLocalTime().ToString("HH:mm:ss"), $"{last.ElapsedMilliseconds:0}");
                }
            }
        }
        Raise(nameof(LiveResolverText)); Raise(nameof(LiveRouteDetail)); Raise(nameof(LastResponseUsedBackup));
    }
}
