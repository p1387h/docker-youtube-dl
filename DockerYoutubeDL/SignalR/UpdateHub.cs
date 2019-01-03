using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.SignalR
{
    public class UpdateHub : Hub<IUpdateClient>
    {
        private ILogger _logger;
        private UpdateClientContainer _container;

        public UpdateHub(ILogger<UpdateHub> logger, UpdateClientContainer container)
        {
            if (logger == null || container  == null)
            {
                throw new ArgumentException();
            }

            _logger = logger;
            _container = container;
        }

        public override Task OnConnectedAsync()
        {
            var name = Context.User.Identity.Name;
            var identifier = new Guid(name);
            _container.StoredClients[identifier] = Context.ConnectionId;

            _logger.LogDebug($"User {name} connected.");

            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            var name = Context.User.Identity.Name;
            var identifier = new Guid(name);
            _container.StoredClients.Remove(identifier);

            _logger.LogDebug($"User {name} disconnected.");

            return base.OnDisconnectedAsync(exception);
        }
    }
}
