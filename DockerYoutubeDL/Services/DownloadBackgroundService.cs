using DockerYoutubeDL.DAL;
using DockerYoutubeDL.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using DockerYoutubeDL.Models;
using System.Text.RegularExpressions;
using System.Globalization;
using Polly;

namespace DockerYoutubeDL.Services
{
    public class DownloadBackgroundService : BackgroundService
    {
        // Factory is used since the service is instantiated once as singleton and therefor 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IConfiguration _config;
        private DownloadPathGenerator _pathGenerator;
        private NotificationService _notification;

        // Percentages are 0-100.
        private double _percentageMinDifference = 10;

        // Shared info between handlers:
        private int _currentIndex;
        private AutoResetEvent _resetEvent;
        private string _currentDownloadVideoIdentifier;
        private double _prevPercentage;

        private Guid _currentDownloadTaskId = new Guid();
        private Process _mainDownloadProcess = null;
        private bool _wasKilledByInterrupt = false;

        public DownloadBackgroundService(
            IDesignTimeDbContextFactory<DownloadContext> factory,
            ILogger<DownloadBackgroundService> logger,
            IConfiguration config,
            DownloadPathGenerator pathGenerator,
            NotificationService notification)
        {
            if (factory == null || logger == null || config == null ||
                pathGenerator == null || notification == null)
            {
                throw new ArgumentException();
            }

            _factory = factory;
            _logger = logger;
            _config = config;
            _pathGenerator = pathGenerator;
            _notification = notification;
        }

        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogDebug("DownloadBackgroundService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var hasDownloaded = false;

                // Find the next download Target.
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    Func<DownloadTask, bool> selector = new Func<DownloadTask, bool>(x => 
                        x.HadInformationGathered && 
                        !x.WasDownloaded && 
                        !x.WasInterrupted && 
                        !x.HadDownloaderError);
                    var now = DateTime.Now;

                    _logger.LogDebug("Checking for pending download task...");

                    if (db.DownloadTask.Any(selector))
                    {
                        // Next task is the one that was queued earliest and was not yet downloaded. 
                        var nextTask = db.DownloadTask
                            .Where(selector)
                            .Select(x => new Tuple<DownloadTask, TimeSpan>(x, now.Subtract(x.DateAdded)))
                            .OrderByDescending(x => x.Item2)
                            .FirstOrDefault()
                            .Item1;

                        _logger.LogDebug($"Next download: Url={nextTask.Url}, Id={nextTask.Id}");

                        // Gather all necessary information.
                        _currentDownloadTaskId = nextTask.Id;
                        var mainProcessInfo = await this.GenerateMainProcessStartInfoAsync();
                        await this.MainDownloadProcessAsync(mainProcessInfo);

                        // Reset any set flag.
                        _wasKilledByInterrupt = false;

                        // Allow for downloads to directly follow one another.
                        hasDownloaded = true;
                    }
                }

