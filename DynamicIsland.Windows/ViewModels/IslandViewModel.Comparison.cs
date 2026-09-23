using DynamicIsland.Q.Core;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services.Q;

namespace DynamicIsland.Windows.ViewModels;

public sealed partial class IslandViewModel
{
    private readonly QComparisonController _qComparison;
    private string? _compareSelectorKey;
    private string? _compareOptionsPrimary;
    private IReadOnlyList<QProviderChoice> _compareProviderOptions = [];
    public bool QCompareEnabled
    {
        get => Settings.QCompareEnabled;
        set
        {
            if (value == Settings.QCompareEnabled || QCanStop) return;
            var active = IsQActive; var context = _qSnapshot.Context; var mode = QCurrentMode;
            _qComparison.Clear(); _qSession.Clear();
            Settings.QCompareEnabled = value;
            EnsureCompareProvider();
            if (active) _ = _qSession.BeginAsync(mode, Settings.QSelectedProvider, Settings.QSelectedModel, context);
            RaiseQProperties(); _ = PersistSettingsAsync();
        }
    }
    public bool QSingleMode => !QCompareEnabled;
    public bool QShowSingleTranscript => !QCompareEnabled || QNeedsConsent;
    public bool QShowComparison => QCompareEnabled && !QNeedsConsent;
    public bool QShowCompactComparison => IsQActive && QCompareEnabled;
    public System.Windows.Media.Brush IslandBaseSurfaceBrush => QShowCompactComparison
        ? System.Windows.Media.Brushes.Transparent : IslandSurfaceBrush;
    public bool QShowCompactSingle => ShowCompactQContent && !QCompareEnabled;
    public double QCompactIconSize => QShowCompactComparison ? 36 : CompactAlbumSize;
    public string QDisclosureText => QCompareEnabled
        ? "Q sends the same prompt and enabled screen context to both selected providers. Each provider uses its own API billing or account limits."
        : "Q sends the active window’s text and, when supported, image to your selected provider.";
    public string QSendLabel => QCompareEnabled ? "Send to both" : "Send";
    public string QStopLabel => QCompareEnabled ? "Stop both" : "Stop";
    public string QComposerPlaceholder => QCompareEnabled ? "Ask both a follow-up…" : "Ask a follow-up…";
    public IReadOnlyList<QProviderChoice> QCompareProviderOptions
    {
        get
        {
            if (_compareOptionsPrimary != QSelectedProvider)
            {
                _compareOptionsPrimary = QSelectedProvider;
                _compareProviderOptions = QProviderOptions.Where(p => p.Id != QSelectedProvider).ToArray();
            }
            return _compareProviderOptions;
        }
    }
    public QProviderChoice? QCompareProviderChoice
    {
        get => QCompareProviderOptions.FirstOrDefault(p => p.Id == QCompareProvider);
        set { if (value is not null) QCompareProvider = value.Id; }
    }
    public string QCompareProvider
    {
        get => Settings.QCompareProvider;
        set
        {
            if (!QCanChangeProvider || value == QSelectedProvider || value == Settings.QCompareProvider || _qProviders.Find(value) is not { } provider) return;
            Settings.QCompareProvider = provider.Info.Id;
            Settings.QCompareModel = Settings.QProviderPreferences.TryGetValue(value, out var saved) ? saved.Model : provider.Info.DefaultModel;
            Settings.QCompareReasoningEffort = "auto";
            ResetComparisonAnswers(); RaiseQProperties(); _ = PersistSettingsAsync();
        }
    }
    public string QCompareModel
    {
        get => Settings.QCompareModel;
        set
        {
            if (!QCanChangeProvider || string.IsNullOrWhiteSpace(value) || value == Settings.QCompareModel) return;
            Settings.QCompareModel = value; Settings.QCompareReasoningEffort = "auto";
            ResetComparisonAnswers(); RaiseQProperties(); _ = PersistSettingsAsync();
        }
    }
    public IReadOnlyList<string> QCompareModelOptions => QCompareProvider == "codex" && _codexModels.Count > 0
        ? _codexModels.Select(m => m.Id).ToArray() : QProviderPolicy.ModelSuggestions(QCompareProvider, QCompareModel);
    public IReadOnlyList<string> QCompareEffortOptions => QCompareProvider == "codex"
        ? CodexModelSelectionPolicy.EffortOptions(_codexModels.FirstOrDefault(m => m.Id == QCompareModel))
        : QProviderPolicy.EffortOptions(QCompareProvider, QCompareModel);
    public string QCompareEffort
    {
        get => Settings.QCompareReasoningEffort;
        set { if (QCanChangeProvider && !string.IsNullOrWhiteSpace(value) && QCompareEffortOptions.Contains(value)) { Settings.QCompareReasoningEffort = value; RaisePropertyChanged(); _ = PersistSettingsAsync(); } }
    }
    private void EnsureCompareProvider()
    {
        if (Settings.QCompareProvider == Settings.QSelectedProvider || _qProviders.Find(Settings.QCompareProvider) is null)
        {
            Settings.QCompareProvider = _qProviders.Providers.FirstOrDefault(p => p.Info.Id != Settings.QSelectedProvider)?.Info.Id ?? "gemini";
            Settings.QCompareModel = "";
        }
        if (string.IsNullOrWhiteSpace(Settings.QCompareModel)) Settings.QCompareModel = _qProviders.Find(Settings.QCompareProvider)?.Info.DefaultModel ?? "";
    }
    private void ResetComparisonAnswers()
    {
        if (!QCompareEnabled || _qComparison.Snapshot.State == QRunState.Idle) return;
        var context = _qSnapshot.Context; var mode = QCurrentMode;
        _qComparison.Clear();
        _ = _qSession.BeginAsync(mode, Settings.QSelectedProvider, Settings.QSelectedModel, context);
    }
    private QSessionSnapshot LeftAnswer => _qComparison.Snapshot.Left;
    private QSessionSnapshot RightAnswer => _qComparison.Snapshot.Right;
    public string QLeftAnswer => AnswerText(LeftAnswer);
    public string QRightAnswer => AnswerText(RightAnswer);
    public string QLeftStatus => AnswerStatus(LeftAnswer);
    public string QRightStatus => AnswerStatus(RightAnswer);
    public bool QLeftCanCopy => !string.IsNullOrWhiteSpace(LeftAnswer.Response);
    public bool QRightCanCopy => !string.IsNullOrWhiteSpace(RightAnswer.Response);
    public bool QCompareCanRetry => !QCanStop && !string.IsNullOrWhiteSpace(QPromptText);
    public string QLeftLabel => ShortProvider(QSelectedProvider) + ":";
    public string QRightLabel => ShortProvider(QCompareProvider) + ":";
    public string QLeftCompact => OneLine(QLeftAnswer);
    public string QRightCompact => OneLine(QRightAnswer);
    private static string ShortProvider(string id) => id switch { "gemini" => "Gemini", "openai" => "OAI", "anthropic" => "Claude", "codex" => "Codex", "xai" => "Grok", _ => id };
    private static string OneLine(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    private static string AnswerStatus(QSessionSnapshot s) => s.State switch
    { QRunState.Capturing => "Reading…", QRunState.Thinking => "Thinking…", QRunState.Streaming => "Responding…", QRunState.Complete => "Complete", QRunState.Error => "Failed", QRunState.Cancelled => "Stopped", _ => "Ready" };
    private static string AnswerText(QSessionSnapshot s) => !string.IsNullOrWhiteSpace(s.Response) ? CleanQResponseText(s.Response)
        : s.State == QRunState.Error ? s.Error ?? "Provider could not respond."
        : s.State is QRunState.Idle or QRunState.Ready ? "Waiting for your question…" : AnswerStatus(s);
    private void OnQComparisonChanged(QComparisonSnapshot snapshot) => OnUi(() =>
    {
        if (!QCompareEnabled || snapshot != _qComparison.Snapshot) return;
        _qSnapshot = snapshot.Left;
        RaiseQProperties();
    });
    private QComparisonTarget ComparisonTarget(bool right)
    {
        var provider = right ? QCompareProvider : QSelectedProvider;
        return new(provider, right ? QCompareModel : QSelectedModel, _qSecrets.Get(provider),
            right ? QCompareEffort : QReasoningEffort, provider == "ollama" ? Settings.QOllamaBaseUrl : null);
    }
    private async Task SubmitComparisonAsync(string prompt)
    {
        EnsureCompareProvider();
        await RefreshCodexModelsAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.QTimeoutSeconds));
        await _qComparison.SubmitAsync(prompt, QCurrentMode, ComparisonTarget(false), ComparisonTarget(true),
            token => _qScreen.CaptureAsync(_qTargetWindow, Settings.QCaptureMode == Models.QCaptureMode.ActiveMonitor
                ? DynamicIsland.Q.Core.QCaptureMode.ActiveMonitor : DynamicIsland.Q.Core.QCaptureMode.ActiveWindow, token),
            Settings.QIncludeScreenImage, timeout.Token, Settings.QMaxResponseTokens,
            QIsSay ? Settings.QSaySystemPrompt : Settings.QAskSystemPrompt);
    }
    public void CopyComparisonAnswer(bool right)
    {
        var answer = right ? RightAnswer.Response : LeftAnswer.Response;
        if (!string.IsNullOrWhiteSpace(answer)) System.Windows.Clipboard.SetText(answer);
    }
    public async Task RetryComparisonAsync(bool right)
    {
        if (!QCompareCanRetry) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.QTimeoutSeconds));
        await _qComparison.RetryAsync(right, ComparisonTarget(right), timeout.Token, Settings.QMaxResponseTokens,
            QIsSay ? Settings.QSaySystemPrompt : Settings.QAskSystemPrompt, Settings.QIncludeScreenImage);
    }
    private void RaiseComparisonProperties()
    {
        // Replacing ItemsSource on every streaming chunk clears WPF's selected value.
        var key = $"{QSelectedProvider}|{QCompareProvider}|{QCompareModel}|{QCompareEffort}|{_codexModels.Count}";
        if (_compareSelectorKey != key)
        {
            _compareSelectorKey = key;
            RaiseMany(nameof(QCompareProviderOptions), nameof(QCompareModelOptions), nameof(QCompareEffortOptions),
                nameof(QCompareProvider), nameof(QCompareProviderChoice), nameof(QCompareModel), nameof(QCompareEffort));
        }
        RaiseMany(nameof(QCompareEnabled), nameof(QSingleMode), nameof(QShowSingleTranscript), nameof(QShowComparison), nameof(QShowCompactComparison),
            nameof(QShowCompactSingle), nameof(IslandBaseSurfaceBrush), nameof(QCompactIconSize), nameof(QDisclosureText), nameof(QSendLabel), nameof(QStopLabel), nameof(QComposerPlaceholder), nameof(QLeftAnswer), nameof(QRightAnswer),
            nameof(QLeftStatus), nameof(QRightStatus), nameof(QLeftCanCopy), nameof(QRightCanCopy), nameof(QCompareCanRetry), nameof(QLeftLabel), nameof(QRightLabel),
            nameof(QLeftCompact), nameof(QRightCompact));
    }
}
