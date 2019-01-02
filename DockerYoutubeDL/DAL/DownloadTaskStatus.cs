using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public enum DownloadTaskStatus
    {
        Waiting = 0,
        BeingWorkedOn = 1,
        Finished = 2
    }
}
