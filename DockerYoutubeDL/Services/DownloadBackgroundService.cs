using DockerYoutubeDL.DAL;
using DockerYoutubeDL.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Newtonsoft.Json;
using DockerYoutubeDL.Models;
using System.Text.RegularExpressions;
using System.Globalization;

namespace DockerYoutubeDL.Services
{
    public class DownloadBackgroundService : BackgroundService
    {
        // Factory is used since the service is instantiated once as singleton and therefor 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IConfiguration _config;
        private IHubContext<UpdateHub> _hub;
        private UpdateClientContainer _container;
        private DownloadPathGenerator _pathGenerator;

        private int _waitTimeSeconds = 10;
        private Policy _notificationPolicy;

        private string _currentDownloadVideoIdentifier = null;
        // Percentages are 0-100.
        private double _prevPercentage = 0;
        private double _percentageMinDifference = 10;
        private readonly string _nameDelimiter = "-----------";

        public DownloadBackgroundService(
            IDesignTimeDbContextFactory<DownloadContext> factory,
            ILogger<DownloadBackgroundService> logger,
            IConfiguration config,
            IHubContext<UpdateHub> hub,
            UpdateClientContainer container,
            DownloadPathGenerator pathGenerator)
        {
            if (factory == null || logger == null || config == null ||
                hub == null || container == null || pathGenerator == null)
            {
                throw new ArgumentException();
            }

            _factory = factory;
            _logger = logger;
            _config = config;
            _hub = hub;
            _container = container;
            _pathGenerator = pathGenerator;

            // The same policy is used for all notification attempts.
            _notificationPolicy = Policy.Handle<Exception>()
                .WaitAndRetryAsync(5, (count) => TimeSpan.FromSeconds(count + 1), (e, retryCount, context) =>
                {
                    _logger.LogError(e, context["errorMessage"] as string);
                });
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
                    Path.Combine(downloadFolder, $"%(id)s{_nameDelimiter}%(title)s.%(ext)s").ToString(),
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
                    var process = new Process()
                    {
                        StartInfo = processInfo,
                        EnableRaisingEvents = true
                    };

                    process.Exited += (s, e) =>
                    {
                        resetEvent.Set();
                    };
                    process.OutputDataReceived += (s, e) =>
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

                            Task.Run(async () => { await this.NotifyClientAboutReceivedDownloadInformationAsync(result); });
                        }
                        else
                        {
                            _logger.LogDebug("Received info data was null.");
                        }
                    };

