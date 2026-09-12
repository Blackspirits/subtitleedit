using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Download;
using Nikse.SubtitleEdit.Logic.SevenZipExtractor;
using Nikse.SubtitleEdit.UiLogic;
using Nikse.SubtitleEdit.UiLogic.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nikse.SubtitleEdit.Logic;
using Timer = System.Timers.Timer;

namespace Nikse.SubtitleEdit.Features.Ocr.Download;

public partial class DownloadPaddleOcrViewModel : ObservableObject, IClosingCleanup
{
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText;
    [ObservableProperty] private double _progressOpacity;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _error;

    public Window? Window { get; set; }
    public bool OkPressed { get; internal set; }

    private string _tempFolder;
    private Task? _downloadTask;
    private int _downloadTaskIndex;
    private List<string> _downloadTaskUrls;
    private Timer _timer = new Timer(500);
    private bool _done;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private PaddleOcrDownloadType _downloadType;
    private IndeterminateProgressHelper? _indeterminateProgressHelper;

    private static readonly IReadOnlyDictionary<string, string> DownloadHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PaddleOCR.PP-OCRv6.support.files.VideOCR.7z"] = "7f98a187a1d8d9b5291f3be7cd6a6b693b32ddffd75d39c05d333d8f0b3ee145",
            ["PaddleOCR-CPU-v3.7.0.7z"] = "a1b597f5620d1a86cec606b50908a12fc1b215adf6807be5538b7ca6bddc9d20",
            ["PaddleOCR-GPU-v3.7.0-CUDA-11.8.7z"] = "5bfe2009cab89ce7f6b70f43f8250460ce6ccc6ccf176b95e0c363079bc4da50",
            ["PaddleOCR-GPU-v3.7.0-CUDA-12.9.7z"] = "6a2c1f17f093403c8f2f4c4c7b81148b29abe710604aca8fac403af2be173cab",
            ["PaddleOCR-CPU-v3.7.0-Linux.7z"] = "1d2bd1db1d534dcd433c2d658f1c9ed13beb92fc7201a7049d376bd15e8fc39e",
            ["PaddleOCR-GPU-v3.7.0-CUDA-11.8-Linux.7z"] = "3850afef8ba8bf9f65911e855a866f9df0de06b0b8f0030dbd827162819d7158",
            ["PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.001"] = "e154edaa5f80913d2a3aba0c05110ebf09f5d100f9db1b11e2d2d2b61bff4212",
            ["PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.002"] = "900200376f77a85fc4fc2562b1832fc547092eaa6951772894666be87585bf89",
        };

    public DownloadPaddleOcrViewModel()
    {
        _cancellationTokenSource = new CancellationTokenSource();

        StatusText = Se.Language.General.StartingDotDotDot;
        ProgressText = string.Empty;
        Error = string.Empty;
        _tempFolder = string.Empty;
        _downloadType = PaddleOcrDownloadType.Models;
        _downloadTaskUrls = new List<string>();
        _downloadTaskIndex = 0;
    }

    private readonly Lock _lockObj = new();

    public void Initialize(PaddleOcrDownloadType paddleOcrDownloadType)
    {
        _downloadType = paddleOcrDownloadType;
        if (_downloadType is PaddleOcrDownloadType.EngineCpu or
            PaddleOcrDownloadType.EngineGpu11 or
            PaddleOcrDownloadType.EngineGpu12 or
            PaddleOcrDownloadType.EngineCpuLinux or
            PaddleOcrDownloadType.EngineGpu11Linux or
            PaddleOcrDownloadType.EngineGpu12Linux)
        {
            StatusText = Se.Language.Ocr.DownloadingPaddleOcrEngineDotDotDot;
        }
        else if (_downloadType == PaddleOcrDownloadType.Models)
        {
            StatusText = Se.Language.Ocr.DownloadingPaddleOcrModelsDotDotDot;
        }
    }

    private void OnTimerOnElapsed(object? sender, ElapsedEventArgs args)
    {
        lock (_lockObj)
        {
            if (_done)
            {
                return;
            }

            // IsCompletedSuccessfully, not the broader IsCompleted (also true for
            // Faulted/Canceled), so a failed download falls through to the IsFaulted
            // branch below instead of proceeding as if it had succeeded.
            if (_downloadTask is { IsCompletedSuccessfully: true })
            {
                _timer.Stop();

                if (_downloadTaskIndex < _downloadTaskUrls.Count - 1)
                {
                    _downloadTaskIndex++;
                    Dispatcher.UIThread.Post(() =>
                    {
                        ProgressText = $"Starting download {_downloadTaskIndex + 1} of {_downloadTaskUrls.Count}...";
                        var url = _downloadTaskUrls[_downloadTaskIndex];
                        var fileName = Path.Combine(_tempFolder, Path.GetFileName(url));
                        _downloadTask = DownloadAndVerifyAssetAsync(
                            HttpClientFactoryWithProxy.CreateHttpClientWithProxy(),
                            url,
                            fileName,
                            new Progress<float>(number =>
                            {
                                var percentage = (int)Math.Round(number * 100.0, MidpointRounding.AwayFromZero);
                                var pctString = percentage.ToString(CultureInfo.InvariantCulture);
                                ProgressValue = percentage;
                                ProgressText = string.Format(Se.Language.General.DownloadingXPercent, pctString);
                            }),
                            _cancellationTokenSource.Token);

                        // The timer was stopped above and only restarted here, after _downloadTask
                        // points at the new download - without this the chained part is never
                        // observed and the dialog hangs at 100%.
                        _timer.Start();
                    });
                    return;
                }

                _done = true;

                if (!AllFileExists())
                {
                    ProgressText = Se.Language.General.DownloadFailed;
                    Error = Se.Language.General.NoDataReceived;
                    return;
                }

                StartIndeterminateProgress();

                try
                {
                    var firstFile = Path.Combine(_tempFolder, Path.GetFileName(_downloadTaskUrls[0]));
                    var isModels = _downloadType == PaddleOcrDownloadType.Models;
                    var archive = PaddleOcr.GetArchive(_downloadType);

                    StatusText = string.Format(Se.Language.General.UnpackingX,
                        isModels ? Se.Language.General.Models : Se.Language.Ocr.PaddleOcr);
                    Unpacker.Extract7Zip(
                        firstFile,
                        isModels ? Se.PaddleOcrModelsFolder : Se.PaddleOcrFolder,
                        archive.RootFolderInArchive,
                        _cancellationTokenSource,
                        text => ProgressText = text);

                    var binFile = Path.Combine(Se.PaddleOcrFolder, "paddleocr.bin");
                    if (!isModels && File.Exists(binFile))
                    {
                        LinuxHelper.MakeExecutable(binFile);
                    }

                    DeleteLegacyInstallFolders();
                }
                catch (Exception exception)
                {
                    // Timer callbacks swallow exceptions, so an unpack failure would
                    // otherwise hang the dialog with no error shown (#12127).
                    Se.LogError(exception, "PaddleOCR unpack failed");
                    StopIndeterminateProgress();
                    ProgressText = Se.Language.General.UnpackingFailed;
                    Error = exception.Message;
                    return;
                }
                finally
                {
                    DeleteDownloadTempFolder();
                }

                StopIndeterminateProgress();
                OkPressed = true;
                Close();
            }
            else if (_downloadTask is { IsFaulted: true })
            {
                _timer.Stop();
                _done = true;
                var ex = _downloadTask.Exception?.InnerException ?? _downloadTask.Exception;
                DeleteDownloadTempFolder();
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
    /// Removes the downloaded archive(s) once they have been unpacked (or failed to unpack).
    /// They live inside the install folder and are between 140 MB and 2.5 GB, so leaving them
    /// behind doubles what an install costs on disk.
    /// </summary>
    private void DeleteDownloadTempFolder()
    {
        try
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, true);
            }
        }
        catch (Exception exception)
        {
            Se.LogError(exception, $"Could not delete Paddle OCR download folder \"{_tempFolder}\"");
        }
    }

    /// <summary>
    /// Removes the install folders of superseded PaddleOCR versions once a newer engine or
    /// models bundle has been unpacked. Nothing reads them any more (the folder name carries
    /// the version), and a full CPU install is ~400 MB, a GPU one several GB.
    /// </summary>
    private static void DeleteLegacyInstallFolders()
    {
        foreach (var legacyFolder in Se.PaddleOcrLegacyFolders)
        {
            try
            {
                if (Directory.Exists(legacyFolder) &&
                    !legacyFolder.Equals(Se.PaddleOcrFolder, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(legacyFolder, true);
                }
            }
            catch (Exception exception)
            {
                // Never fail an otherwise successful install over leftovers we could not remove.
                Se.LogError(exception, $"Could not delete old Paddle OCR folder \"{legacyFolder}\"");
            }
        }
    }

    private bool AllFileExists()
    {
        foreach (var url in _downloadTaskUrls)
        {
            var fileName = Path.Combine(_tempFolder, Path.GetFileName(url));
            if (!File.Exists(fileName))
            {
                Se.LogError($"Expected file not found after download: {fileName}");
                return false;
            }

            var fileInfo = new FileInfo(fileName);
            if (fileInfo.Length == 0)
            {
                Se.LogError($"Downloaded file is empty: {fileName}");
                return false;
            }
        }

        return true;
    }

    internal static string GetExpectedHashForUrl(string url)
    {
        var fileName = Path.GetFileName(url);
        if (string.IsNullOrEmpty(fileName) || !DownloadHashes.TryGetValue(fileName, out var expected))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for Paddle OCR asset '{fileName}'.");
        }

        return expected;
    }

    internal static async Task DownloadAndVerifyAssetAsync(
        HttpClient httpClient,
        string url,
        string destinationFileName,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        var expected = GetExpectedHashForUrl(url);
        await DownloadHelper.DownloadFileAsync(httpClient, url, destinationFileName, progress, cancellationToken);

        string actual;
        await using (var stream = File.OpenRead(destinationFileName))
        {
            actual = await Sha256Util.ComputeSha256Async(stream, cancellationToken);
        }

        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(destinationFileName);
        }
        catch
        {
            // best effort; integrity failure is still surfaced below
        }

        throw new IOException(
            $"Paddle OCR download failed integrity check for {Path.GetFileName(url)} " +
            $"(expected SHA-256 {expected}, got {actual}).");
    }

    private void StartIndeterminateProgress()
    {
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
        _cancellationTokenSource?.Cancel();
        _done = true;
        Close();
    }

    public void OnClosingCleanup()
    {
        _timer.StopAndDispose(OnTimerOnElapsed);
    }

    public void StartDownload()
    {
        var downloadProgress = new Progress<float>(number =>
        {
            var percentage = (int)Math.Round(number * 100.0, MidpointRounding.AwayFromZero);
            var pctString = percentage.ToString(CultureInfo.InvariantCulture);
            ProgressValue = percentage;
            ProgressText = string.Format(Se.Language.General.DownloadingXPercent, pctString);
        });

        var folder = Se.PaddleOcrFolder;
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _tempFolder = Path.Combine(folder, $"{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempFolder);
        _downloadTaskIndex = 0;
        _downloadTaskUrls = new List<string>();

        List<string> urls;
        try
        {
            urls = PaddleOcr.GetArchive(_downloadType).Urls.ToList();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Se.LogError(exception, $"Unknown Paddle OCR download type: {_downloadType}");
            ProgressText = Se.Language.General.DownloadFailed;
            Error = "Unknown download type";
            return;
        }

        _downloadTaskUrls.AddRange(urls);
        var firstUrl = _downloadTaskUrls[_downloadTaskIndex];
        var firstFileName = Path.Combine(_tempFolder, Path.GetFileName(firstUrl));
        _downloadTask = DownloadAndVerifyAssetAsync(
            HttpClientFactoryWithProxy.CreateHttpClientWithProxy(),
            firstUrl,
            firstFileName,
            downloadProgress,
            _cancellationTokenSource.Token);

        _timer.Elapsed += OnTimerOnElapsed;
        _timer.Start();
    }

    internal void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CommandCancel();
        }
    }
}