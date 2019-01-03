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
                _logger.LogDebug($"Deleting entries of user {name}");

                await this.DeleteDownloadEntriesAsync(identifier);
            }

            await base.OnDisconnectedAsync(exception);
        }

        private async Task DeleteDownloadEntriesAsync(Guid identifier)
        {
            // Remove any folders / downloads of the user.
            var folderPath = _pathGenerator.GenerateDownloadFolderPath(identifier);
            Directory.Delete(folderPath, true);

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
