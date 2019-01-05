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
            var processInfo = await this.GenerateProcessStartInfoAsync(downloadTaskId);

            var executeTask = Task.Run(async () =>
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
                        resetEvent.Set();
                    };
                    process.OutputDataReceived += (s, e) =>
                    {
                        _logger.LogDebug(e.Data);
                    };

                    await this.NotifyClientAboutStartedDownloadAsync(downloadTaskId);

                    process.Start();
                    // Enables the asynchronus callback function to receive the redirected data.
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for the process to finish.
                    _logger.LogDebug("Waiting for the download to finish...");
                    resetEvent.WaitOne();
                    _logger.LogDebug($"Finished downloading.");

                    await this.NotifyClientAboutFinishedDownloadAsync(downloadTaskId);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Download process threw an exception:");

                    resetEvent.Set();
                    await this.NotifyClientAboutFailedDownloadAsync(downloadTaskId);
                }
                finally
                {
                    // Remove download task in order to not download it again.
                    // If not removed this would cause an endless loop.
                    await this.RemoveDownloadTaskAsync(downloadTaskId);
                }
            }, stoppingToken);

            await executeTask;
        }

        private async Task<ProcessStartInfo> GenerateProcessStartInfoAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);

                _logger.LogDebug($"Preparing to download {downloadTask.Url}");

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
                    Path.Combine(downloadFolder, "%(title)s.%(ext)s").ToString(),
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

        private async Task RemoveDownloadTaskAsync(Guid id)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var downloadTask = await db.DownloadTask.FindAsync(id);
                db.DownloadTask.Remove(downloadTask);

                await db.SaveChangesAsync();
            }
        }

        private async Task NotifyClientAboutStartedDownloadAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                var client = _hub.Clients.Client(_container.StoredClients[downloadTask.Downloader]);

                _logger.LogDebug($"Notifying client about the started download task with id={downloadTaskId}.");

                // Notify the matching client about the started download.
                await _notificationPolicy.ExecuteAsync(
                    (context) => client.SendAsync(nameof(IUpdateClient.DownloadStarted), downloadTaskId),
                    new Dictionary<string, object>()
                    {
                        { "errorMessage", $"Error while notifying user {downloadTask.Downloader} about the started download." }
                    }
                );
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

        private async Task NotifyClientAboutFinishedDownloadAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                var downloadFolder = _pathGenerator.GenerateDownloadFolderPath(downloadTask.Downloader, downloadTask.Id);
                var files = Directory.GetFiles(downloadFolder);
                var generatedResults = new List<DownloadResult>();

                // Multiple files can be downloaded at once (playlists etc.)
                foreach (var filePath in files)
                {
                    var result = new DownloadResult()
                    {
                        PathToFile = filePath,
                        IdentifierDownloader = downloadTask.Downloader,
                        IdentifierDownloadTask = downloadTask.Id
                    };

                    // Keep track of generated instances for notifications. Must be done since the 
                    // entries receive their respective id AFTER the save function was called.
                    generatedResults.Add(result);
                    db.DownloadResult.Add(result);
                }

                // Notify the matching client for each downloaded file.
                var client = _hub.Clients.Client(_container.StoredClients[downloadTask.Downloader]);
                foreach (var result in generatedResults)
                {
                    _logger.LogDebug($"Notifying client about result with id={result.Id}.");

                    await _notificationPolicy.ExecuteAsync(
                        (context) => client.SendAsync(nameof(IUpdateClient.DownloadFinished), result.IdentifierDownloadTask, result.Id),
                        new Dictionary<string, object>()
                        {
                            { "errorMessage", $"Error while notifying user {downloadTask.Downloader} about the finished download." }
                        }
                    );
                }

                await db.SaveChangesAsync();
            }
        }
    }
}

