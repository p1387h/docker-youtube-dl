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

        public UpdateHub(ILogger<UpdateHub> logger)
        {
            if (logger == null)
            {
                throw new ArgumentException();
            }

            _logger = logger;
        }

        public override Task OnConnectedAsync()
        {
            _logger.LogDebug($"User {Context.User.Identity.Name} connected.");

            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogDebug($"User {Context.User.Identity.Name} disconnected.");

            return base.OnDisconnectedAsync(exception);
        }
    }
}
