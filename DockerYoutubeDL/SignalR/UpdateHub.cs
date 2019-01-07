using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
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
            _logger.LogInformation($"User {Context.ConnectionId} connected.");

            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogInformation($"User {Context.ConnectionId} disconnected.");

            await base.OnDisconnectedAsync(exception);
        }
    }
}
