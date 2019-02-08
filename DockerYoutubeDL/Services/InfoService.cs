using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Models;
using Hangfire;
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
    public class InfoService
    {
        // Factory is used since the service modifies multiple different instances.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IConfiguration _config;
        private DownloadPathGenerator _pathGenerator;
        private NotificationService _notification;

        // Shared info between handlers:
        private int _currentIndex;
        private AutoResetEvent _resetEvent;

        private Guid _downloadTaskId = new Guid();
        private Process _infoDownloadProcess = null;
        private bool _wasKilledByInterrupt = false;

        public InfoService(
            IDesignTimeDbContextFactory<DownloadContext> factory,
            ILogger<DownloadService> logger,
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

        public async Task ExecuteAsync(IJobCancellationToken token, Guid downloadTaskIdentifier)
        {
            _logger.LogDebug($"InfoService started for: {downloadTaskIdentifier}");

            _downloadTaskId = downloadTaskIdentifier;

            var infoProcessInfo = await this.GenerateInfoProcessStartInfoAsync();
            var infoProcessTask = this.InfoDownloadProcessAsync(infoProcessInfo);
            var desiredState = new HashSet<TaskStatus>()
                {
                    TaskStatus.Canceled,
                    TaskStatus.Faulted,
                    TaskStatus.RanToCompletion
                };
            var running = true;

            try
            {
                // Keep an eye on the cancellation token in order to remove any tasks that are 
                // removed by the user until the task finishes.
                while (running)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    running = !desiredState.Contains(infoProcessTask.Status);
                }
            }
            catch (OperationCanceledException)
            {
                this.HandleDownloadInterrupt();
                _logger.LogInformation("Running Hangfire background job stopped: Info");
            }

            await infoProcessTask;
        }

        private async Task<ProcessStartInfo> GenerateInfoProcessStartInfoAsync()
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var downloadTask = await db.DownloadTask.FindAsync(_downloadTaskId);

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

        private async Task InfoDownloadProcessAsync(ProcessStartInfo processInfo)
        {
            // Playlist indices start at 1.
            _currentIndex = 1;
            _resetEvent = new AutoResetEvent(false);

            try
            {
                _infoDownloadProcess = new Process()
                {
                    StartInfo = processInfo,
                    EnableRaisingEvents = true
                };

                _infoDownloadProcess.Exited += this.HandleExited;
                _infoDownloadProcess.ErrorDataReceived += this.HandleError;
                _infoDownloadProcess.OutputDataReceived += this.HandleOutput;

                _infoDownloadProcess.Start();

                // Enables the asynchronus callback functions to receive the redirected data.
                _infoDownloadProcess.BeginOutputReadLine();
                _infoDownloadProcess.BeginErrorReadLine();

                // Wait for the process to finish.
                _logger.LogDebug("Waiting for the info download process to finish...");
                _resetEvent.WaitOne();

                // Diable the asynchronus callback functions.
                _infoDownloadProcess.CancelOutputRead();
                _infoDownloadProcess.CancelErrorRead();

                _logger.LogDebug($"Finished downloading information.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Info download process threw an exception:");

                _resetEvent.Set();
                this.MarkDownloadTaskAsDownloaderError(_downloadTaskId);
                await _notification.NotifyClientsAboutDownloaderError(_downloadTaskId);
            }
            finally
            {
                // Prevents null pointer exceptions when killing the process.
                _downloadTaskId = new Guid();

                // Remove all handlers etc. to prevent memory leaks.
                _infoDownloadProcess.Exited -= this.HandleExited;
                _infoDownloadProcess.ErrorDataReceived -= this.HandleError;
                _infoDownloadProcess.OutputDataReceived -= this.HandleOutput;
                _infoDownloadProcess.Dispose();
                _infoDownloadProcess = null;
            }
        }

        private void HandleExited(object sender, EventArgs e)
        {
            // Prevent exceptions by only allowing non killed processes to continue.
            if (!_wasKilledByInterrupt)
            {
                // Unavailable videos do not return the name of their playlist, thus not setting the 
                // flag correctly. This ensures that all flags are set accordingly.
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    var downloadResults = db.DownloadResult
                        .Include(x => x.DownloadTask)
                        .Where(x => x.DownloadTask.Id == _downloadTaskId)
                        .ToList();
                    downloadResults.ForEach(x => x.IsPartOfPlaylist = downloadResults.Count > 1);

                    db.SaveChanges();
                }
            }

            _resetEvent.Set();
        }

        private void HandleError(object sender, DataReceivedEventArgs e)
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
                                DownloadTask = db.DownloadTask.Find(_downloadTaskId),
                                Index = _currentIndex,
                                HasError = true,
                                Message = message
                            };

                            db.DownloadResult.Add(result);
                            db.DownloadTask.Find(_downloadTaskId).DownloadResult.Add(result);
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
                    _currentIndex++;
                }
            }
        }

        private void HandleOutput(object sender, DataReceivedEventArgs e)
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
                            DownloadTask = db.DownloadTask.Find(_downloadTaskId),
                            Url = template.webpage_url,
                            Name = template.title,
                            VideoIdentifier = template.id,
                            Index = _currentIndex,
                            HasError = false,
                            IsPartOfPlaylist = !string.IsNullOrEmpty(template.playlist)
                        };

                        db.DownloadResult.Add(result);
                        db.DownloadTask.Find(_downloadTaskId).DownloadResult.Add(db.DownloadResult.Find(result.Id));
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
                _currentIndex++;
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

        public void HandleDownloadInterrupt()
        {
            // Set the flag indicating that the process was killed manually.
            _wasKilledByInterrupt = true;

            _logger.LogDebug($"Killing info process.");

            _infoDownloadProcess.Kill();
        }
    }
}
