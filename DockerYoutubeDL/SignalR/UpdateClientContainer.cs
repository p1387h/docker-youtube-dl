using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.SignalR
{
    public class UpdateClientContainer
    {
        // Key: User-Identifier
        // Value: ConnectionId
        public Dictionary<Guid, string> StoredClients { get; set; }

        public UpdateClientContainer()
        {
            this.StoredClients = new Dictionary<Guid, string>();
        }
    }
}
