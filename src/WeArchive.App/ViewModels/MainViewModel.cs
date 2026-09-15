using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using WeArchive.App.Services;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Settings;

namespace WeArchive.App.ViewModels;

public enum ConversationFilter
{
    All,
    Direct,
    Group,
}

/// <summary>
/// The single screen of the MVP: environment status, conversation selection, one
/// conversation export with live progress, cancellation and a result summary.
/// <para>
/// The view model contains no source-format knowledge and never touches WeChat data
/// directly; it drives the application services and renders their progress and diagnostics.
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SourceCatalogService _catalog;
    private readonly ArchiveWorkflow _workflow;
    private readonly SettingsStore _settings;
    private readonly UpdateService _updates;
    private readonly IFolderPicker _folderPicker;

    private readonly List<ConversationItemViewModel> _allConversations = [];
    private SourceDescriptor? _descriptor;

    // The conversation-list load is fire-and-forget from the SelectedAccount setter. It
    // observes its own exceptions (see LoadConversationsAsync) and stores the task here so
    // tests can await it deterministically; it never throws to a discarded-task handler.
    private Task? _conversationLoadTask;

    // A monotonically increasing generation stamped onto each conversation-list load when an
    // account is selected. A load that completes after a newer selection must discard its
    // result: otherwise a slow load for account A could publish A's conversations and
    // persist A's profile id after the user selected B, so the UI would show A's list while
    // export used B's SourceProfileId — reading/exporting the wrong conversation. Mirrors
    // the ReferenceEquals staleness guard in LoadConversationDetailAsync.
    private long _accountSelectionGeneration;

    // Cancelled and replaced on each selection so a superseded load stops doing source work
    // (the adapter cooperates via ThrowIfCancellationRequested) instead of running to
    // completion only to be discarded. The generation check is the authoritative guard. The
    // source is intentionally not disposed: a load keeps observing its token after the setter
    // returns and not every token consumer can be tracked, so cleanup is left to the GC.
    private CancellationTokenSource? _conversationLoadCts;

    private string _conversationStatus = string.Empty;

    private string _sourceStatus = "正在检测…";
    private string _dataSourceStatus = string.Empty;
    private string _archiveStatus = string.Empty;
    private SourceAccount? _selectedAccount;
    private string _searchText = string.Empty;
    private ConversationFilter _filter = ConversationFilter.All;
    private ConversationItemViewModel? _selectedConversation;
    private string _conversationDetail = "尚未选择会话。";
    private string _exportDirectory = string.Empty;
    private string _resultSummary = string.Empty;
    private string _resultPath = string.Empty;
    private string _partialWarning = string.Empty;
    private bool _hasPartialWarning;
    private bool _isBusy;
    private string _progressStage = string.Empty;
    private string _progressCounters = string.Empty;
    private double _progressValue;
    private double _progressMaximum = 1;
    private bool _isProgressIndeterminate = true;
    private string _updateStatus = string.Empty;
    private string _errorMessage = string.Empty;

    public MainViewModel(
        SourceCatalogService catalog,
        ArchiveWorkflow workflow,
        SettingsStore settings,
        UpdateService updates,
        IFolderPicker folderPicker)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));

        var stored = _settings.Load();
        _exportDirectory = stored.ExportDirectory ?? DefaultExportDirectory();

        RefreshCommand = new AsyncRelayCommand(
            _ => LoadAsync(CancellationToken.None),
            onError: ReportError);
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanExport, ReportError);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, onError: ex => UpdateStatus = ex.Message);
        CancelCommand = new RelayCommand(() => ExportCommand.Cancel(), () => IsBusy);
        BrowseCommand = new RelayCommand(BrowseForExportDirectory, () => !IsBusy);
        OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => ResultPath.Length > 0);

        ExportCommand.RaiseCanExecuteChanged();
    }

    public string ApplicationVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public ObservableCollection<SourceAccount> Accounts { get; } = [];

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public AsyncRelayCommand CheckUpdatesCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand OpenResultFolderCommand { get; }

    public string SourceStatus
    {
        get => _sourceStatus;
        private set => SetProperty(ref _sourceStatus, value);
    }

    public string DataSourceStatus
    {
        get => _dataSourceStatus;
        private set => SetProperty(ref _dataSourceStatus, value);
    }

    public string ArchiveStatus
    {
        get => _archiveStatus;
        private set => SetProperty(ref _archiveStatus, value);
    }

    public SourceAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                OnPropertyChanged(nameof(AccountLabel));
                _conversationLoadTask = StartConversationLoad();
            }
        }
    }

    public string AccountLabel => SelectedAccount?.SourceProfileId ?? "（未发现账号）";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool FilterAll
    {
        get => _filter == ConversationFilter.All;
        set => SetFilter(ConversationFilter.All, value);
    }

    public bool FilterDirect
    {
        get => _filter == ConversationFilter.Direct;
        set => SetFilter(ConversationFilter.Direct, value);
    }

    public bool FilterGroup
    {
        get => _filter == ConversationFilter.Group;
        set => SetFilter(ConversationFilter.Group, value);
    }

    public ConversationItemViewModel? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (SetProperty(ref _selectedConversation, value))
            {
                ExportCommand.RaiseCanExecuteChanged();
                _ = LoadConversationDetailAsync();
            }
        }
    }

    public string ConversationDetail
    {
        get => _conversationDetail;
        private set => SetProperty(ref _conversationDetail, value);
    }

    /// <summary>
    /// Status line for the conversation list. Populated only when listing conversations fails
    /// for a recoverable reason (for example the WeChat client is not running, so the local
    /// archive key cannot be recovered from its memory); empty on success so the list is the
    /// only thing shown. A recoverable list failure is surfaced here rather than being allowed
    /// to escape the fire-and-forget load as an unobserved task exception that writes a crash
    /// log. docs/ARCHITECTURE.md sections 3.1 and 11.
    /// </summary>
    public string ConversationStatus
    {
        get => _conversationStatus;
        private set
        {
            if (SetProperty(ref _conversationStatus, value))
            {
                OnPropertyChanged(nameof(HasConversationStatus));
            }
        }
    }

    /// <summary>True when a conversation-list status message should be shown.</summary>
    public bool HasConversationStatus => _conversationStatus.Length > 0;

    /// <summary>
    /// The most recently started conversation-list load. Completed (not faulted) once the
    /// list or the failure status has settled, because <see cref="LoadConversationsAsync"/>
    /// observes its own exceptions. Exposed for deterministic tests.
    /// </summary>
    internal Task? ConversationLoadTask => _conversationLoadTask;

    public string ExportDirectory
    {
        get => _exportDirectory;
        set => SetProperty(ref _exportDirectory, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public string ResultPath
    {
        get => _resultPath;
        private set
        {
            if (SetProperty(ref _resultPath, value))
            {
                OnPropertyChanged(nameof(HasResultPath));
                OpenResultFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when an exported conversation folder can be opened.</summary>
    public bool HasResultPath => _resultPath.Length > 0;

    public bool HasPartialWarning
    {
        get => _hasPartialWarning;
        private set => SetProperty(ref _hasPartialWarning, value);
    }

    public string PartialWarning
    {
        get => _partialWarning;
        private set => SetProperty(ref _partialWarning, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
                BrowseCommand.RaiseCanExecuteChanged();
                ExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProgressStage
    {
        get => _progressStage;
        private set => SetProperty(ref _progressStage, value);
    }

    public string ProgressCounters
    {
        get => _progressCounters;
        private set => SetProperty(ref _progressCounters, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetProperty(ref _progressMaximum, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public int ConversationCount => Conversations.Count;

    /// <summary>Probes the source, refreshes the account list and the archive counters.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = string.Empty;
        SourceStatus = "正在检测…";

        var descriptor = await _catalog.DescribeSourceAsync(cancellationToken).ConfigureAwait(true);
        _descriptor = descriptor;

        SourceStatus = descriptor.IsAvailable
            ? $"{descriptor.SourceProductName} {descriptor.SourceVersion ?? "(版本未知)"}"
            : "不可用";

        DataSourceStatus = descriptor.IsAvailable
            ? descriptor.Diagnostics.Count == 0
                ? "数据源可用（只读）"
                : string.Join(Environment.NewLine, descriptor.Diagnostics.Select(d => d.Message))
            : descriptor.UnavailableReason ?? "未发现本地微信数据。";

        Accounts.Clear();
        if (descriptor.IsAvailable)
        {
            var accounts = await _catalog.ListAccountsAsync(cancellationToken).ConfigureAwait(true);
            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }
        }

        var stored = _settings.Load().SourceProfileId;
        SelectedAccount = Accounts.FirstOrDefault(a => a.SourceProfileId == stored) ?? Accounts.FirstOrDefault();

        var stats = await Task.Run(() => _workflow.GetArchiveStatsAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        ArchiveStatus = string.Create(
            CultureInfo.InvariantCulture,
            $"档案：{stats.ConversationCount} 个会话 / {stats.MessageCount} 条消息\n{stats.ArchivePath}");
    }

    private Task StartConversationLoad()
    {
        // Cancel any in-flight load for a previously selected account: it is now stale and its
        // result must never be published or persisted. A fresh generation is stamped onto the
        // new load so that even if the cancelled (or merely slow) previous load completes
        // last, it is discarded. See LoadConversationsAsync for the guard.
        if (_conversationLoadCts is { } previous)
        {
            previous.Cancel();
        }

        var cts = new CancellationTokenSource();
        _conversationLoadCts = cts;
        var generation = ++_accountSelectionGeneration;
        return LoadConversationsAsync(generation, cts.Token);
    }

    private async Task LoadConversationsAsync(long generation, CancellationToken cancellationToken)
    {
        var account = SelectedAccount;

        _allConversations.Clear();
        Conversations.Clear();
        ConversationStatus = string.Empty;

        if (account is null)
        {
            ApplyFilter();
            return;
        }

        try
        {
            var conversations = await Task.Run(
                () => _catalog.ListConversationsAsync(account.SourceProfileId, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            // A slow load that completed after a newer account was selected must not publish or
            // persist the stale account's conversations. Otherwise the UI would show the previous
            // account's list while SelectedAccount had moved on, and exporting a displayed
            // conversation would pair the new account's SourceProfileId with the old account's
            // SourceConversationId. Mirrors the ReferenceEquals guard in LoadConversationDetailAsync.
            if (generation != _accountSelectionGeneration)
            {
                return;
            }

            foreach (var conversation in conversations)
            {
                _allConversations.Add(new ConversationItemViewModel(conversation));
            }

            _settings.Save(_settings.Load() with { SourceProfileId = account.SourceProfileId });
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // A superseded list load is cancelled by the next account selection; this is expected
            // and must never surface a status or escape this fire-and-forget task as an unobserved
            // exception that writes a crash log. docs/ARCHITECTURE.md §11.
        }
        catch (Exception ex)
        {
            // Only surface a list failure if this load is still current; a stale failure must not
            // overwrite a newer selection's (possibly successful) status.
            if (generation != _accountSelectionGeneration)
            {
                return;
            }

            // Listing conversations can fail for an expected, recoverable reason: most often
            // the WeChat client is not running, so the local archive key cannot be recovered
            // from its memory (a normal missing precondition already reported by source
            // discovery as `source_not_running`). Such a failure is surfaced here as a
            // conversation-list status and must never escape this discarded task as an
            // unobserved exception that writes a crash log. docs/ARCHITECTURE.md §11.
            ConversationStatus = $"无法加载会话列表：{ex.Message}";
            ApplyFilter();
        }
    }

    private async Task LoadConversationDetailAsync()
    {
        var selection = SelectedConversation;
        if (selection is null || SelectedAccount is null)
        {
            ConversationDetail = "尚未选择会话。";
            return;
        }

        ConversationDetail = "正在读取会话信息……";

        try
        {
            var account = SelectedAccount;
            var detail = await Task.Run(
                () => _catalog.DescribeConversationAsync(account.SourceProfileId, selection.SourceConversationId, CancellationToken.None),
                CancellationToken.None).ConfigureAwait(true);

            // A slow preview must not overwrite a newer selection.
            if (!ReferenceEquals(SelectedConversation, selection))
            {
                return;
            }

            ConversationDetail = string.Create(
                CultureInfo.InvariantCulture,
                $"消息数量：{detail.MessageCount}\n" +
                $"可用时间范围：{Format(detail.FirstMessageAt)} ~ {Format(detail.LastMessageAt)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConversationDetail = $"无法读取会话信息：{ex.Message}";
        }
    }

    private bool CanExport() =>
        !IsBusy && SelectedConversation is not null && !string.IsNullOrWhiteSpace(ExportDirectory);

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        var selection = SelectedConversation;
        var account = SelectedAccount;
        if (selection is null || account is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        ResultSummary = string.Empty;
        ResultPath = string.Empty;
        HasPartialWarning = false;
        PartialWarning = string.Empty;
        Diagnostics.Clear();
        ProgressStage = "准备中……";
        ProgressCounters = string.Empty;
        ProgressValue = 0;
        ProgressMaximum = 1;
        IsProgressIndeterminate = true;

        _settings.Save(_settings.Load() with { ExportDirectory = ExportDirectory });

        var progress = new Progress<OperationProgress>(report =>
        {
            ProgressStage = ProgressText.Describe(report.Stage);
            ProgressCounters = ProgressText.Counters(report.Processed, report.Total);
            ProgressMaximum = Math.Max(1, report.Total);
            ProgressValue = Math.Min(report.Processed, ProgressMaximum);
            IsProgressIndeterminate = report.Total <= 0;
        });

        try
        {
            var request = new ExportConversationRequest
            {
                SourceProfileId = account.SourceProfileId,
                SourceConversationId = selection.SourceConversationId,
                Kind = selection.Kind,
                PeerSourceUserId = selection.Model.PeerSourceUserId,
                ConversationTitle = selection.Model.Title,
                OutputDirectory = ExportDirectory,
            };

            // Microsoft.Data.Sqlite is synchronous, so the whole operation is offloaded to a
            // background thread here rather than blocking the dispatcher. This is a deliberate
            // boundary, not a wrapper hiding an architectural problem: the layers themselves
            // are fully async and cancellable.
            var result = await Task.Run(
                () => _workflow.ExportConversationAsync(request, progress, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            PresentResult(result, selection);
        }
        catch (OperationCanceledException)
        {
            ProgressStage = "已取消";
            ResultSummary = "导出已取消。已写入的档案记录会保留，SQLite 档案按稳定 ID 幂等写入，重新导出不会产生重复消息。";
        }
        finally
        {
            IsBusy = false;
            await RefreshArchiveStatusAsync().ConfigureAwait(true);
        }
    }

    private void PresentResult(ExportResult result, ConversationItemViewModel selection)
    {
        ProgressStage = "导出完成";
        ProgressCounters = string.Empty;
        IsProgressIndeterminate = false;
        ProgressValue = ProgressMaximum;

        ResultSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"导出成功：{result.RecordCount} 条消息\n" +
            $"会话：{selection.Title}（{result.ConversationIds.FirstOrDefault() ?? selection.SourceConversationId}）\n" +
            $"未知类型：{result.UnknownCount} 条，部分解析：{result.PartialCount} 条\n" +
            $"时间范围：{Format(result.FirstMessageAt)} ~ {Format(result.LastMessageAt)}");

        ResultPath = result.ConversationPaths.FirstOrDefault() ?? result.OutputDirectory;

        var partials = result.Diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Info)
            .ToList();

        HasPartialWarning = partials.Count > 0;
        if (partials.Count > 0)
        {
            PartialWarning = $"导出完成，但存在 {result.PartialCount} 条部分解析记录。";
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            Diagnostics.Add(ProgressText.DescribeDiagnostic(diagnostic));
        }
    }

    private async Task RefreshArchiveStatusAsync()
    {
        var stats = await Task.Run(() => _workflow.GetArchiveStatsAsync(CancellationToken.None), CancellationToken.None)
            .ConfigureAwait(true);
        ArchiveStatus = string.Create(
            CultureInfo.InvariantCulture,
            $"档案：{stats.ConversationCount} 个会话 / {stats.MessageCount} 条消息\n{stats.ArchivePath}");
    }

    private async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        UpdateStatus = "正在检查更新……";
        UpdateStatus = await _updates.CheckAsync(cancellationToken).ConfigureAwait(true);
    }

    private void BrowseForExportDirectory()
    {
        var picked = _folderPicker.PickFolder(ExportDirectory, "选择导出目录");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            ExportDirectory = picked;
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    private void OpenResultFolder()
    {
        try
        {
            if (Directory.Exists(ResultPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ResultPath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void SetFilter(ConversationFilter filter, bool value)
    {
        if (!value)
        {
            return;
        }

        _filter = filter;
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterDirect));
        OnPropertyChanged(nameof(FilterGroup));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Conversations.Clear();

        foreach (var item in _allConversations)
        {
            if (_filter == ConversationFilter.Direct && item.Kind != ConversationKind.Direct)
            {
                continue;
            }

            if (_filter == ConversationFilter.Group && item.Kind != ConversationKind.Group)
            {
                continue;
            }

            if (search.Length > 0
                && !item.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !item.SourceConversationId.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Conversations.Add(item);
        }

        OnPropertyChanged(nameof(ConversationCount));
    }

    private void ReportError(Exception exception)
    {
        IsBusy = false;
        ProgressStage = "失败";
        ErrorMessage = exception is AggregateException aggregate
            ? string.Join(Environment.NewLine, aggregate.Flatten().InnerExceptions.Select(e => e.Message))
            : exception.Message;
    }

    private static string Format(DateTimeOffset? value) =>
        value is null ? "（未知）" : value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string DefaultExportDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WeArchiveData");
}