                // Don't wait inbetween downloads if more are queued.
                if (!hasDownloaded)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.GetValue<int>("DownloadCheckIntervalSeconds")), stoppingToken);
                }
            }
        }

        private async Task<ProcessStartInfo> GenerateMainProcessStartInfoAsync()
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var downloadTask = await db.DownloadTask.FindAsync(_currentDownloadTaskId);

                _logger.LogDebug($"Generating main ProcessStartInformation for {downloadTask.Url}");

                var downloadFolder = _pathGenerator.GenerateDownloadFolderPath(downloadTask.Id).ToString();
                var ffmpegLocation = _config.GetValue<string>("FfmpegLocation");
                var youtubeDlLocation = _config.GetValue<string>("YoutubeDlLocation");
                var maxStoredFileNameLength = _config.GetValue<int>("MaxStoredFileNameLength");

                var processInfo = new ProcessStartInfo(youtubeDlLocation)
                {
                    UseShellExecute = false,
                    WorkingDirectory = baseDir,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var arguments = new List<string>()
                {
                    "--no-call-home",
                    "--ignore-errors",
                    "--ffmpeg-location",
                    $"{ffmpegLocation}",
                    "-o",
                    Path.Combine(downloadFolder, $"%(id)s{_pathGenerator.NameDilimiter}%(title)#0.{maxStoredFileNameLength}s.%(ext)s").ToString(),
                };

                // Depending on the chosen settings, different types of arguments must be
                // provided to the command line.
                if (downloadTask.AudioFormat != AudioFormat.None)
                {
                    arguments.AddRange(new List<string>
                    {
                        "-x",
                        "--audio-format",
                        Enum.GetName(typeof(AudioFormat), downloadTask.AudioFormat).ToLower()
                    });
                }
                else if (downloadTask.VideoFormat != VideoFormat.None)
                {
                    arguments.AddRange(new List<string>
                    {
                        "-f",
                        (downloadTask.VideoQuality == VideoQuality.BestOverall)? "best" : "bestvideo+bestaudio/best",
                        "--recode-video",
                        Enum.GetName(typeof(VideoFormat), downloadTask.VideoFormat).ToLower()
                    });
                }

                // Url must be added last.
                arguments.Add(downloadTask.Url);

                // Transfer all arguments to the process information.
                foreach (var entry in arguments)
                {
                    processInfo.ArgumentList.Add(entry);
                }

                return processInfo;
            }
        }

        private async Task MainDownloadProcessAsync(ProcessStartInfo processInfo)
        {
            // Playlist indices start at 1.
            _currentIndex = 1;
            _resetEvent = new AutoResetEvent(false);
            _currentDownloadVideoIdentifier = null;
            _prevPercentage = 0.0;

            try
            {
                _mainDownloadProcess = new Process()
                {
                    StartInfo = processInfo,
                    EnableRaisingEvents = true
                };

                _mainDownloadProcess.Exited += this.HandleExited;
                _mainDownloadProcess.ErrorDataReceived += this.HandleError;
                _mainDownloadProcess.OutputDataReceived += this.HandleOutput;

                _mainDownloadProcess.Start();

                // Enables the asynchronus callback functions to receive the redirected data.
                _mainDownloadProcess.BeginOutputReadLine();
                _mainDownloadProcess.BeginErrorReadLine();

                // Wait for the process to finish.
                _logger.LogDebug("Waiting for the main download process to finish...");
                _resetEvent.WaitOne();

                // Diable the asynchronus callback functions.
                _mainDownloadProcess.CancelOutputRead();
                _mainDownloadProcess.CancelErrorRead();

                _logger.LogDebug($"Finished downloading files.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Main download process threw an exception:");

                _resetEvent.Set();

                // Prevent infinite loops.
                this.MarkDownloadTaskAsDownloaderError(_currentDownloadTaskId);

                await _notification.NotifyClientsAboutDownloaderError(_currentDownloadTaskId);
            }
            finally
            {
                // Prevents null pointer exceptions when killing the process.
                _currentDownloadTaskId = new Guid();

                // Remove all handlers etc. to prevent memory leaks.
                _mainDownloadProcess.Exited -= this.HandleExited;
                _mainDownloadProcess.ErrorDataReceived -= this.HandleError;
                _mainDownloadProcess.OutputDataReceived -= this.HandleOutput;
                _mainDownloadProcess.Dispose();
                _mainDownloadProcess = null;
            }
        }

        private void HandleExited(object sender, EventArgs e)
        {
            // Prevent exceptions by only allowing non killed processes to continue.
            if (!_wasKilledByInterrupt)
            {
                this.MarkDownloadTaskAsDownloaded(_currentDownloadTaskId);
                Task.Run(async () => await _notification.NotifyClientsAboutFinishedDownloadTaskAsync(_currentDownloadTaskId));

                // The last download of a playlist does not trigger the notification of the output
                // (Same goes for a simple download).
                this.SavePathForDownloadResult(_currentDownloadTaskId, _currentDownloadVideoIdentifier);
                this.SendDownloadResultFinishedNotification(_currentDownloadTaskId, _currentDownloadVideoIdentifier);
                this.MarkPossibleDownloadResultAsDownloaded(_currentDownloadTaskId, _currentDownloadVideoIdentifier);
            }

            _resetEvent.Set();
        }

        private void HandleError(object sender, DataReceivedEventArgs e)
        {
            // Only log the error, since this output includes the error and warning messages of 
            // youtube-dl which are handled in the info gathering process. Therefor sending a
            // downloader error to the user is not advised.
            if (e.Data != null)
            {
                _logger.LogError("Main download process threw Error: " + e.Data);
            }
        }

        private void HandleOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                _logger.LogDebug(e.Data);

                var regexVideoDownloadPlaylist = new Regex(@"^\[download\] Downloading video (?<currentIndex>[0-9]+) of [0-9]+$");
                var regexVideoWebPage = new Regex(@"^\[youtube\] (?<videoIdentifier>.+): Downloading webpage$");
                var regexVideoDownloadProgress = new Regex(@"^\[download\]\s\s?(?<percentage>[0-9]+\.[0-9]+)%.*$");
                var regexVideoConversion = new Regex(@"^\[ffmpeg\]\s.*Destination:.*$");
                var matchVideoDownloadPlaylist = regexVideoDownloadPlaylist.Match(e.Data);
                var matchVideoWebPage = regexVideoWebPage.Match(e.Data);
                var matchVideoDownloadProgress = regexVideoDownloadProgress.Match(e.Data);
                var matchVideoConversion = regexVideoConversion.Match(e.Data);

                if (matchVideoDownloadPlaylist.Success)
                {
                    var prevIndex = _currentIndex;
                    _currentIndex = Int32.Parse(matchVideoDownloadPlaylist.Groups["currentIndex"].Value);

                    // The next index always indicates that the previous entry was downloaded.
                    if (_currentIndex - 1 > 0)
                    {
                        this.SavePathForDownloadResult(_currentDownloadTaskId, _currentDownloadVideoIdentifier);
                        this.SendDownloadResultFinishedNotification(_currentDownloadTaskId, _currentDownloadVideoIdentifier);
                        this.MarkPossibleDownloadResultAsDownloaded(_currentDownloadTaskId, _currentDownloadVideoIdentifier);
                    }
                }
                else if (matchVideoWebPage.Success)
                {
                    _currentDownloadVideoIdentifier = matchVideoWebPage.Groups["videoIdentifier"].Value;

                    // --dump-json in the info process is unable to extract the video identifier for a 
                    // unavailable video. Therefor this must be inserted here.
                    // NOTE:
                    // The identifier stays null UNTIL the download action for the result itself if performed!
                    using (var db = _factory.CreateDbContext(new string[0]))
                    {
                        try
                        {
                            // Index must be used since a failed result does NOT yet contain the video identifier!
                            var result = db.DownloadResult
                                .Include(x => x.DownloadTask)
                                .Single(x => x.DownloadTask.Id == _currentDownloadTaskId && x.Index == _currentIndex);

                            if (string.IsNullOrEmpty(result.VideoIdentifier))
                            {
                                result.VideoIdentifier = _currentDownloadVideoIdentifier;
                            }

                            // Skip notifications for unavailable videos.
                            if (!result.HasError)
                            {
                                Task.Run(async () => await _notification.NotifyClientsAboutStartedDownloadAsync(_currentDownloadTaskId, result.Id));
                            }

                            db.SaveChanges();
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(exception, "Error while changing the video identifier based on main process stdOut information.");
                        }
                    }
                }
                else if (matchVideoDownloadProgress.Success)
                {
                    var percentage = matchVideoDownloadProgress.Groups["percentage"].Value;
                    var percentageDouble = Double.Parse(percentage.Replace(',', '.'), CultureInfo.InvariantCulture);

                    // Do not flood the client with messages.
                    if (percentageDouble - _prevPercentage >= _percentageMinDifference)
                    {
                        _prevPercentage = percentageDouble;

                        using (var db = _factory.CreateDbContext(new string[0]))
                        {
                            try
                            {
                                var result = db.DownloadResult
                                    .Include(x => x.DownloadTask)
                                    .Single(x => 
                                        x.DownloadTask.Id == _currentDownloadTaskId && 
                                        x.VideoIdentifier != null && 
                                        x.VideoIdentifier.Equals(_currentDownloadVideoIdentifier));

                                Task.Run(async () => await _notification.NotifyClientsAboutDownloadProgressAsync(_currentDownloadTaskId, result.Id, percentageDouble));
                            }
                            catch (Exception exception)
                            {
                                _logger.LogError(exception, "Error while performing progress notifications based on main process stdOut information.");
                            }
                        }
                    }
                }
                else if (matchVideoConversion.Success)
                {
                    using (var db = _factory.CreateDbContext(new string[0]))
                    {
                        try
                        {
                            var result = db.DownloadResult
                                .Include(x => x.DownloadTask)
                                .Single(x => 
                                    x.DownloadTask.Id == _currentDownloadTaskId && 
                                    x.VideoIdentifier != null && 
                                    x.VideoIdentifier.Equals(_currentDownloadVideoIdentifier));

                            Task.Run(async () => await _notification.NotifyClientsAboutDownloadConversionAsync(_currentDownloadTaskId, result.Id));
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(exception, "Error while performing conversion notifications based on main process stdOut information.");
                        }
                    }
                }
            }
        }


        private void SavePathForDownloadResult(Guid downloadTaskId, string videoIdentifier)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResult = db.DownloadResult
                        .Include(x => x.DownloadTask)
                        .Single(x => x.DownloadTask.Id == downloadTaskId && x.VideoIdentifier != null && x.VideoIdentifier.Equals(videoIdentifier));
                    var downloadFolder = _pathGenerator.GenerateDownloadFolderPath(downloadResult.DownloadTask.Id);

                    // Find the downloaded file. Only possible if no error occurred.
                    if (!downloadResult.HasError && Directory.Exists(downloadFolder))
                    {
                        foreach (string filePath in Directory.GetFiles(downloadFolder))
                        {
                            var fileVideoIdentifier = Path.GetFileNameWithoutExtension(filePath).Split(_pathGenerator.NameDilimiter)[0];

                            if (fileVideoIdentifier.Equals(downloadResult.VideoIdentifier))
                            {
                                // Set the file path in order to retrieve the file later on.
                                downloadResult.PathToFile = filePath;
                                db.SaveChanges();
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while tupdating the file path of task id={downloadTaskId}, videoIdentifier={videoIdentifier}.");
                }
            }
        }

        private void SendDownloadResultFinishedNotification(Guid downloadTaskId, string videoIdentifier)
        {
            // Skip notifications for unavailable videos.
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var result = db.DownloadResult
                        .Include(x => x.DownloadTask)
                        .Single(x => x.DownloadTask.Id == downloadTaskId && x.VideoIdentifier != null && x.VideoIdentifier.Equals(videoIdentifier));
                    var sendNotification = !result.HasError;

                    if (sendNotification)
                    {
                        Task.Run(async () => await _notification.NotifyClientsAboutFinishedDownloadResultAsync(downloadTaskId, result.Id));
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while sending a finished notification for task {downloadTaskId}.");
                }
            }
        }

        private void MarkPossibleDownloadResultAsDownloaded(Guid downloadTaskId, string videoIdentifier)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResult = db.DownloadResult
                        .Include(x => x.DownloadTask)
                        .Single(x => x.DownloadTask.Id == downloadTaskId && x.VideoIdentifier != null && x.VideoIdentifier.Equals(videoIdentifier));
                    downloadResult.WasDownloaded = true;

                    db.SaveChanges();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while marking result with identifier={videoIdentifier} of task with id={downloadTaskId} as downloaded.");
                }
            }
        }

        private void MarkDownloadTaskAsDownloaded(Guid downloadTaskId)
        {
            try
            {
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    var downloadTask = db.DownloadTask.Find(downloadTaskId);
                    downloadTask.WasDownloaded = true;

                    db.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error while marking {downloadTaskId} as downloaded.");
            }
        }

        private void MarkDownloadTaskAsDownloaderError(Guid downloadTaskId)
        {
            try
            {
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    var downloadTask = db.DownloadTask.Find(downloadTaskId);
                    downloadTask.HadDownloaderError = true;

                    db.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error while marking error on {downloadTaskId}.");
            }
        }

        public Task HandleDownloadInterrupt(Guid downloadTaskId)
        {
            if(downloadTaskId == _currentDownloadTaskId)
            {
                // Set the flag indicating that the process was killed manually.
                _wasKilledByInterrupt = true;

                _logger.LogDebug($"Killing conversion processes.");

                // Kill all conversion processes (avconv, ffmpeg) that might block the deletion 
                // of files.
                Process.GetProcessesByName("ffmpeg").ToList().ForEach(x => x.Kill());
                Process.GetProcessesByName("avconv").ToList().ForEach(x => x.Kill());

                _logger.LogDebug($"Killing main process.");

                _mainDownloadProcess.Kill();
            }
            else
            {
                _logger.LogError($"Error while handling interrupt request. Received task id {downloadTaskId} does not match the currently active one {_currentDownloadTaskId}");
            }

            return Task.CompletedTask;
        }
    }
}

