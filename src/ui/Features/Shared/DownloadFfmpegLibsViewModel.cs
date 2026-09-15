using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Download;
using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace Nikse.SubtitleEdit.Features.Shared;

/// <summary>
/// Downloads the FFmpeg shared libraries for the ffmpeg video player into
/// <see cref="Se.FfmpegLibFolder"/>. Same shape as <see cref="DownloadLibVlcViewModel"/>.
/// </summary>
public partial class DownloadFfmpegLibsViewModel : ObservableObject, IClosingCleanup
{
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText;
    [ObservableProperty] private double _progressOpacity;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _error;

    public Window? Window { get; set; }
    public bool OkPressed { get; internal set; }

    private string _tempFileName;
    private readonly IFfmpegLibsDownloadService _downloadService;
    private Task? _downloadTask;
    private readonly Timer _timer;
    private volatile bool _done;
    private int _cleanupStarted;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private IndeterminateProgressHelper? _indeterminateProgressHelper;
    private readonly Lock _lockObj = new();

    public DownloadFfmpegLibsViewModel(IFfmpegLibsDownloadService downloadService)
    {
        _downloadService = downloadService;
        _cancellationTokenSource = new CancellationTokenSource();

        StatusText = string.Format(Se.Language.General.DownloadingX, "FFmpeg libraries");
        ProgressText = string.Empty;
        ProgressOpacity = 1.0;
        Error = string.Empty;
        _tempFileName = string.Empty;

        _timer = new Timer(500);
        _timer.Elapsed += OnTimerOnElapsed;
        _timer.Start();
    }

    private void OnTimerOnElapsed(object? sender, ElapsedEventArgs args)
    {
        lock (_lockObj)
        {
            if (_done)
            {
                return;
            }

            if (_downloadTask is { IsCompletedSuccessfully: true })
            {
                _timer.Stop();
                _done = true;

                if (!File.Exists(_tempFileName) || new FileInfo(_tempFileName).Length == 0)
                {
                    ProgressText = Se.Language.General.DownloadFailed;
                    Error = Se.Language.General.NoDataReceived;
                    return;
                }

                StartIndeterminateProgress();
                try
                {
                    ExtractLibraries(_tempFileName, Se.FfmpegLibFolder, _cancellationTokenSource.Token);
                }
                catch (Exception exception)
                {
                    Se.LogError(exception, "FFmpeg libraries unpack failed");
                    StopIndeterminateProgress();
                    ProgressText = Se.Language.General.UnpackingFailed;
                    Error = exception.Message;
                    return;
                }
                finally
                {
                    TryDeleteTempFile(_tempFileName);
                }

                StopIndeterminateProgress();
                FfmpegLibraries.Reset();
                OkPressed = true;
                Close();
            }
            else if (_downloadTask is { IsFaulted: true })
            {
                _timer.Stop();
                _done = true;
                var ex = _downloadTask.Exception?.InnerException ?? _downloadTask.Exception;
                if (ex is OperationCanceledException)
                {
                    ProgressText = Se.Language.General.DownloadCanceled;
                    Close();
                }
                else
                {
                    ProgressText = Se.Language.General.DownloadFailed;
                    Error = ex?.Message ?? Se.Language.General.UnknownError;
                }
            }
        }
    }

    /// <summary>
    /// Stages, validates and transactionally installs the DLLs from the reviewed FFmpeg build zip.
    /// The active library folder is changed only after all required runtime libraries are present.
    /// </summary>
    internal static void ExtractLibraries(string zipFileName, string targetFolder, CancellationToken cancellationToken)
    {
        FfmpegLibraryInstaller.Install(zipFileName, targetFolder, cancellationToken);
    }

    private void StartIndeterminateProgress()
    {
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _indeterminateProgressHelper?.Dispose();
        _indeterminateProgressHelper = new IndeterminateProgressHelper(
            value => ProgressValue = value,
            opacity => ProgressOpacity = opacity,
            () => _cancellationTokenSource.IsCancellationRequested);
        _indeterminateProgressHelper.Start();
    }

    private void StopIndeterminateProgress()
    {
        _indeterminateProgressHelper?.Stop();
    }

    private void Close()
    {
        Dispatcher.UIThread.Post(() => { Window?.Close(); });
    }

    [RelayCommand]
    private void CommandCancel()
    {
        CancelPendingWork();
        Close();
    }

    private void CancelPendingWork()
    {
        _done = true;
        _cancellationTokenSource.Cancel();
        StopIndeterminateProgress();
    }

    public void OnClosingCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return;
        }

        // Closed can come from the Cancel button, Escape, the title-bar X, or normal success.
        // Make the first three equivalent: stop all work before detaching the polling timer.
        CancelPendingWork();
        _timer.StopAndDispose(OnTimerOnElapsed);
        _ = DeleteTempFileWhenTaskCompletesAsync(_downloadTask, _tempFileName);
    }

    internal static async Task DeleteTempFileWhenTaskCompletesAsync(Task? downloadTask, string tempFileName)
    {
        if (downloadTask != null)
        {
            try
            {
                await downloadTask.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation/download failure is already the reason cleanup is running.
            }
        }

        TryDeleteTempFile(tempFileName);
    }

    private static void TryDeleteTempFile(string tempFileName)
    {
        if (string.IsNullOrWhiteSpace(tempFileName))
        {
            return;
        }

        try
        {
            if (File.Exists(tempFileName))
            {
                File.Delete(tempFileName);
            }
        }
        catch
        {
            // Best effort. During unpacking the timer callback may still own the ZIP and its
            // finally block will retry deletion once that operation observes cancellation.
        }
    }

    public void StartDownload()
    {
        var downloadProgress = new Progress<float>(number =>
        {
            var percentage = (int)Math.Round(number * 100.0, MidpointRounding.AwayFromZero);
            ProgressValue = percentage;
            ProgressText = string.Format(Se.Language.General.DownloadingXPercent, percentage.ToString(CultureInfo.InvariantCulture));
        });

        var folder = Se.DataFolder;
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _tempFileName = Path.Combine(folder, $"{Guid.NewGuid()}.zip");
        _downloadTask = _downloadService.DownloadFfmpegLibs(_tempFileName, downloadProgress, _cancellationTokenSource.Token);
    }

    internal void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CommandCancel();
        }
    }
}
