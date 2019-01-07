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

        private int _waitTimeBetweenDownloadChecksSeconds = 10;

        // Percentages are 0-100.
        private double _percentageMinDifference = 10;

        private Guid _infoDownloadProcessOwner = new Guid();
        private Guid _mainDownloadProcessOwner = new Guid();
        private Process _infoDownloadProcess = null;
        private Process _mainDownloadProcess = null;
        private bool _wasKilledByDisconnect = false;

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

                        await this.ExecuteDownloadTaskAsync(nextTask.Id);

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

        private async Task ExecuteDownloadTaskAsync(Guid downloadTaskId)
        {
            // ProcessStartInformation that are needed for invoking youtube-dl.
            var infoProcessInfo = await this.GenerateInfoProcessStartInfoAsync(downloadTaskId);
            var mainProcessInfo = await this.GenerateMainProcessStartInfoAsync(downloadTaskId);

            // Download info.
            if (!_wasKilledByDisconnect)
            {
                await this.InfoDownloadProcessAsync(infoProcessInfo, downloadTaskId);

                // Since the Exit event of the Process class can be processed first, a delay is needed 
                // to ensure that all information for the main process are present.
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            // Download selected files.
            if (!_wasKilledByDisconnect)
            {
                await this.MainDownloadProcessAsync(mainProcessInfo, downloadTaskId);
            }

            // Db clean up.
            if (!_wasKilledByDisconnect)
            {
                await this.MarkDownloadTaskAsDownloadedAsync(downloadTaskId);
                await this.MarkPossibleDownloadResultsAsDownloadedAsync(downloadTaskId);
            }

            // Reset any set flag.
            _wasKilledByDisconnect = false;
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
                var maxFileNameLength = _config.GetValue<int>("MaxFileNameLength");

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
                    Path.Combine(downloadFolder, $"%(id)s{_pathGenerator.NameDilimiter}%(title)#0.{maxFileNameLength}s.%(ext)s").ToString(),
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
            // Playlist indices start at 1.
            var currentIndex = 1;
            var resetEvent = new AutoResetEvent(false);

            try
            {
                // Mark the current owner such that the process can be killed.
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    _infoDownloadProcessOwner = (await db.DownloadTask.FindAsync(downloadTaskId)).Downloader;
                }

                _infoDownloadProcess = new Process()
                {
                    StartInfo = processInfo,
                    EnableRaisingEvents = true
                };
                _infoDownloadProcess.Exited += (s, e) =>
                {
                    // Prevent exceptions by only allowing non killed processes to continue.
                    if (!_wasKilledByDisconnect)
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

                                    // Notify the client about the error. Only do this AFTER the db saved the index of the result!
                                    var outputInfo = new YoutubeDlOutputInfo()
                                    {
                                        DownloadResultIdentifier = result.Id,
                                        DownloadTaskIdentifier = result.DownloadTask.Id,
                                        DownloaderIdentifier = result.DownloadTask.Downloader,
                                        Index = result.Index,
                                        HasError = result.HasError,
                                        Message = result.Message,
                                    };
                                    Task.Run(async () => await _notification.NotifyClientAboutFailedDownloadAsync(outputInfo));
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
                                    DownloaderIdentifier = result.DownloadTask.Downloader,
                                    Index = result.Index,
                                    VideoIdentifier = result.VideoIdentifier,
                                    Url = result.Url,
                                    Name = result.Name,
                                    IsPartOfPlaylist = result.IsPartOfPlaylist
                                };
                                Task.Run(async () => { await _notification.NotifyClientAboutReceivedDownloadInformationAsync(outputInfo); });
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
                _infoDownloadProcessOwner = new Guid();
                _infoDownloadProcess = null;
            }
        }

        private async Task MainDownloadProcessAsync(ProcessStartInfo processInfo, Guid downloadTaskId)
        {
            // Playlist indices start at 1.
            var currentIndex = 1;
            var resetEvent = new AutoResetEvent(false);
            string currentDownloadVideoIdentifier = null;
            var prevPercentage = 0.0;

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
                    // Prevent exceptions by only allowing non killed processes to continue.
                    if (!_wasKilledByDisconnect)
                    {
                        // The last download of a playlist does not trigger the notification of the output
                        // (Same goes for a simple download).
                        this.SavePathForDownloadResult(downloadTaskId, currentDownloadVideoIdentifier);
                        this.SendFinishedNotification(downloadTaskId, currentDownloadVideoIdentifier);
                    }

                    resetEvent.Set();
                };
                // Only log the error, since this output includes the error and warning messages of 
                // youtube-dl which are handled in the info gathering process. Therefor sending a
                // downloader error to the user is not advised.
                _mainDownloadProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        _logger.LogError("Main download process threw Error: " + e.Data);
                    }
                };
                _mainDownloadProcess.OutputDataReceived += (s, e) =>
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
                            var prevIndex = currentIndex;
                            currentIndex = Int32.Parse(matchVideoDownloadPlaylist.Groups["currentIndex"].Value);

                            // The next index always indicates that the previous entry was downloaded.
                            if (currentIndex - 1 > 0)
                            {
                                this.SavePathForDownloadResult(downloadTaskId, currentDownloadVideoIdentifier);
                                this.SendFinishedNotification(downloadTaskId, currentDownloadVideoIdentifier);
                            }
                        }
                        else if (matchVideoWebPage.Success)
                        {
                            currentDownloadVideoIdentifier = matchVideoWebPage.Groups["videoIdentifier"].Value;

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
                                        .Single(x => x.DownloadTask.Id == downloadTaskId && x.Index == currentIndex);

                                    if (string.IsNullOrEmpty(result.VideoIdentifier))
                                    {
                                        result.VideoIdentifier = currentDownloadVideoIdentifier;
                                    }

                                    // Skip notifications for unavailable videos.
                                    if (!result.HasError)
                                    {
                                        Task.Run(async () => await _notification.NotifyClientAboutStartedDownloadAsync(downloadTaskId, result.Id));
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
                            if (percentageDouble - prevPercentage >= _percentageMinDifference)
                            {
                                prevPercentage = percentageDouble;

                                using (var db = _factory.CreateDbContext(new string[0]))
                                {
                                    try
                                    {
                                        var result = db.DownloadResult
                                            .Include(x => x.DownloadTask)
                                            .Single(x => x.DownloadTask.Id == downloadTaskId && x.VideoIdentifier != null && x.VideoIdentifier.Equals(currentDownloadVideoIdentifier));

                                        Task.Run(async () => await _notification.NotifyClientAboutDownloadProgressAsync(downloadTaskId, result.Id, percentageDouble));
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
                                        .Single(x => x.DownloadTask.Id == downloadTaskId && x.VideoIdentifier != null && x.VideoIdentifier.Equals(currentDownloadVideoIdentifier));

                                    Task.Run(async () => await _notification.NotifyClientAboutDownloadConversionAsync(downloadTaskId, result.Id));
                                }
                                catch (Exception exception)
                                {
                                    _logger.LogError(exception, "Error while performing conversion notifications based on main process stdOut information.");
                                }
                            }
                        }
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
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Main download process threw an exception:");

                resetEvent.Set();
                await _notification.NotifyClientAboutDownloaderError(downloadTaskId);
            }
            finally
            {
                _mainDownloadProcessOwner = new Guid();
                _mainDownloadProcess = null;
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
                    var downloadFolder = _pathGenerator.GenerateDownloadFolderPath(downloadResult.DownloadTask.Downloader, downloadResult.DownloadTask.Id);

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

        private void SendFinishedNotification(Guid downloadTaskId, string videoIdentifier)
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
                        Task.Run(async () => await _notification.NotifyClientAboutFinishedDownloadAsync(downloadTaskId, result.Id));
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while sending a finished notification for task {downloadTaskId}.");
                }
            }
        }

        private async Task MarkPossibleDownloadResultsAsDownloadedAsync(Guid downloadTaskId)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResults = db.DownloadResult
                        .Include(x => x.DownloadTask)
                        .Where(x => x.DownloadTask.Id == downloadTaskId)
                        .Where(x => !x.HasError)
                        .ToList();
                    downloadResults.ForEach(x => x.WasDownloaded = true);

                    await db.SaveChangesAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while marking the downloadable results of {downloadTaskId} as downloaded.");
                }
            }
        }

        private async Task MarkDownloadTaskAsDownloadedAsync(Guid downloadTaskId)
        {
            try
            {
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    var downloadTask = await db.DownloadTask.FindAsync(downloadTaskId);
                    downloadTask.WasDownloaded = true;
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error while marking {downloadTaskId} as downloaded.");
            }
        }

        public async Task HandleDisconnectAsync(Guid userIdentifier)
        {
            // 8 should be enough since the time inbetween stacks n^n.
            int maxRetryCount = 8;

            _logger.LogDebug($"Handling the disconnect of user {userIdentifier}.");

            // Set the flag indicating that the processes are killed manually.
            _wasKilledByDisconnect = true;

            _logger.LogDebug($"Killing conversion processes.");

            // Kill all conversion processes (avconv, ffmpeg) that might block the deletion 
            // of files.
            Process.GetProcessesByName("ffmpeg").ToList().ForEach(x => x.Kill());
            Process.GetProcessesByName("avconv").ToList().ForEach(x => x.Kill());

            _logger.LogDebug($"Killing processes of user {userIdentifier}.");

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
            // The db entries must be reset first in order to prevent the main download from
            // queuing another one of them.
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
                            .Include(x => x.DownloadTask)
                            .Where(x => x.DownloadTask.Downloader == userIdentifier);
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

                        if (Directory.Exists(folderPath))
                        {
                            Directory.Delete(folderPath, true);
                        }

                        _logger.LogDebug($"Successfully removed files of user {userIdentifier}");
                    }
                    catch (IOException)
                    {
                        return Task.FromResult<bool>(false);
                    }
                    return Task.FromResult<bool>(true);
                });
        }
    }
}

