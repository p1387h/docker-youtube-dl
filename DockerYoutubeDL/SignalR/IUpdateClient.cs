using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.SignalR
{
    public interface IUpdateClient
    {
        // Id of the task (received when it got queued up) and id of the result corresponding to the task.

        // Send from info process.
        Task ReceivedDownloadInfo(YoutubeDlOutputInfo outputInfo);
        Task DownloadFailed(YoutubeDlOutputInfo outputInfo);

        // Send from main process.
        Task DownloadStarted(Guid taskIdentifier, Guid taskResultIdentifier);
        Task DownloadProgress(Guid taskIdentifier, Guid taskResultIdentifier, double percentage);
        Task DownloadConversion(Guid taskIdentifier, Guid taskResultIdentifier);
        Task DownloadFinished(Guid taskIdentifier, Guid taskResultIdentifier);
        // Error in the main download process. Causes a whole download task to fail.
        Task DownloaderError(Guid taskIdentifier);

        // Test if a client disconnected or just downloaded a file.
        Task Ping();
    }
}
