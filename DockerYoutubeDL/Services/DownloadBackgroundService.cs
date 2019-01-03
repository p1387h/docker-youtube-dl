using DockerYoutubeDL.DAL;
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

namespace DockerYoutubeDL.Services
{
    public class DownloadBackgroundService : BackgroundService
    {
        private int _waitTimeSeconds = 10;
        // Factory is used since the service is instantiated once as singleton and therefor 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IConfiguration _config;

        public DownloadBackgroundService(
            IDesignTimeDbContextFactory<DownloadContext> factory,
            ILogger<DownloadBackgroundService> logger,
            IConfiguration config)
        {
            if (factory == null || logger == null || config == null)
            {
                throw new ArgumentException();
            }

            _factory = factory;
            _logger = logger;
            _config = config;
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

                    if (db.DownloadTask.Any(x => !x.WasDownloaded))
                    {
                        // Next task is the one that was queued earliest and was not yet downloaded. 
                        var nextTask = db.DownloadTask
                            .Where(x => !x.WasDownloaded)
                            .Select(x => new Tuple<DownloadTask, TimeSpan>(x, now.Subtract(x.DateAdded)))
                            .OrderByDescending(x => x.Item2)
                            .FirstOrDefault()
                            .Item1;

                        _logger.LogDebug($"Next download: Downloader={nextTask.Downloader}, Url={nextTask.Url}, Id={nextTask.Id}");

                        await this.ExecuteDownloadTask(nextTask.Id, stoppingToken);
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

        private async Task ExecuteDownloadTask(Guid downloadTaskId, CancellationToken stoppingToken)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);

                var downloadRootFolder = _config.GetValue<string>("DownloadRootFolder");
                var downloadFolder = Path.Combine(downloadRootFolder, downloadTask.Downloader.ToString()).ToString();
                var ffmpegLocation = _config.GetValue<string>("FfmpegLocation");
                var youtubeDlLocation = _config.GetValue<string>("YoutubeDlLocation");

                _logger.LogDebug($"Preparing to download {downloadTask.Url}");

                var arguments = new List<string>()
                {
                    "-x",
                    "--audio-format",
                    "mp3",
                    "--no-call-home",
                    "--ffmpeg-location",
                    $"{ffmpegLocation}",
                    "-o",
                    $"\"{Path.Combine(downloadFolder, "%(title)s.%(ext)s").ToString()}\"",
                    downloadTask.Url
                };
                var processInfo = new ProcessStartInfo(youtubeDlLocation) { UseShellExecute = true };
                foreach (var entry in arguments)
                {
                    processInfo.ArgumentList.Add(entry);
                }

                var executeTask = Task.Run(async () =>
                {
                    var resetEvent = new AutoResetEvent(false);
                    var process = new Process()
                    {
                        StartInfo = processInfo,
                        EnableRaisingEvents = true
                    };
                    process.Exited += (s, e) =>
                    {
                        resetEvent.Set();
                    };
                    process.Start();

                    // Wait for the download to finish.
                    resetEvent.WaitOne();

                    if(!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogDebug($"Finished downloading {downloadTask.Url}, marking entity as downloaded.");

                        // Mark the download as finished.
                        downloadTask.WasDownloaded = true;
                        await db.SaveChangesAsync();
                    }
                }, stoppingToken);

                await executeTask;
            }
        }
    }
}

