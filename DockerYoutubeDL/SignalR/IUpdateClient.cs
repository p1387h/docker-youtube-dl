using DockerYoutubeDL.DAL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.SignalR
{
    public interface IUpdateClient
    {
        // Id of the task (received when it got queued up) and id of the result corresponding to the task.

        Task ReceivedDownloadInfo(DownloadResult downloadResult);

        Task DownloadStarted(Guid taskIdentifier, Guid taskResultIdentifier);

        Task DownloadProgress(Guid taskIdentifier, Guid taskResultIdentifier, double percentage);

        Task DownloadConversion(Guid taskIdentifier, Guid taskResultIdentifier);
        
        Task DownloadFinished(Guid taskIdentifier, Guid taskResultIdentifier);

        Task DownloadFailed(Guid taskIdentifier);

        // Problem (error or warning) in youtubedl. Causes the download to fail.
        Task DownloadProblem(Guid taskIdentifier, string message);

        // Error in the main download process. Causes a whole download task to fail.
        Task DownloaderError(Guid taskIdentifier);

        // Test if a client disconnected or just downloaded a file.
        Task Ping();
    }
}
