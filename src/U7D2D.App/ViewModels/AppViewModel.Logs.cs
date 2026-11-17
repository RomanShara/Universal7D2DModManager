using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.I18n;
using U7D2D.Core.Logs;

namespace U7D2D.App.ViewModels;

/// <summary>
/// Game log analysis: one button, progress, cancel, and a report.
///
/// The heavy work runs on the thread pool, but the handler awaits it. The continuation therefore
/// resumes on the renderer's synchronization context, which is where the report has to be applied:
/// the observable collection and the SQLite operation log belong to the renderer thread and must
/// never be touched from a pool thread.
/// </summary>
public partial class AppViewModel
{
    private LogDiagnosisReport? _lastLogReport;
    private Loc? _summaryLoc;
    private CancellationTokenSource? _logCancellation;
    private int _truncatedShown;
    private int _truncatedTotal;

    [ObservableProperty] private bool _isAnalyzingLogs;
    [ObservableProperty] private string _logSummary = string.Empty;
    [ObservableProperty] private string _logProgressText = string.Empty;
    [ObservableProperty] private bool _hasLogReport;
    [ObservableProperty] private double _logProgress;
    [ObservableProperty] private LogComparison? _logComparison;

    public ObservableCollection<LogFinding> LogFindings { get; } = new();

    public IReadOnlyList<string> LogIndexWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>Advice for the whole report, rendered once. Loc resolves it in the current language.</summary>
    public IReadOnlyList<Loc> LogNotes { get; private set; } = Array.Empty<Loc>();

    /// <summary>Summary in the current language. Counts are kept, the sentence is built on demand.</summary>
    public string LogSummaryText => _summaryLoc?.Text ?? LogSummary;

    /// <summary>How many findings the report cap dropped. Empty when every finding is listed.</summary>
    public string LogTruncationText => _truncatedTotal > _truncatedShown
        ? F("log.reportTruncated", _truncatedShown, _truncatedTotal)
        : string.Empty;

    public bool CanCancelLogs => IsAnalyzingLogs;

    /// <summary>Runs the analysis and applies the report on the renderer's synchronization context.</summary>
    public async Task StartLogAnalysisAsync()
    {
        if (IsAnalyzingLogs) return;

        var gamePath = Settings.GamePath;
        var modsPath = Settings.ModsPath;

        IsAnalyzingLogs = true;
        LogProgress = 0;
        LogProgressText = T("logs.working");
        _truncatedShown = 0;
        _truncatedTotal = 0;
        OnPropertyChanged(nameof(LogTruncationText));
        OnPropertyChanged(nameof(CanCancelLogs));

        var cts = new CancellationTokenSource();
        _logCancellation = cts;

        // Progress is created here so its callbacks are posted to the renderer, not to the pool.
        // The throttle sits on the producing side: only a few ticks per second reach the UI queue,
        // so a large log cannot flood the renderer with thousands of callbacks.
        var uiProgress = new Progress<LogProgress>(p =>
        {
            LogProgressText = p.Stage;
            LogProgress = p.Fraction;
        });
        var progress = new ThrottledProgress<LogProgress>(uiProgress, 120, p => p.Stage);

        try
        {
            // No ConfigureAwait(false): the continuation must stay on the renderer's context,
            // because Apply mutates the observable collection and writes to SQLite.
            var report = await Task.Run(
                () => _manager.AnalyzeLogsCore(gamePath, modsPath, progress, cts.Token),
                cts.Token);

            Apply(report);
        }
        catch (OperationCanceledException)
        {
            _summaryLoc = null;
            LogSummary = T("logs.cancelled");
            OnPropertyChanged(nameof(LogSummaryText));
            _toasts.Info(T("logs.cancelledTitle"), T("logs.cancelled"));
        }
        catch (Exception e)
        {
            _summaryLoc = null;
            LogSummary = e.Message;
            HasLogReport = true;
            OnPropertyChanged(nameof(LogSummaryText));
            _toasts.Error(T("logs.failedTitle"), e.Message);
        }
        finally
        {
            _logCancellation = null;
            IsAnalyzingLogs = false;
            LogProgress = 0;
            LogProgressText = string.Empty;
            OnPropertyChanged(nameof(CanCancelLogs));
        }
    }

    private void Apply(LogDiagnosisReport report)
    {
        // The operation log lives in SQLite and is written here, on the renderer thread, so the
        // connection is never shared with a worker.
        _manager.RecordLogAnalysis(report, report.Sources.Count);
        _lastLogReport = report;

        LogNotes = report.Notes;
        LogIndexWarnings = report.IndexWarnings;
        OnPropertyChanged(nameof(LogNotes));
        OnPropertyChanged(nameof(LogIndexWarnings));

        LogFindings.Clear();
        foreach (var finding in report.Findings) LogFindings.Add(finding);

        _truncatedShown = report.Findings.Count;
        _truncatedTotal = report.TotalFindings;
        OnPropertyChanged(nameof(LogTruncationText));

        HasLogReport = true;
        LogComparison = report.Comparison;

        if (report.Error is not null)
        {
            _summaryLoc = null;
            LogSummary = report.Error;
            OnPropertyChanged(nameof(LogSummaryText));
            _toasts.Warning(T("logs.emptyTitle"), report.Error);
            return;
        }

        _summaryLoc = new Loc("logs.summary", report.Findings.Count, report.HighCount,
            report.MediumCount, report.NoticeCount);
        LogSummary = string.Empty;
        OnPropertyChanged(nameof(LogSummaryText));

        if (report.Findings.Count == 0)
            _toasts.Success(T("logs.cleanTitle"), T("logs.cleanText"));
        else
            _toasts.Info(T("logs.doneTitle"), LogSummaryText);
    }

    public void CancelLogAnalysis()
    {
        try
        {
            _logCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the click and the cancel.
        }
    }

    public async Task SaveLogReportAsync()
    {
        if (_lastLogReport is null)
        {
            _toasts.Info(T("logs.run"), T("logs.saveLater"));
            await StartLogAnalysisAsync();
            return;
        }

        try
        {
            var text = LogReportWriter.Build(_lastLogReport);
            var path = Path.Combine(
                _manager.Paths.LogDirectory,
                $"log-analysis-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            Directory.CreateDirectory(_manager.Paths.LogDirectory);
            await File.WriteAllTextAsync(path, text);

            OpenFolder(_manager.Paths.LogDirectory);
            _toasts.Success(T("logs.saved"), Path.GetFileName(path));
        }
        catch (Exception e)
        {
            _toasts.Error(T("logs.saveFailed"), e.Message);
        }
    }
}
