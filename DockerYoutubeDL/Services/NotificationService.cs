using DockerYoutubeDL.DAL;
using DockerYoutubeDL.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Services
{
    public class NotificationService
    {
        // Factory is used since the service is instantiated once as singleton and therefor 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IHubContext<UpdateHub> _hub;
        private UpdateClientContainer _container;
        private DownloadPathGenerator _pathGenerator;

        private Policy _notificationPolicy;

        public NotificationService(
            IDesignTimeDbContextFactory<DownloadContext> factory, 
            ILogger<NotificationService> logger, 
            IHubContext<UpdateHub> hub, 
            UpdateClientContainer container, 
            DownloadPathGenerator pathGenerator)
        {
            if(factory == null || logger == null || hub == null ||
                container == null || pathGenerator == null)
            {
                throw new ArgumentException();
            }

            _factory = factory;
            _logger = logger;
            _hub = hub;
            _container = container;
            _pathGenerator = pathGenerator;

            // The same policy is used for all notification attempts.
            _notificationPolicy = Policy.Handle<Exception>()
                .WaitAndRetryAsync(5, (count) => TimeSpan.FromSeconds(count + 1), (e, retryCount, context) =>
                {
                    _logger.LogError(e, "Polly retry error: " + context["errorMessage"] as string);
                });
        }

        public async Task NotifyClientAboutReceivedDownloadInformationAsync(DownloadResult downloadResult)
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

        public async Task NotifyClientAboutStartedDownloadAsync(Guid downloadTaskId, string videoIdentifier)
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

        public async Task NotifyClientAboutFailedDownloadAsync(Guid downloadTaskId)
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

        public async Task NotifyClientAboutFinishedDownloadAsync(Guid downloadTaskId, string videoIdentifier)
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
                        var fileVideoIdentifier = Path.GetFileNameWithoutExtension(filePath).Split(_pathGenerator.NameDilimiter)[0];

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

        public async Task NotifyClientAboutDownloadProgressAsync(Guid downloadTaskId, string videoIdentifier, double percentage)
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

        public async Task NotifyClientAboutDownloadConversionAsync(Guid downloadTaskId, string videoIdentifier)
        {
            using (var db = _factory.CreateDbContext(new string[0]))
            {
                try
                {
                    var downloadResult = db.DownloadResult.First(x => x.IdentifierDownloadTask == downloadTaskId && x.VideoIdentifier == videoIdentifier);
                    var client = _hub.Clients.Client(_container.StoredClients[downloadResult.IdentifierDownloader]);

                    _logger.LogDebug($"Notifying client about conversion with id={downloadResult.Id}.");

                    await _notificationPolicy.ExecuteAsync(
                        (context) => client.SendAsync(nameof(IUpdateClient.DownloadConversion), downloadResult.IdentifierDownloadTask, downloadResult.Id),
                        new Dictionary<string, object>()
                        {
                            { "errorMessage", $"Error while notifying user {downloadResult.IdentifierDownloader} about the conversion of {videoIdentifier}." }
                        }
                    );
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error while notifying the user of {downloadTaskId} about the conversion of {videoIdentifier}.");
                }
            }
        }
    }
}
