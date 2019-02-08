using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    // The type of service that is enqueued.
    public enum HangfireExecutionType
    {
        InfoService,
        DownloadService
    }
}
