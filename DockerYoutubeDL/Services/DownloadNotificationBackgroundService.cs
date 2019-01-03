using DockerYoutubeDL.DAL;
using DockerYoutubeDL.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Services
{
    public class DownloadNotificationBackgroundService : BackgroundService
    {
        private int _waitTimeSeconds = 3;
        // Factory is used since the service is instantiated once as singleton and therefor 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;
        private IHubContext<UpdateHub> _hub;
        private UpdateClientContainer _container;

        public DownloadNotificationBackgroundService(
            IDesignTimeDbContextFactory<DownloadContext> factory, 
            ILogger<DownloadBackgroundService> logger,
            IHubContext<UpdateHub> hub,
            UpdateClientContainer container)
        {
            if (factory == null || logger == null || hub == null || container == null)
            {
                throw new ArgumentException();
            }

            _factory = factory;
            _logger = logger;
            _hub = hub;
            _container = container;
        }

        protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogDebug("DownloadNotificationBackgroundService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                // Find any finished downloads and notify the downloader that it finished.
                using (var db = _factory.CreateDbContext(new string[0]))
                {
                    _logger.LogDebug("Checking for pending notification...");

                    var toNotify = db.DownloadResult.Where(x => !x.WasNotified);

                    if (toNotify.Any())
                    {
                        var connectionInfos = toNotify
                            .Select(x => new Tuple<Guid, string>(x.Id, _container.StoredClients[x.DownloadTask.Downloader]));

                        // Update each found client respectively with the new information.
                        foreach (var info in connectionInfos)
                        {
                            var taskResultIdentifier = info.Item1;
                            var connectionId = info.Item2;
                            var client = _hub.Clients.Client(connectionId);

                            var taskIdentifier = (await db.DownloadResult.FindAsync(taskResultIdentifier)).DownloadTask.Id;

                            await client.SendAsync(nameof(IUpdateClient.DownloadFinished), taskIdentifier, taskResultIdentifier, stoppingToken);
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(_waitTimeSeconds), stoppingToken);
            }
        }
    }
}
