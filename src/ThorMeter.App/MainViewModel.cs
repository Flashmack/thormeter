using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ThorMeter.Core;
using ThorMeter.Infrastructure;

namespace ThorMeter.App;

public sealed record AdapterChoice(string Id, string Description);
public sealed record FlowChoice(string Id, string Label);
public sealed record TargetCategoryChoice(EntityKind? Kind, string Label);
public enum ManualTargetTag { None, Boss, Dummy }
public sealed record TargetTagChoice(ManualTargetTag Tag, string Label);
public sealed class TargetChoice(ulong? id, string label) : INotifyPropertyChanged
{
    public ulong? Id { get; } = id;
    private string name = label;
    public string Label { get => name; set { if (name == value) return; name = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
    public string SearchName { get; set; } = "";
    public EntityKind Kind { get; set; }
    public long Damage { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PlayerRow : INotifyPropertyChanged
{
    private readonly Action<PlayerRow> pinChanged;
    private bool isPinned;
    public ulong EntityId { get; }
    public string Rank { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Class { get; private set; } = "";
    public long Damage { get; private set; }
    public double Dps { get; private set; }
    public long Healing { get; private set; }
    public double Hps { get; private set; }
    public Brush Color { get; private set; } = Brushes.Silver;
    public bool IsPinned { get => isPinned; set { if (isPinned == value) return; isPinned = value; Changed(); pinChanged(this); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public PlayerRow(ulong entityId, Action<PlayerRow> pinChanged) { EntityId = entityId; this.pinChanged = pinChanged; }

    public void Update(string rank, string name, string playerClass, long damage, double dps, long healing, double hps, Brush color)
    {
        Rank = rank; Name = name; Class = playerClass; Damage = damage; Dps = dps; Healing = healing; Hps = hps; Color = color;
        Changed(nameof(Rank)); Changed(nameof(Name)); Changed(nameof(Class)); Changed(nameof(Damage));
        Changed(nameof(Dps)); Changed(nameof(Healing)); Changed(nameof(Hps)); Changed(nameof(Color));
    }
    public void SetPinnedSilently(bool pinned) { isPinned = pinned; Changed(nameof(IsPinned)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CombatEngine engine = new();
    private readonly object engineGate = new();
    private readonly MeterCaptureService capture = new();
    private readonly Dictionary<ulong, PlayerRow> rowsById = new();
    private readonly HashSet<ulong> pinnedIds = new();
    private readonly Dictionary<ulong, string> aliases = new();
    private readonly Dictionary<ulong, TargetChoice> targetChoicesById = new();
    private readonly Dictionary<ulong, string> targetAliases = new();
    private readonly Dictionary<ulong, ManualTargetTag> targetTags = new();
    private IReadOnlyList<TargetStatistics> latestTargetStats = [];
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? replayCancellation;
    private bool running, replaying, disposed, refreshingAdapters, paused;
    private Task<IReadOnlyList<CaptureAdapterInfo>>? adapterDiscovery;
    private string mode = UiText.Get("ModeLive"), status = UiText.Get("StatusStoppedSelectAdapter"), health = UiText.Get("HealthProtocolUnvalidated");
    private string? pendingDiagnostic;
    private int flowsDirty;
    private int sessionDirty;
    private bool updatingTargets;
    private string? currentConnectionId;
    private DateTimeOffset? replayClock, stoppedClock;
    private DateTimeOffset demoNext;
    private int demoIndex;
    private bool alwaysOnTop, record, showHps;
    private string aliasInput = "";
    private string targetAliasInput = "", targetSearch = "";
    private RankMetric rankMetric = RankMetric.Dps;
    private AdapterChoice? selectedAdapter;
    private FlowChoice? selectedFlow;
    private TargetChoice? selectedTarget;
    private TargetCategoryChoice? selectedTargetCategory;
    private TargetTagChoice? selectedTargetTag;
    private PlayerRow? selectedPlayer;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string[] Modes { get; } = [UiText.Get("ModeLive"), UiText.Get("ModeDemo"), UiText.Get("ModeReplay")];
    public ObservableCollection<AdapterChoice> Adapters { get; } = new();
    public ObservableCollection<FlowChoice> Flows { get; } = new();
    public ObservableCollection<TargetChoice> Targets { get; } = new();
    public TargetCategoryChoice[] TargetCategories { get; } =
    [
        new(null, UiText.Get("TargetCategoryAll")),
        new(EntityKind.Player, UiText.Get("TargetCategoryPlayers")),
        new(EntityKind.Npc, UiText.Get("TargetCategoryNpcs")),
        new(EntityKind.Pet, UiText.Get("TargetCategoryPets")),
        new(EntityKind.Unknown, UiText.Get("TargetCategoryUnknown"))
    ];
    public TargetTagChoice[] TargetTags { get; } =
    [
        new(ManualTargetTag.None, UiText.Get("TargetTagNone")),
        new(ManualTargetTag.Boss, UiText.Get("TargetTagBoss")),
        new(ManualTargetTag.Dummy, UiText.Get("TargetTagDummy"))
    ];
    public ObservableCollection<PlayerRow> Rows { get; } = new();
    public string Mode { get => mode; set { if (CanConfigure) { mode = value; Changed(); } } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string Health { get => health; private set { health = value; Changed(); } }
    public string Progress { get; private set; } = "";
    public string Warning { get; private set; } = "";
    public string DurationLabel { get; private set; } = UiText.Format("ProgressActiveTime", 0d);
    public string ReplayFolder => ReplayDirectory;
    public bool CanConfigure => !running;
    public bool AlwaysOnTop { get => alwaysOnTop; set { alwaysOnTop = value; Changed(); } }
    public bool Record { get => record; set { record = value; Changed(); } }
    public bool ShowHps { get => showHps; set { showHps = value; Changed(); if (!value && rankMetric == RankMetric.Hps) SetRankMetric(RankMetric.Dps); UpdateRows(); } }
    public bool IsPaused => paused;
    public string PauseLabel => UiText.Get(paused ? "Resume" : "Pause");
    public string DamageScopeLabel => SelectedTarget?.Id is ulong id ? UiText.Format("ObservedTarget", SelectedTarget.Label) : UiText.Get("ObservedDamage");
    public bool CanEditSelectedTarget => SelectedTarget?.Id is not null;
    public string AliasInput { get => aliasInput; set { aliasInput = value; Changed(); CommandManager.InvalidateRequerySuggested(); } }
    public string TargetAliasInput { get => targetAliasInput; set { targetAliasInput = value; Changed(); CommandManager.InvalidateRequerySuggested(); } }
    public string TargetSearch { get => targetSearch; set { targetSearch = value; Changed(); RefreshTargetChoices(); } }
    public TargetCategoryChoice? SelectedTargetCategory { get => selectedTargetCategory; set { selectedTargetCategory = value; Changed(); RefreshTargetChoices(); } }
    public TargetTagChoice? SelectedTargetTag
    {
        get => selectedTargetTag;
        set
        {
            selectedTargetTag = value; Changed();
            if (selectedTarget?.Id is not ulong id || value is null) return;
            if (value.Tag == ManualTargetTag.None) targetTags.Remove(id);
            else targetTags[id] = value.Tag;
            RefreshTargets(latestTargetStats);
        }
    }
    public PlayerRow? SelectedPlayer { get => selectedPlayer; set { selectedPlayer = value; AliasInput = value is not null && aliases.TryGetValue(value.EntityId, out var alias) ? alias : ""; Changed(); CommandManager.InvalidateRequerySuggested(); } }
    public TargetChoice? SelectedTarget
    {
        get => selectedTarget;
        set
        {
            selectedTarget = value;
            targetAliasInput = value?.Id is ulong id && targetAliases.TryGetValue(id, out var alias) ? alias : "";
            selectedTargetTag = TargetTags.FirstOrDefault(x => x.Tag == (value?.Id is ulong targetId && targetTags.TryGetValue(targetId, out var tag) ? tag : ManualTargetTag.None));
            Changed(); Changed(nameof(TargetAliasInput)); Changed(nameof(SelectedTargetTag)); Changed(nameof(DamageScopeLabel)); Changed(nameof(CanEditSelectedTarget));
            CommandManager.InvalidateRequerySuggested();
            if (!updatingTargets) { RefreshTargetChoices(); UpdateRows(); }
        }
    }
    public AdapterChoice? SelectedAdapter { get => selectedAdapter; set { selectedAdapter = value; Changed(); } }
    public FlowChoice? SelectedFlow { get => selectedFlow; set { selectedFlow = value; Changed(); } }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand SelectFlowCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand SetAliasCommand { get; }
    public ICommand ClearAliasCommand { get; }
    public ICommand SetTargetAliasCommand { get; }
    public ICommand ClearTargetAliasCommand { get; }
    public ICommand SelectMainTargetCommand { get; }
    public ICommand CopyHealDiagnosticsCommand { get; }

    public MainViewModel()
    {
        StartCommand = new Command(Start, () => !running);
        StopCommand = new Command(Stop, () => running);
        ResetCommand = new Command(ResetTotals);
        RefreshCommand = new Command(async () => await RefreshAdaptersAsync(), () => !running && !refreshingAdapters);
        ReplayCommand = new Command(async () => await Replay(), () => !running);
        SelectFlowCommand = new Command(SelectFlow, () => running && !replaying && Mode == UiText.Get("ModeLive") && SelectedFlow != null);
        PauseCommand = new Command(TogglePause, () => running && !replaying);
        SetAliasCommand = new Command(SetAlias, () => SelectedPlayer is not null && !string.IsNullOrWhiteSpace(AliasInput));
        ClearAliasCommand = new Command(ClearAlias, () => SelectedPlayer is not null && aliases.ContainsKey(SelectedPlayer.EntityId));
        SetTargetAliasCommand = new Command(SetTargetAlias, () => SelectedTarget?.Id is not null && !string.IsNullOrWhiteSpace(TargetAliasInput));
        ClearTargetAliasCommand = new Command(ClearTargetAlias, () => SelectedTarget?.Id is ulong id && targetAliases.ContainsKey(id));
        SelectMainTargetCommand = new Command(SelectMainTarget, () => TargetSelection.MainCandidate(latestTargetStats) is not null);
        CopyHealDiagnosticsCommand = new Command(CopyHealDiagnostics);
        Targets.Add(new TargetChoice(null, UiText.Get("AllTargets")));
        selectedTarget = Targets[0];
        selectedTargetCategory = TargetCategories[0];
        selectedTargetTag = TargetTags[0];
        capture.CombatEventReceived += OnCombatEvent;
        capture.Diagnostic += message => Interlocked.Exchange(ref pendingDiagnostic, message);
        capture.FlowsChanged += () => Interlocked.Exchange(ref flowsDirty, 1);
        capture.CaptureTimeChanged += time => { lock (engineGate) { if (replaying) replayClock = time; } };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        _ = RefreshAdaptersAsync();
    }

    private async Task RefreshAdaptersAsync()
    {
        try
        {
            refreshingAdapters = true;
            CommandManager.InvalidateRequerySuggested();
            Status = UiText.Get("StatusDiscovering");
            adapterDiscovery ??= Task.Run(MeterCaptureService.ListAdapters);
            var discovered = await adapterDiscovery.WaitAsync(TimeSpan.FromSeconds(10));
            adapterDiscovery = null;
            if (disposed) return;
            var previous = SelectedAdapter?.Id;
            Adapters.Clear();
            foreach (var adapter in discovered) Adapters.Add(new(adapter.Id, adapter.Name));
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == previous) ?? Adapters.FirstOrDefault();
            if (Adapters.Count > 0 && !running) Status = UiText.Get("StatusStoppedChooseAdapter");
            if (Adapters.Count == 0) Status = UiText.Get("StatusNoAdapters");
        }
        catch (TimeoutException) { Status = UiText.Get("StatusDiscoveryTimeout"); }
        catch (Exception ex) { adapterDiscovery = null; Status = UiText.Format("StatusCaptureUnavailable", ex.Message); }
        finally { refreshingAdapters = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private void Start()
    {
        if (Mode == UiText.Get("ModeReplay")) { _ = Replay(); return; }
        try
        {
            string? recordingPath = null;
            if (Mode == UiText.Get("ModeLive"))
            {
                if (SelectedAdapter == null) { Status = UiText.Get("StatusChooseAdapter"); return; }
                if (Record)
                {
                    Directory.CreateDirectory(ReplayDirectory);
#if DEBUG
                    const string prefix = "thor-debug-";
#else
                    const string prefix = "thor-";
#endif
                    recordingPath = Path.Combine(ReplayDirectory, $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
                }
            }
            replayClock = null; stoppedClock = null;
            lock (engineGate) engine.Apply(new ConnectionEvent(DateTimeOffset.UtcNow, Guid.NewGuid().ToString(), true));
            ResetSessionUi(); Flows.Clear(); SelectedFlow = null;
            if (Mode == UiText.Get("ModeDemo"))
            {
                demoNext = DateTimeOffset.UtcNow; demoIndex = 0;
                Health = UiText.Get("HealthDemo");
                Status = UiText.Get("StatusDemoRunning");
            }
            else
            {
                Health = UiText.Get("HealthAwaitingTraffic");
                capture.Start(SelectedAdapter!.Id, recordingPath);
                Status = UiText.Get(Record ? "StatusLiveRecording" : "StatusLive");
            }
            SetRunning(true);
        }
        catch (Exception ex) { capture.Stop(); Status = UiText.Format("StatusCouldNotStart", ex.Message); SetRunning(false); }
    }

    private void Stop()
    {
        replayCancellation?.Cancel();
        if (paused && !replaying) TogglePause();
        capture.Stop();
        stoppedClock = DateTimeOffset.UtcNow.AddSeconds(CombatEngine.IdleSeconds);
        if (!replaying) SetRunning(false);
        Status = UiText.Get(replaying ? "StatusStoppingReplay" : "StatusStoppedTotalsRetained");
    }

    private void ResetTotals()
    {
        var now = DateTimeOffset.UtcNow;
        if (running && !replaying && Mode == UiText.Get("ModeLive")) capture.ResetTotals(now);
        else lock (engineGate) engine.Apply(new ResetEvent(now));
        UpdateRows();
    }

    private void SelectFlow()
    {
        try { if (SelectedFlow is not null) { capture.SelectFlow(SelectedFlow.Id); Status = UiText.Format("StatusSelectedConnection", SelectedFlow.Label); } }
        catch (Exception ex) { Health = UiText.Format("HealthSelectionFailed", ex.Message); }
    }

    private static string ReplayDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThorMeter", "Replays");

    private void TogglePause()
    {
        var next = !paused;
        var now = DateTimeOffset.UtcNow;
        if (Mode == UiText.Get("ModeLive")) capture.SetPaused(next, now);
        else lock (engineGate) engine.Apply(new PauseEvent(now, next));
        paused = next;
        Changed(nameof(IsPaused)); Changed(nameof(PauseLabel));
    }

    private void SetAlias()
    {
        if (SelectedPlayer is null) return;
        var alias = AliasInput.Trim();
        if (alias.Length == 0) return;
        aliases[SelectedPlayer.EntityId] = alias;
        CommandManager.InvalidateRequerySuggested();
        UpdateRows();
    }

    private void ClearAlias()
    {
        if (SelectedPlayer is null) return;
        aliases.Remove(SelectedPlayer.EntityId);
        AliasInput = "";
        UpdateRows();
    }

    private void SetTargetAlias()
    {
        if (SelectedTarget?.Id is not ulong id) return;
        var alias = TargetAliasInput.Trim();
        if (alias.Length == 0) return;
        targetAliases[id] = alias;
        RefreshTargets(latestTargetStats);
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearTargetAlias()
    {
        if (SelectedTarget?.Id is not ulong id) return;
        targetAliases.Remove(id);
        TargetAliasInput = "";
        RefreshTargets(latestTargetStats);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectMainTarget()
    {
        var candidate = TargetSelection.MainCandidate(latestTargetStats);
        if (candidate is not null && targetChoicesById.TryGetValue(candidate.EntityId, out var choice)) SelectedTarget = choice;
    }

    private void CopyHealDiagnostics()
    {
        var diagnostics = capture.HealDiagnostics;
        if (diagnostics.Count == 0) { Status = UiText.Get("NoHealDiagnostics"); return; }
        var output = new StringBuilder(UiText.Get("HealDiagnosticsNote")).AppendLine();
        output.AppendLine("opcode,source_id,target_id,skill_id,event_count,amount_sum,largest_amount");
        foreach (var item in diagnostics.OrderByDescending(x => x.TotalHealing))
            output.Append(item.Opcode.ToString("X4", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.SourceId.ToString("X", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.TargetId.ToString("X", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.SkillId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.EventCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.TotalHealing.ToString(CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(item.MaxHealing.ToString(CultureInfo.InvariantCulture));
        try { Clipboard.SetText(output.ToString()); Status = UiText.Get("HealDiagnosticsCopied"); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Status = UiText.Format("HealDiagnosticsCopyFailed", ex.Message); }
    }

    private void OnPinChanged(PlayerRow row)
    {
        if (row.IsPinned) pinnedIds.Add(row.EntityId);
        else pinnedIds.Remove(row.EntityId);
        UpdateRows();
    }

    public void SetRankMetric(RankMetric metric)
    {
        if (metric == RankMetric.Hps && !showHps) return;
        rankMetric = metric;
        UpdateRows();
    }

    private void ResetSessionUi()
    {
        pinnedIds.Clear(); aliases.Clear(); rowsById.Clear(); Rows.Clear(); SelectedPlayer = null;
        targetChoicesById.Clear(); targetAliases.Clear(); targetTags.Clear(); latestTargetStats = [];
        updatingTargets = true;
        try
        {
            Targets.Clear();
            var all = new TargetChoice(null, UiText.Get("AllTargets"));
            Targets.Add(all); selectedTarget = all;
            Changed(nameof(SelectedTarget)); Changed(nameof(DamageScopeLabel)); Changed(nameof(CanEditSelectedTarget));
        }
        finally { updatingTargets = false; }
        targetAliasInput = ""; targetSearch = ""; selectedTargetCategory = TargetCategories[0]; selectedTargetTag = TargetTags[0];
        Changed(nameof(TargetAliasInput)); Changed(nameof(TargetSearch)); Changed(nameof(SelectedTargetCategory)); Changed(nameof(SelectedTargetTag));
        paused = capture.IsPaused; Changed(nameof(IsPaused)); Changed(nameof(PauseLabel));
        rankMetric = RankMetric.Dps;
    }

    private async Task Replay()
    {
        OpenFileDialog dialog;
        try
        {
            Directory.CreateDirectory(ReplayDirectory);
            dialog = new OpenFileDialog { Filter = UiText.Get("ReplayFilter"), Title = UiText.Get("ReplayTitle"), InitialDirectory = ReplayDirectory };
            if (dialog.ShowDialog() != true) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status = UiText.Format("StatusReplayFolderUnavailable", ex.Message); return; }
        Mode = UiText.Get("ModeReplay");
        replayCancellation = new CancellationTokenSource();
        replaying = true; replayClock = null; stoppedClock = null;
        lock (engineGate) engine.Apply(new ConnectionEvent(DateTimeOffset.UtcNow, Guid.NewGuid().ToString(), true));
        ResetSessionUi(); Flows.Clear();
        SetRunning(true);
        Status = UiText.Get("StatusReplayReading");
        Health = UiText.Get("HealthNoLiveCapture");
        try
        {
            await Task.Run(() => capture.ReplayAsync(dialog.FileName, replayCancellation.Token));
            Status = UiText.Get("StatusReplayComplete");
        }
        catch (OperationCanceledException) { Status = UiText.Get("StatusReplayStopped"); }
        catch (Exception ex) { Status = UiText.Format("StatusReplayFailed", ex.Message); }
        finally
        {
            replaying = false;
            replayCancellation.Dispose(); replayCancellation = null;
            SetRunning(false);
            UpdateRows();
        }
    }

    private void OnCombatEvent(CombatEvent e)
    {
        lock (engineGate)
        {
            if (e is ConnectionEvent { Connected: true } connection && currentConnectionId != connection.ConnectionId)
            {
                currentConnectionId = connection.ConnectionId;
                Interlocked.Exchange(ref sessionDirty, 1);
            }
            engine.Apply(e);
            if (replaying) replayClock = e.Timestamp;
        }
    }

    private void Tick()
    {
        if (disposed) return;
        if (Interlocked.Exchange(ref sessionDirty, 0) != 0) ResetSessionUi();
        Warning = Mode == UiText.Get("ModeDemo") && !replaying ? "" : capture.DataMayBeIncomplete ? capture.HealthSummary : "";
        Progress = Mode == UiText.Get("ModeDemo") && !replaying ? UiText.Get("ProgressSyntheticDemo") : capture.ProgressSummary;
        Changed(nameof(Progress));
        Changed(nameof(Warning));
        if (Interlocked.Exchange(ref pendingDiagnostic, null) is { } message) Health = message;
        if (Interlocked.Exchange(ref flowsDirty, 0) != 0)
        {
            var previous = SelectedFlow?.Id;
            Flows.Clear();
            foreach (var flow in capture.ObservedFlows) Flows.Add(new(flow.Id, $"{(flow.Selected ? UiText.Get("FlowSelected") : "")}{flow.Client} ↔ {flow.Server}{UiText.Get(flow.VerifiedProtocol ? "FlowFramed" : "FlowAwaitingFrames")}"));
            SelectedFlow = Flows.FirstOrDefault(f => f.Id == previous) ?? Flows.FirstOrDefault();
        }
        if (running && !replaying && Mode == UiText.Get("ModeDemo") && DateTimeOffset.UtcNow >= demoNext)
        {
            var now = DateTimeOffset.UtcNow;
            lock (engineGate)
            {
                if (demoIndex == 0)
                {
                    engine.Apply(new EntityMetadataEvent(now, 1, "Flashmack", PlayerClass.Gladiator, EntityKind.Player, true));
                    engine.Apply(new EntityMetadataEvent(now, 2, "Windrunner", PlayerClass.Ranger, EntityKind.Player));
                    engine.Apply(new EntityMetadataEvent(now, 4, "Dawn", PlayerClass.Cleric, EntityKind.Player));
                    engine.Apply(new EntityMetadataEvent(now, 5, "Echo", PlayerClass.Chanter, EntityKind.Player));
                }
                engine.Apply(new DamageEvent(now, (ulong)(demoIndex % 6 + 1), 900, 850 + (demoIndex * 317 % 1800)));
                if (demoIndex % 3 == 0) engine.Apply(new HealEvent(now, 4, 1, 500 + demoIndex * 25));
                if (demoIndex == 8) engine.Apply(new EntityMetadataEvent(now, 3, "Nightfall", PlayerClass.Spiritmaster, EntityKind.Player));
                if (demoIndex == 10) engine.Apply(new OwnershipEvent(now, 6, 3));
            }
            demoIndex++;
            demoNext = now.AddMilliseconds(650);
        }
        UpdateRows();
    }

    private void UpdateRows()
    {
        IReadOnlyList<PlayerStatistics> snapshot;
        IReadOnlyList<TargetStatistics> targetSnapshot;
        double damageDuration, healingDuration;
        lock (engineGate)
        {
            var now = replayClock ?? stoppedClock ?? DateTimeOffset.UtcNow;
            snapshot = engine.Snapshot(now, selectedTarget?.Id, pinnedIds);
            targetSnapshot = engine.Targets(now);
            damageDuration = selectedTarget?.Id is ulong targetId ? engine.Duration(now, targetId) : engine.Duration(now);
            healingDuration = engine.HealingDuration(now);
        }
        RefreshTargets(targetSnapshot);
        var rankedPlayers = PlayerRanking.Rank(snapshot.Where(p => showHps || p.Damage > 0 || pinnedIds.Contains(p.EntityId)),
            pinnedIds, rankMetric);
        var orderedRows = new List<PlayerRow>(rankedPlayers.Count);
        foreach (var ranked in rankedPlayers)
        {
            var player = ranked.Player;
            if (!rowsById.TryGetValue(player.EntityId, out var row))
            {
                row = new PlayerRow(player.EntityId, OnPinChanged);
                row.SetPinnedSilently(pinnedIds.Contains(player.EntityId));
                rowsById[player.EntityId] = row;
            }
            var rowRank = ranked.Rank?.ToString(CultureInfo.CurrentCulture) ?? "";
            var name = aliases.TryGetValue(player.EntityId, out var alias) ? alias + UiText.Get("ManualAlias") : player.Name;
            row.Update(rowRank, name + (player.IsLocalPlayer ? UiText.Get("PlayerYou") : ""), player.Class.ToString(),
                player.Damage, player.Dps, player.Healing, player.Hps, ColorFor(player.Class));
            orderedRows.Add(row);
        }
        var visibleIds = orderedRows.Select(r => r.EntityId).ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--) if (!visibleIds.Contains(Rows[i].EntityId)) Rows.RemoveAt(i);
        for (int i = 0; i < orderedRows.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], orderedRows[i])) continue;
            int current = Rows.IndexOf(orderedRows[i]);
            if (current >= 0) Rows.Move(current, i);
            else Rows.Insert(i, orderedRows[i]);
        }
        DurationLabel = showHps
            ? UiText.Format("ProgressDamageHealingTime", damageDuration, healingDuration)
            : UiText.Format("ProgressActiveTime", damageDuration);
        Changed(nameof(DurationLabel));
    }

    private void RefreshTargets(IReadOnlyList<TargetStatistics> observed)
    {
        latestTargetStats = observed;
        var observedIds = observed.Select(t => t.EntityId).ToHashSet();
        var retained = new HashSet<ulong>(observedIds);
        if (selectedTarget?.Id is ulong selectedId) retained.Add(selectedId);
        retained.UnionWith(targetAliases.Keys);
        retained.UnionWith(targetTags.Keys);
        foreach (var id in targetChoicesById.Keys.Where(id => !retained.Contains(id)).ToArray()) targetChoicesById.Remove(id);
        foreach (var target in observed)
        {
            if (!targetChoicesById.TryGetValue(target.EntityId, out var choice))
                targetChoicesById[target.EntityId] = choice = new TargetChoice(target.EntityId, "");
            choice.Kind = target.Kind;
            choice.Damage = target.Damage;
            choice.SearchName = target.Name ?? "";
        }
        foreach (var choice in targetChoicesById.Values)
        {
            if (!observedIds.Contains(choice.Id!.Value)) choice.Damage = 0;
            var id = choice.Id!.Value;
            var name = targetAliases.TryGetValue(id, out var alias) ? alias + UiText.Get("ManualAlias")
                : string.IsNullOrWhiteSpace(choice.SearchName) ? UiText.Get("TargetUnnamed") : choice.SearchName;
            var category = UiText.Get(choice.Kind switch
            {
                EntityKind.Player => "TargetCategoryPlayer",
                EntityKind.Npc => "TargetCategoryNpc",
                EntityKind.Pet => "TargetCategoryPet",
                _ => "TargetCategoryUnknownSingle"
            });
            var tag = targetTags.TryGetValue(id, out var manualTag) ? UiText.Get(manualTag == ManualTargetTag.Boss ? "TargetTagBossMarked" : "TargetTagDummyMarked") : "";
            choice.Label = UiText.Format("TargetDisplay", name, id, category, choice.Damage) + tag;
        }
        RefreshTargetChoices();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshTargetChoices()
    {
        updatingTargets = true;
        try
        {
            var desired = new List<TargetChoice> { Targets.FirstOrDefault(t => t.Id is null) ?? new TargetChoice(null, UiText.Get("AllTargets")) };
            desired.AddRange(targetChoicesById.Values
                .Where(t => ReferenceEquals(t, selectedTarget) || TargetSelection.Matches(t.Kind, t.Label,
                    t.Id!.Value, selectedTargetCategory?.Kind, targetSearch))
                .OrderByDescending(t => t.Damage).ThenBy(t => t.Id));
            var ids = desired.Select(t => t.Id).ToHashSet();
            for (int i = Targets.Count - 1; i >= 0; i--) if (!ids.Contains(Targets[i].Id)) Targets.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < Targets.Count && ReferenceEquals(Targets[i], desired[i])) continue;
                int current = Targets.IndexOf(desired[i]);
                if (current >= 0) Targets.Move(current, i);
                else Targets.Insert(i, desired[i]);
            }
            if (selectedTarget is null || !Targets.Contains(selectedTarget))
            { selectedTarget = Targets[0]; Changed(nameof(SelectedTarget)); }
            Changed(nameof(DamageScopeLabel));
        }
        finally { updatingTargets = false; }
    }

    private static Brush ColorFor(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Templar or PlayerClass.Gladiator => Brushes.DeepSkyBlue,
        PlayerClass.Assassin or PlayerClass.Ranger => Brushes.LightGreen,
        PlayerClass.Sorcerer or PlayerClass.Spiritmaster => Brushes.Violet,
        PlayerClass.Cleric => Brushes.Khaki,
        PlayerClass.Chanter => Brushes.WhiteSmoke,
        _ => Brushes.Silver
    };
    private void SetRunning(bool value) { running = value; Changed(nameof(CanConfigure)); CommandManager.InvalidateRequerySuggested(); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { disposed = true; timer.Stop(); replayCancellation?.Cancel(); capture.Stop(); }
}

internal sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}







