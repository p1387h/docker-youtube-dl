﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using DockerYoutubeDL.Services;
using Polly;
using System.IO;

namespace DockerYoutubeDL.Pages
{
    public class IndexModel : PageModel
    {
        public List<DownloadTask> DownloadTasks { get; set; }
        public Guid RecentlyAddedTaskIdentifier { get; set; }

        // Quality of life features. Users don't have to select same setting again:
        [BindProperty(SupportsGet = true)]
        public AudioFormat SelectedAudioFormat { get; set; }
        [BindProperty(SupportsGet = true)]
        public VideoFormat SelectedVideoFormat { get; set; }
        [BindProperty(SupportsGet = true)]
        public VideoQuality SelectedVideoQuality { get; set; }

        private DownloadContext _context;
        private ILogger _logger;
        private HangfireExecutionService _hangfireService;
        private NotificationService _notification;
        private DownloadPathGenerator _pathGenerator;

        public IndexModel(
            DownloadContext context,
            ILogger<IndexModel> logger,
            HangfireExecutionService hangfireService,
            NotificationService notification,
            DownloadPathGenerator pathGenerator)
        {
            if (context == null || logger == null || hangfireService == null || 
                notification == null || pathGenerator == null)
            {
                throw new ArgumentException();
            }

            _context = context;
            _logger = logger;
            _hangfireService = hangfireService;
            _notification = notification;
            _pathGenerator = pathGenerator;
        }

        public void OnGet()
        {
            this.LoadDownloadTasks();

            // Initial loading of the page.
            if (this.SelectedAudioFormat == AudioFormat.None && this.SelectedVideoFormat == VideoFormat.None)
            {
                this.SelectedAudioFormat = AudioFormat.None;
                this.SelectedVideoFormat = VideoFormat.Mp4;
                this.SelectedVideoQuality = VideoQuality.BestOverall;
            }
        }

        public async Task OnPostDeleteTask([FromForm] Guid removeTaskId)
        {
            if (ModelState.IsValid)
            {
                await this.DeleteTask(removeTaskId);
            }
            // Invalid ModelState means, that no task id was provided. Therefore all 
            // download tasks must be deleted.
            else
            {
                foreach (var id in _context.DownloadTask.Select(x => x.Id))
                {
                    await this.DeleteTask(id);
                }
            }

            this.LoadDownloadTasks();
        }

        private async Task DeleteTask(Guid downloadTaskId)
        {
            var storedTask = await _context.DownloadTask.FindAsync(downloadTaskId);

            if (storedTask != null)
            {
                var retryCount = 8;

                _logger.LogDebug($"Forwarding interrupt.");

                // Remove any traces from hangfire.
                await _hangfireService.StopAndRemoveBackgroundJob(downloadTaskId);

                // Db cleanup.
                await this.RemoveDbEntriesAsync(downloadTaskId);

                // Notify all clients.
                await _notification.NotifyClientsAboutInterruptedDownloadAsync(downloadTaskId);

                // After killing all processes etc. remove the directory. Started in own task 
                // since polly is used for removing the files, which could lead to timeouts.
#pragma warning disable CS4014
                Task.Run(async () => await this.DeleteDownloadFilesAsync(downloadTaskId, retryCount));
            }
        }

        public async Task OnPostNewTask([FromForm] DownloadInfoModel downloadInfo)
        {
            // ModelState must be valid (= url etc.) for the task to be saved in the db.
            if (ModelState.IsValid)
            {
                var downloadTask = new DownloadTask()
                {
                    Url = downloadInfo.Url,
                    DateAdded = DateTime.Now,
                    AudioFormat = downloadInfo.AudioFormat,
                    VideoFormat = downloadInfo.VideoFormat,
                    VideoQuality = downloadInfo.VideoQuality
                };

                await _context.DownloadTask.AddAsync(downloadTask);
                await _context.SaveChangesAsync();

                // Forward info to hangfire.
                await _hangfireService.QueueBackgroundJob(downloadTask.Id);

                _logger.LogInformation($"New DownloadTask added to the db: Url={downloadTask.Url}, Id={downloadTask.Id}");

                // Prepare the view.
                this.RecentlyAddedTaskIdentifier = downloadTask.Id;
                this.UpdateLastSelectedValues(downloadInfo);
            }

            this.LoadDownloadTasks();
        }

        private void LoadDownloadTasks()
        {
            var now = DateTime.Now;

            // Latest entries are the first ones in the list.
            this.DownloadTasks = _context.DownloadTask
                .Include(x => x.DownloadResult)
                .OrderBy(x => now.Subtract(x.DateAdded))
                .ToList();
        }

        private void UpdateLastSelectedValues(DownloadInfoModel downloadInfo)
        {
            // Fix for hidden fields not correctly updating themselves. Without removing the 
            // existing key before assigning the new values, the "Html.HiddenFor()"-helper is 
            // prioritising the previously send POST data.
            ModelState.Remove(nameof(this.SelectedAudioFormat));
            ModelState.Remove(nameof(this.SelectedVideoFormat));
            ModelState.Remove(nameof(this.SelectedVideoQuality));

            // Video chosen.
            if (downloadInfo.AudioFormat == AudioFormat.None)
            {
                this.SelectedAudioFormat = AudioFormat.None;
                this.SelectedVideoFormat = downloadInfo.VideoFormat;
                this.SelectedVideoQuality = downloadInfo.VideoQuality;
            }
            // Audio chosen.
            else if (downloadInfo.VideoFormat == VideoFormat.None)
            {
                this.SelectedAudioFormat = downloadInfo.AudioFormat;
                this.SelectedVideoFormat = VideoFormat.None;
                this.SelectedVideoQuality = downloadInfo.VideoQuality;
            }
        }

        private async Task RemoveDbEntriesAsync(Guid downloadTaskId)
        {
            var downloadTask = await _context.DownloadTask.FindAsync(downloadTaskId);

            // Prevent Exceptions.
            if (downloadTask != null)
            {
                var results = _context.DownloadResult
                    .Include(x => x.DownloadTask)
                    .Where(x => x.DownloadTask.Id == downloadTaskId);

                _context.DownloadResult.RemoveRange(results);
                _context.DownloadTask.Remove(downloadTask);

                await _context.SaveChangesAsync();
            }
        }

        private async Task DeleteDownloadFilesAsync(Guid downloadTaskId, int maxRetryCount)
        {
            // Remove any folders / downloads of the task.
            await Policy
                .HandleResult<bool>((deleted) => !deleted)
                .WaitAndRetryAsync(maxRetryCount, (retry) => TimeSpan.FromSeconds(retry * retry), (result, waitTime, retry, context) =>
                {
                    if (retry == maxRetryCount)
                    {
                        _logger.LogCritical($"Polly was not able to remove the directory of task {downloadTaskId} after the maximum number of retries.");
                    }
                    else
                    {
                        _logger.LogError($"Error while removing the directory of task {downloadTaskId}:");
                    }
                }).ExecuteAsync(() =>
                {
                    _logger.LogDebug($"Trying to remove the directory of task {downloadTaskId}.");

                    // IOExceptions are not correctly handled by Polly. Therefore the result is used.
                    try
                    {
                        var folderPath = _pathGenerator.GenerateDownloadFolderPath(downloadTaskId);

                        if (Directory.Exists(folderPath))
                        {
                            Directory.Delete(folderPath, true);
                        }

                        _logger.LogDebug($"Successfully removed the directory of task {downloadTaskId}");
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
