using DockerYoutubeDL.DAL;
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
    public class DownloadBackgroundService : BackgroundService
    {
        private int _waitTimeSeconds = 10;
        // Factory is used since the service is instantiated once as singleton and therefore 
        // outlives the "normal" session lifetime of the dbcontext.
        private IDesignTimeDbContextFactory<DownloadContext> _factory;
        private ILogger _logger;

        public DownloadBackgroundService(IDesignTimeDbContextFactory<DownloadContext> factory, ILogger<DownloadBackgroundService> logger)
        {
            if (factory == null || logger == null)
            {
                throw new ArgumentException();
            }

            _factory = factory;
            _logger = logger;
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
                    
                    if(db.DownloadTask.Any())
                    {
                        // Next task is the one that was queued earliest. 
                        var nextTask = db.DownloadTask
                            .Select(x => new Tuple<DownloadTask, TimeSpan>(x, now.Subtract(x.DateAdded)))
                            .OrderByDescending(x => x.Item2)
                            .FirstOrDefault()
                            .Item1;

                        // TODO: Implement download functionality.
                        _logger.LogDebug($"Next download: Downloader={nextTask.Downloader}, Url={nextTask.Url}, Id={nextTask.Id}");

                        hasDownloaded = true;
                    }
                }

                // Don't wait inbetween downloads if more are queued.
                if(!hasDownloaded)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_waitTimeSeconds), stoppingToken);
                }
            }
        }
    }
}
