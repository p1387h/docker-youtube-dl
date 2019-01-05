using DockerYoutubeDL.DAL;
using DockerYoutubeDL.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore.Design;
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

        private int _waitTimeSeconds = 10;

        private string _currentDownloadVideoIdentifier = null;
        // Percentages are 0-100.
        private double _prevPercentage = 0;
        private double _percentageMinDifference = 10;

        private Guid _infoDownloadProcessOwner = new Guid();
        private Guid _mainDownloadProcessOwner = new Guid();
        private Process _infoDownloadProcess = null;
        private Process _mainDownloadProcess = null;

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
                    var now = DateTime.Now;

                    _logger.LogDebug("Checking for pending download task...");

                    if (db.DownloadTask.Any())
                    {
                        // Next task is the one that was queued earliest and was not yet downloaded. 
                        var nextTask = db.DownloadTask
                            .Select(x => new Tuple<DownloadTask, TimeSpan>(x, now.Subtract(x.DateAdded)))
                            .OrderByDescending(x => x.Item2)
                            .FirstOrDefault()
                            .Item1;

                        _logger.LogDebug($"Next download: Downloader={nextTask.Downloader}, Url={nextTask.Url}, Id={nextTask.Id}");

                        await this.ExecuteDownloadTaskAsync(nextTask.Id, stoppingToken);
                        hasDownloaded = true;
                    }
                }

                // Don't wait inbetween downloads if more are queued.
                if (!hasDownloaded)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_waitTimeSeconds), stoppingToken);
                }
            }
        }

        private async Task ExecuteDownloadTaskAsync(Guid downloadTaskId, CancellationToken stoppingToken)
        {
            var infoProcessInfo = await this.GenerateInfoProcessStartInfoAsync(downloadTaskId);
            var mainProcessInfo = await this.GenerateMainProcessStartInfoAsync(downloadTaskId);

            await Task.Run(async () => await this.InfoDownloadProcessAsync(infoProcessInfo, downloadTaskId), stoppingToken);
            await Task.Run(async () => await this.MainDownloadProcessAsync(mainProcessInfo, downloadTaskId), stoppingToken);
        }

        private async Task<ProcessStartInfo> GenerateInfoProcessStartInfoAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);

                _logger.LogDebug($"Generating info ProcessStartInformation for {downloadTask.Url}");

                var youtubeDlLocation = _config.GetValue<string>("YoutubeDlLocation");
                var processInfo = new ProcessStartInfo(youtubeDlLocation)
                {
                    UseShellExecute = false,
                    WorkingDirectory = baseDir,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Arguments = $"--no-call-home -j {downloadTask.Url}"
                };

                return processInfo;
            }
        }

        private async Task<ProcessStartInfo> GenerateMainProcessStartInfoAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);

                _logger.LogDebug($"Generating main ProcessStartInformation for {downloadTask.Url}");

                var downloadFolder = _pathGenerator.GenerateDownloadFolderPath(downloadTask.Downloader, downloadTask.Id).ToString();
                var ffmpegLocation = _config.GetValue<string>("FfmpegLocation");
                var youtubeDlLocation = _config.GetValue<string>("YoutubeDlLocation");

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
                    "--ffmpeg-location",
                    $"{ffmpegLocation}",
                    "-o",
                    Path.Combine(downloadFolder, $"%(id)s{_pathGenerator.NameDilimiter}%(title)s.%(ext)s").ToString(),
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

        private async Task InfoDownloadProcessAsync(ProcessStartInfo processInfo, Guid downloadTaskId)
        {
            var resetEvent = new AutoResetEvent(false);

            try
            {
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                    _infoDownloadProcessOwner = downloadTask.Downloader;
                    _infoDownloadProcess = new Process()
                    {
                        StartInfo = processInfo,
                        EnableRaisingEvents = true
                    };

                    _infoDownloadProcess.Exited += (s, e) =>
                    {
                        resetEvent.Set();
                    };
                    _infoDownloadProcess.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            var template = JsonConvert.DeserializeObject<YoutubeDlOutputTemplate>(e.Data);
                            var result = new DownloadResult()
                            {
                                IdentifierDownloader = downloadTask.Downloader,
                                IdentifierDownloadTask = downloadTask.Id,
                                Index = (string.IsNullOrEmpty(template.playlist_index)) ? 0 : Int32.Parse(template.playlist_index),
                                Url = template.webpage_url,
                                IsPartOfPlaylist = !string.IsNullOrEmpty(template.playlist_index),
                                Name = template.title,
                                VideoIdentifier = template.id
                            };

                            db.DownloadResult.Add(result);
                            db.SaveChanges();

                            _logger.LogDebug($"Info received: Name={result.Name}, Index={result.Index}, IsPartOfPlaylist={result.IsPartOfPlaylist}");

                            Task.Run(async () => { await _notification.NotifyClientAboutReceivedDownloadInformationAsync(result); });
                        }
                        else
                        {
                            _logger.LogDebug("Received info data was null.");
                        }
                    };

                    _infoDownloadProcess.Start();
                    // Enables the asynchronus callback function to receive the redirected data.
                    _infoDownloadProcess.BeginOutputReadLine();
                    _infoDownloadProcess.BeginErrorReadLine();

                    // Wait for the process to finish.
                    _logger.LogDebug("Waiting for the info download process to finish...");
                    resetEvent.WaitOne();
                    _logger.LogDebug($"Finished downloading information.");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Info download process threw an exception:");

                resetEvent.Set();
            }
            finally
            {
                _infoDownloadProcessOwner = new Guid();
                _infoDownloadProcess = null;
            }
        }

        private async Task MainDownloadProcessAsync(ProcessStartInfo processInfo, Guid downloadTaskId)
        {
            var resetEvent = new AutoResetEvent(false);

            try
            {
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                    _mainDownloadProcessOwner = downloadTask.Downloader;
                }
                _mainDownloadProcess = new Process()
                {
                    StartInfo = processInfo,
                    EnableRaisingEvents = true
                };

                _mainDownloadProcess.Exited += (s, e) =>
                {
                    // The last download of a playlist does not trigger the notification of the output
                    // (Same goes for a simple download).
                    Task.Run(async () => await _notification.NotifyClientAboutFinishedDownloadAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                    Task.Run(async () => await this.MarkDownloadResultAsDownloadedAsync(downloadTaskId, _currentDownloadVideoIdentifier));

                    resetEvent.Set();
                };
                _mainDownloadProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        _logger.LogDebug(e.Data);

                        var regexVideoDownloadPlaylist = new Regex(@"^\[download\] Downloading video (?<currentIndex>[0-9]+) of [0-9]+$");
                        var regexVideoWebPage = new Regex(@"^\[youtube\] (?<videoIdentifier>.+): Downloading webpage$");
                        var regexVideoDownloadProgress = new Regex(@"^\[download\]\s\s?(?<percentage>[0-9]+\.[0-9]+)%.*$");
                        var regexVideoConverting = new Regex(@"^\[ffmpeg] Destination:.*$");
                        var matchVideoDownloadPlaylist = regexVideoDownloadPlaylist.Match(e.Data);
                        var matchVideoWebPage = regexVideoWebPage.Match(e.Data);
                        var matchVideoDownloadProgress = regexVideoDownloadProgress.Match(e.Data);
                        var matchVideoConverting = regexVideoConverting.Match(e.Data);

                        if (matchVideoDownloadPlaylist.Success)
                        {
                            Task.Run(async () => await _notification.NotifyClientAboutFinishedDownloadAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                            Task.Run(async () => await this.MarkDownloadResultAsDownloadedAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                        }
                        else if (matchVideoWebPage.Success)
                        {
                            _currentDownloadVideoIdentifier = matchVideoWebPage.Groups["videoIdentifier"].Value;
                            Task.Run(async () => await _notification.NotifyClientAboutStartedDownloadAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                        }
                        else if (matchVideoDownloadProgress.Success)
                        {
                            var percentage = matchVideoDownloadProgress.Groups["percentage"].Value;
                            var percentageDouble = Double.Parse(percentage.Replace(',', '.'), CultureInfo.InvariantCulture);

                            // Do not flood the client with messages.
                            if (percentageDouble - _prevPercentage >= _percentageMinDifference)
                            {
                                _prevPercentage = percentageDouble;
                                Task.Run(async () => await _notification.NotifyClientAboutDownloadProgressAsync(downloadTaskId, _currentDownloadVideoIdentifier, percentageDouble));
                            }
                        }
                        else if (matchVideoConverting.Success)
                        {
                            Task.Run(async () => await _notification.NotifyClientAboutDownloadConversionAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Received download data was null.");
                    }
                };

                _mainDownloadProcess.Start();
                // Enables the asynchronus callback function to receive the redirected data.
                _mainDownloadProcess.BeginOutputReadLine();
                _mainDownloadProcess.BeginErrorReadLine();

                // Wait for the process to finish.
                _logger.LogDebug("Waiting for the main download process to finish...");
                resetEvent.WaitOne();
                _logger.LogDebug($"Finished downloading files.");

                // Remove download task in order to not download it again.
                // If not removed this would cause an endless loop.
                await this.RemoveDownloadTaskAsync(downloadTaskId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Main download process threw an exception:");

                resetEvent.Set();
                await _notification.NotifyClientAboutFailedDownloadAsync(downloadTaskId);
            }
            finally
            {
                _currentDownloadVideoIdentifier = null;
                _prevPercentage = 0;
                _mainDownloadProcessOwner = new Guid();
                _mainDownloadProcess = null;
            }
        }

        private async Task RemoveDownloadTaskAsync(Guid id)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var downloadTask = await db.DownloadTask.FindAsync(id);
                db.DownloadTask.Remove(downloadTask);

                await db.SaveChangesAsync();
            }
        }

        private async Task MarkDownloadResultAsDownloadedAsync(Guid downloadTaskId, string videoIdentifier)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResult = db.DownloadResult.First(x => x.IdentifierDownloadTask == downloadTaskId && x.VideoIdentifier == videoIdentifier);
                    downloadResult.WasDownloaded = true;

                    await db.SaveChangesAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while Marking the video {videoIdentifier} of {downloadTaskId} as finished.");
                }
            }
        }

        public async Task HandleDisconnectAsync(Guid userIdentifier)
        {
            int maxRetryCount = 8;

            _logger.LogDebug($"Handling the DisconnectNotification of user {userIdentifier}.");

            // Only kill the processes if they are associated with the current user.
            if (_infoDownloadProcessOwner == userIdentifier)
            {
                _infoDownloadProcess.Kill();
            }
            if (_mainDownloadProcessOwner == userIdentifier)
            {
                _mainDownloadProcess.Kill();
            }

            _logger.LogDebug($"Deleting all entries (db/files) of user {userIdentifier}.");

            // Polly must be used since a disconnect interferes with all current operations.
            await this.DeleteDbEntriesAsync(userIdentifier, maxRetryCount);
            await this.DeleteDownloadFilesAsync(userIdentifier, maxRetryCount);
        }

        private async Task DeleteDbEntriesAsync(Guid userIdentifier, int maxRetryCount)
        {
            await Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(maxRetryCount, (retry) => TimeSpan.FromSeconds(retry * retry), (result, waitTime, retry, context) =>
                {
                    if (retry == maxRetryCount)
                    {
                        _logger.LogCritical($"Polly was not able to remove the entries of user {userIdentifier} after the maximum number of retries.");
                    }
                    else
                    {
                        _logger.LogError($"Error while removing the entries of user {userIdentifier}:");
                    }
                }).ExecuteAsync(async () =>
                {
                    using (var db = _factory.CreateDbContext(new string[0]))
                    {
                        // Entries of the user must be removed from the db.
                        var toDeleteTasks = db.DownloadTask
                            .Where(x => x.Downloader == userIdentifier);
                        var toDeleteResults = db.DownloadResult
                            .Where(x => x.IdentifierDownloader == userIdentifier);
                        db.DownloadTask.RemoveRange(toDeleteTasks);
                        db.DownloadResult.RemoveRange(toDeleteResults);

                        await db.SaveChangesAsync();

                        _logger.LogDebug($"Successfully removed db entries of user {userIdentifier}");
                    }
                });
        }

        private async Task DeleteDownloadFilesAsync(Guid userIdentifier, int maxRetryCount)
        {
            // Remove any folders / downloads of the user.
            await Policy
                .HandleResult<bool>((deleted) => !deleted)
                .WaitAndRetryAsync(maxRetryCount, (retry) => TimeSpan.FromSeconds(retry * retry), (result, waitTime, retry, context) =>
                {
                    if (retry == maxRetryCount)
                    {
                        _logger.LogCritical($"Polly was not able to remove the directory of user {userIdentifier} after the maximum number of retries.");
                    }
                    else
                    {
                        _logger.LogError($"Error while removing downloads of user {userIdentifier}:");
                    }
                }).ExecuteAsync(() =>
                {
                    _logger.LogDebug($"Trying to remove downloads of user {userIdentifier}.");

                    // IOExceptions are not correctly handled by Polly. Therefor the result is used.
                    try
                    {
                        var folderPath = _pathGenerator.GenerateDownloadFolderPath(userIdentifier);
                        Directory.Delete(folderPath, true);

                        _logger.LogDebug($"Successfully removed files of user {userIdentifier}");
                    }
                    catch (IOException e)
                    {
                        return Task.FromResult<bool>(false);
                    }
                    return Task.FromResult<bool>(true);
                });
        }
    }
}

