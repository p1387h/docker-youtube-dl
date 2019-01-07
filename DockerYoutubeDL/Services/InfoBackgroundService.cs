using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Services
{
    public class InfoBackgroundService : BackgroundService
    {
        // Factory is used since the service is instantiated once as singleton and therefor 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IConfiguration _config;
        private DownloadPathGenerator _pathGenerator;
        private NotificationService _notification;

        private int _waitTimeBetweenDownloadChecksSeconds = 10;

        private Process _infoDownloadProcess = null;

        public InfoBackgroundService(
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

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogDebug("InfoBackgroundService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var hasDownloaded = false;

                // Find the next download Target.
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    Func<DownloadTask, bool> selector = new Func<DownloadTask, bool>(x => !x.HadInformationGathered);
                    var now = DateTime.Now;

                    _logger.LogDebug("Checking for task that requires information gathering...");

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

                        // Gather all necessary information
                        var infoProcessInfo = await this.GenerateInfoProcessStartInfoAsync(nextTask.Id);
                        await this.InfoDownloadProcessAsync(infoProcessInfo, nextTask.Id);

                        // Allow for downloads to directly follow one another.
                        hasDownloaded = true;
                    }
                }

                // Don't wait inbetween downloads if more are queued.
                if (!hasDownloaded)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_waitTimeBetweenDownloadChecksSeconds), stoppingToken);
                }
            }
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
                    Arguments = $"--no-call-home --dump-json --ignore-errors {downloadTask.Url}"
                };

                return processInfo;
            }
        }

        private Task InfoDownloadProcessAsync(ProcessStartInfo processInfo, Guid downloadTaskId)
        {
            // Playlist indices start at 1.
            var currentIndex = 1;
            var resetEvent = new AutoResetEvent(false);

            try
            {
                _infoDownloadProcess = new Process()
                {
                    StartInfo = processInfo,
                    EnableRaisingEvents = true
                };
                _infoDownloadProcess.Exited += (s, e) =>
                {
                    // Unavailable videos do not return the name of their playlist, thus not setting the 
                    // flag correctly. This ensures that all flags are set accordingly.
                    using (var db = _factory.CreateDbContext(new string[0]))
                    {
                        var downloadResults = db.DownloadResult
                            .Include(x => x.DownloadTask)
                            .Where(x => x.DownloadTask.Id == downloadTaskId)
                            .ToList();
                        downloadResults.ForEach(x => x.IsPartOfPlaylist = downloadResults.Count > 1);

                        db.SaveChanges();
                    }

                    // Task must be marked in order for the download task to pick it up.
                    using (var db = _factory.CreateDbContext(new string[0]))
                    {
                        db.DownloadTask.Find(downloadTaskId).HadInformationGathered = true;
                        db.SaveChanges();
                    }

                    resetEvent.Set();
                };
                _infoDownloadProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        _logger.LogDebug(e.Data);

                        var regexVideoProblem = new Regex(@"^ERROR:\s(?<message>.*)$");
                        var matchVideoProblem = regexVideoProblem.Match(e.Data);

                        if (matchVideoProblem.Success)
                        {
                            var message = matchVideoProblem.Groups["message"].Value;

                            // An unavailable video must still be marked in the db (no url but can be accessed by the index).
                            using (var db = _factory.CreateDbContext(new string[0]))
                            {
                                try
                                {
                                    // An error means a video is unavailable and can not be downloaded.
                                    var result = new DownloadResult()
                                    {
                                        DownloadTask = db.DownloadTask.Find(downloadTaskId),
                                        Index = currentIndex,
                                        HasError = true,
                                        Message = message
                                    };

                                    db.DownloadResult.Add(result);
                                    db.DownloadTask.Find(downloadTaskId).DownloadResult.Add(result);
                                    db.SaveChanges();

                                    _logger.LogDebug($"Error Info received and added to db: Index={result.Index}, Message={result.Message}");

                                    // Notify the client about the error. Only do this AFTER the db saved the id of the result!
                                    var outputInfo = new YoutubeDlOutputInfo()
                                    {
                                        DownloadResultIdentifier = result.Id,
                                        DownloadTaskIdentifier = result.DownloadTask.Id,
                                        Index = result.Index,
                                        HasError = result.HasError,
                                        Message = result.Message,
                                    };
                                    Task.Run(async () => await _notification.NotifyClientsAboutFailedDownloadAsync(outputInfo));
                                }
                                catch (Exception exception)
                                {
                                    _logger.LogError(exception, "Error while adding parsed info process stdErr information to database.");
                                }
                            }

                            // Index must be incremented for unavailable videos as well!
                            currentIndex++;
                        }
                    }
                };
                _infoDownloadProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        var template = JsonConvert.DeserializeObject<YoutubeDlOutputTemplate>(e.Data);

                        using (var db = _factory.CreateDbContext(new string[0]))
                        {
                            try
                            {
                                // An error means a video is unavailable and can not be downloaded.
                                var result = new DownloadResult()
                                {
                                    DownloadTask = db.DownloadTask.Find(downloadTaskId),
                                    Url = template.webpage_url,
                                    Name = template.title,
                                    VideoIdentifier = template.id,
                                    Index = currentIndex,
                                    HasError = false,
                                    IsPartOfPlaylist = !string.IsNullOrEmpty(template.playlist)
                                };

                                db.DownloadResult.Add(result);
                                db.DownloadTask.Find(downloadTaskId).DownloadResult.Add(db.DownloadResult.Find(result.Id));
                                db.SaveChanges();

                                // Notify the client about the info. Only do this AFTER the db saved the index of the result!
                                var outputInfo = new YoutubeDlOutputInfo()
                                {
                                    DownloadResultIdentifier = result.Id,
                                    DownloadTaskIdentifier = result.DownloadTask.Id,
                                    Index = result.Index,
                                    VideoIdentifier = result.VideoIdentifier,
                                    Url = result.Url,
                                    Name = result.Name,
                                    IsPartOfPlaylist = result.IsPartOfPlaylist
                                };
                                Task.Run(async () => { await _notification.NotifyClientsAboutReceivedDownloadInformationAsync(outputInfo); });
                            }
                            catch (Exception exception)
                            {
                                _logger.LogError(exception, "Error while adding parsed info process stdOut information to database.");
                            }
                        }

                        // Increment index for further entries.
                        currentIndex++;
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
            catch (Exception e)
            {
                _logger.LogError(e, "Info download process threw an exception:");

                resetEvent.Set();
            }
            finally
            {
                _infoDownloadProcess = null;
            }

            return Task.CompletedTask;
        }
    }
}