                    process.Start();
                    // Enables the asynchronus callback function to receive the redirected data.
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

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
        }

        private async Task MainDownloadProcessAsync(ProcessStartInfo processInfo, Guid downloadTaskId)
        {
            var resetEvent = new AutoResetEvent(false);

            try
            {
                var process = new Process()
                {
                    StartInfo = processInfo,
                    EnableRaisingEvents = true
                };

                process.Exited += (s, e) =>
                {
                    // The last download of a playlist does not trigger the notification of the output
                    // (Same goes for a simple download).
                    Task.Run(async () => await this.NotifyClientAboutFinishedDownloadAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                    Task.Run(async () => await this.MarkDownloadResultAsDownloadedAsync(downloadTaskId, _currentDownloadVideoIdentifier));

                    resetEvent.Set();
                };
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        _logger.LogDebug(e.Data);

                        var regexVideoDownloadPlaylist = new Regex(@"^\[download\] Downloading video (?<currentIndex>[0-9]+) of [0-9]+$");
                        var regexVideoWebPage = new Regex(@"^\[youtube\] (?<videoIdentifier>.+): Downloading webpage$");
                        var regexVideoDownloadProgress = new Regex(@"^\[download\]\s\s?(?<percentage>[0-9]+\.[0-9]+)%.*$");
                        var matchVideoDownloadPlaylist = regexVideoDownloadPlaylist.Match(e.Data);
                        var matchVideoWebPage = regexVideoWebPage.Match(e.Data);
                        var matchVideoDownloadProgress = regexVideoDownloadProgress.Match(e.Data);

                        if (matchVideoDownloadPlaylist.Success)
                        {
                            Task.Run(async () => await this.NotifyClientAboutFinishedDownloadAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                            Task.Run(async () => await this.MarkDownloadResultAsDownloadedAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                        }
                        else if (matchVideoWebPage.Success)
                        {
                            _currentDownloadVideoIdentifier = matchVideoWebPage.Groups["videoIdentifier"].Value;
                            Task.Run(async () => await this.NotifyClientAboutStartedDownloadAsync(downloadTaskId, _currentDownloadVideoIdentifier));
                        }
                        else if(matchVideoDownloadProgress.Success)
                        {
                            var percentage = matchVideoDownloadProgress.Groups["percentage"].Value;
                            var percentageDouble = Double.Parse(percentage.Replace(',', '.'), CultureInfo.InvariantCulture);

                            // Do not flood the client with messages.
                            if(percentageDouble - _prevPercentage >= _percentageMinDifference)
                            {
                                _prevPercentage = percentageDouble;
                                Task.Run(async () => await this.NotifyClientAboutDownloadProgressAsync(downloadTaskId, _currentDownloadVideoIdentifier, percentageDouble));
                            }
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Received download data was null.");
                    }
                };

                process.Start();
                // Enables the asynchronus callback function to receive the redirected data.
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for the process to finish.
                _logger.LogDebug("Waiting for the main download process to finish...");
                resetEvent.WaitOne();
                _logger.LogDebug($"Finished downloading files.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Main download process threw an exception:");

                resetEvent.Set();
                await this.NotifyClientAboutFailedDownloadAsync(downloadTaskId);
            }
            finally
            {
                // Remove download task in order to not download it again.
                // If not removed this would cause an endless loop.
                await this.RemoveDownloadTaskAsync(downloadTaskId);
                _currentDownloadVideoIdentifier = null;
                _prevPercentage = 0;
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

        private async Task NotifyClientAboutReceivedDownloadInformationAsync(DownloadResult downloadResult)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var client = _hub.Clients.Client(_container.StoredClients[downloadResult.IdentifierDownloader]);

                _logger.LogDebug($"Notifying client about the received download info for result with id={downloadResult.Id}.");

                // Notify the matching client about the received download information.
                await _notificationPolicy.ExecuteAsync(
                    (context) => client.SendAsync(nameof(IUpdateClient.ReceivedDownloadInfo), downloadResult),
                    new Dictionary<string, object>()
                    {
                        { "errorMessage", $"Error while notifying user {downloadResult.IdentifierDownloader} about the received information." }
                    }
                );
            }
        }

        private async Task NotifyClientAboutStartedDownloadAsync(Guid downloadTaskId, string videoIdentifier)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                    var downloadResultId = db.DownloadResult
                        .First(x => x.IdentifierDownloadTask == downloadTaskId && x.VideoIdentifier == videoIdentifier)
                        .Id;

                    var client = _hub.Clients.Client(_container.StoredClients[downloadTask.Downloader]);

                    _logger.LogDebug($"Notifying client about the started download task with id={downloadTaskId}, result.id={downloadResultId}.");

                    // Notify the matching client about the started download.
                    await _notificationPolicy.ExecuteAsync(
                        (context) => client.SendAsync(nameof(IUpdateClient.DownloadStarted), downloadTaskId, downloadResultId),
                        new Dictionary<string, object>()
                        {
                            { "errorMessage", $"Error while notifying user {downloadTask.Downloader} about the started download result id={downloadResultId}." }
                        }
                    );
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while notifying the user of {downloadTaskId} about the started download video={videoIdentifier}.");
                }
            }
        }

        private async Task NotifyClientAboutFailedDownloadAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                var client = _hub.Clients.Client(_container.StoredClients[downloadTask.Downloader]);

                _logger.LogDebug($"Notifying client about failed download task with id={downloadTaskId}.");

                // Notify the matching client about the failure.
                await _notificationPolicy.ExecuteAsync(
                    (context) => client.SendAsync(nameof(IUpdateClient.DownloadFailed), downloadTaskId),
                    new Dictionary<string, object>()
                    {
                        { "errorMessage", $"Error while notifying user {downloadTask.Downloader} about the failed download." }
                    }
                );
            }
        }

        private async Task NotifyClientAboutFinishedDownloadAsync(Guid downloadTaskId, string videoIdentifier)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResult = db.DownloadResult.First(x => x.IdentifierDownloadTask == downloadTaskId && x.VideoIdentifier == videoIdentifier);
                    var downloadFolder = _pathGenerator.GenerateDownloadFolderPath(downloadResult.IdentifierDownloader, downloadResult.IdentifierDownloadTask);

                    // Find the downloaded file.
                    foreach (string filePath in Directory.GetFiles(downloadFolder))
                    {
                        var fileVideoIdentifier = Path.GetFileNameWithoutExtension(filePath).Split(_nameDelimiter)[0];

                        if (fileVideoIdentifier.Equals(downloadResult.VideoIdentifier))
                        {
                            var client = _hub.Clients.Client(_container.StoredClients[downloadResult.IdentifierDownloader]);

                            // Set the file path in order to retrieve the file later on.
                            downloadResult.PathToFile = filePath;

                            _logger.LogDebug($"Notifying client about result with id={downloadResult.Id}.");

                            await _notificationPolicy.ExecuteAsync(
                                (context) => client.SendAsync(nameof(IUpdateClient.DownloadFinished), downloadResult.IdentifierDownloadTask, downloadResult.Id),
                                new Dictionary<string, object>()
                                {
                                    { "errorMessage", $"Error while notifying user {downloadResult.IdentifierDownloader} about the finished download." }
                                }
                            );
                        }
                    }

                    await db.SaveChangesAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while notifying the user of {downloadTaskId} about the finished download.");
                }
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

        private async Task NotifyClientAboutDownloadProgressAsync(Guid downloadTaskId, string videoIdentifier, double percentage)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResult = db.DownloadResult.First(x => x.IdentifierDownloadTask == downloadTaskId && x.VideoIdentifier == videoIdentifier);
                    var client = _hub.Clients.Client(_container.StoredClients[downloadResult.IdentifierDownloader]);

                    _logger.LogDebug($"Notifying client about progress {percentage} with id={downloadResult.Id}.");

                    await _notificationPolicy.ExecuteAsync(
                        (context) => client.SendAsync(nameof(IUpdateClient.DownloadProgress), downloadResult.IdentifierDownloadTask, downloadResult.Id, percentage),
                        new Dictionary<string, object>()
                        {
                            { "errorMessage", $"Error while notifying user {downloadResult.IdentifierDownloader} about the progresss {percentage}." }
                        }
                    );
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while notifying the user of {downloadTaskId} about the progress {percentage}.");
                }
            }
        }
    }
}

