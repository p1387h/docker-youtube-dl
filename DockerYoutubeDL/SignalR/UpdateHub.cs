using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.SignalR
{
    public class UpdateHub : Hub<IUpdateClient>
    {
        private ILogger _logger;
        private UpdateClientContainer _container;
        private DownloadPathGenerator _pathGenerator;
        private IConfiguration _config;
        private DownloadContext _context;

        private int _waitTimeReconnectSeconds = 10;

        public UpdateHub(
            ILogger<UpdateHub> logger, 
            UpdateClientContainer container, 
            DownloadPathGenerator pathGenerator, 
            IConfiguration config,
            DownloadContext context)
        {
            if (logger == null || container  == null || pathGenerator == null || 
                config == null || context == null)
            {
                throw new ArgumentException();
            }

            _logger = logger;
            _container = container;
            _pathGenerator = pathGenerator;
            _config = config;
            _context = context;
        }

        public override Task OnConnectedAsync()
        {
            var name = Context.User.Identity.Name;
            var identifier = new Guid(name);
            _container.StoredClients[identifier] = Context.ConnectionId;

            _logger.LogDebug($"User {name} connected.");

            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var name = Context.User.Identity.Name;
            var identifier = new Guid(name);
            _container.StoredClients.Remove(identifier);

            _logger.LogDebug($"User {name} disconnected.");

            // Enable deletion of downloads if the user's websocket connection fails.
            // Ensures that the server frees memory and does not keep allocating space.
            if(_config.GetValue<bool>("DeleteDownloadsOnDisconnect"))
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogDebug($"Giving user {name} time to reconnect...");

                        await Task.Delay(TimeSpan.FromSeconds(_waitTimeReconnectSeconds));
                        await Clients.Client(_container.StoredClients[identifier]).Ping();
                    } catch(Exception e)
                    {
                        _logger.LogDebug($"User {name} failed to reconnect.");

                        await this.DeleteDownloadEntriesAsync(identifier);
                    }
                });
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task Pong()
        {
            _logger.LogDebug($"User {Context.User.Identity.Name} replied to the ping.");
        }

        private async Task DeleteDownloadEntriesAsync(Guid identifier)
        {
            _logger.LogDebug($"Deleting entries of user {identifier}.");

            // Remove any folders / downloads of the user.
            try
            {
                var folderPath = _pathGenerator.GenerateDownloadFolderPath(identifier);
                Directory.Delete(folderPath, true);
            } catch(IOException e)
            {
                _logger.LogError(e, $"Error while removing downloads of the user {identifier}:");
            }

            // Entries of the user must be removed from the db.
            var toDeleteTasks = _context.DownloadTask
                .Where(x => x.Downloader == identifier);
            var toDeleteResults = _context.DownloadResult
                .Where(x => x.IdentifierDownloader == identifier);
            _context.DownloadTask.RemoveRange(toDeleteTasks);
            _context.DownloadResult.RemoveRange(toDeleteResults);

            await _context.SaveChangesAsync();
        }
    }
}
