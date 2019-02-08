using DockerYoutubeDL.DAL;
using Hangfire;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace DockerYoutubeDL.Services
{
    public class HangfireExecutionService
    {
        private DownloadContext _context;
        private ILogger _logger;
        private InfoService _infoService;
        private DownloadService _downloadService;

        public HangfireExecutionService(
            DownloadContext context, 
            ILogger<HangfireExecutionService> logger, 
            InfoService infoService, 
            DownloadService downloadService)
        {
            if(context == null || logger == null || infoService == null || 
                downloadService == null)
            {
                throw new ArgumentException();
            }

            _context = context;
            _logger = logger;
            _infoService = infoService;
            _downloadService = downloadService;
        }

        public async Task QueueBackgroundJob(Guid downloadTaskIdentifier)
        {
            _logger.LogDebug($"Queuing background job information for: {downloadTaskIdentifier}");

            var infoJobId = BackgroundJob.Enqueue(() => this.StartInfoService(JobCancellationToken.Null, downloadTaskIdentifier));
            var downloadJobId = BackgroundJob.ContinueWith(infoJobId, () => this.StartDownloadService(JobCancellationToken.Null, downloadTaskIdentifier));
            var downloadTask = await _context.DownloadTask.FindAsync(downloadTaskIdentifier);

            // Both the info and download job ids must be preserved in the db since 
            // the user is able to stop a download.
            await _context.HangfireInformation.AddAsync(new HangfireInformation()
            {
                JobId = infoJobId,
                DownloadTask = downloadTask,
                HangfireExecutionType = HangfireExecutionType.InfoService
            });
            await _context.HangfireInformation.AddAsync(new HangfireInformation()
            {
                JobId = downloadJobId,
                DownloadTask = downloadTask,
                HangfireExecutionType = HangfireExecutionType.DownloadService

            });
            await _context.SaveChangesAsync();
        }

        public void StartInfoService(IJobCancellationToken token, Guid downloadTaskIdentifier)
        {
            _infoService.ExecuteAsync(token, downloadTaskIdentifier).Wait();
        }

        public void StartDownloadService(IJobCancellationToken token, Guid downloadTaskIdentifier)
        {
            _downloadService.ExecuteAsync(token, downloadTaskIdentifier).Wait();
        }

        public async Task StopAndRemoveBackgroundJob(Guid downloadTaskIdentifier)
        {
            _logger.LogDebug($"Stopping and removing background job information for: {downloadTaskIdentifier}");

            var toRemove = _context.DownloadTask
                .Include(x => x.HangfireInformation)
                .Single(x => x.Id == downloadTaskIdentifier)
                .HangfireInformation;
            var infoJobId = toRemove
                .Single(x => x.HangfireExecutionType == HangfireExecutionType.InfoService)
                .JobId;
            var downloadJobId = toRemove
                .Single(x => x.HangfireExecutionType == HangfireExecutionType.InfoService)
                .JobId;

            // Remove each job from hangfire first to prevent them from being queued again.
            // Download job first, since the download job could otherwise be started as a 
            // continuation.
            BackgroundJob.Delete(downloadJobId);
            BackgroundJob.Delete(infoJobId);

            _context.HangfireInformation.RemoveRange(toRemove);
            await _context.SaveChangesAsync();
        }
    }
}
