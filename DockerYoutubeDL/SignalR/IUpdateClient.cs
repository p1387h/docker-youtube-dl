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
        // Send from info process.
        Task ReceivedDownloadInfo(YoutubeDlOutputInfo outputInfo);
        Task DownloadFailed(YoutubeDlOutputInfo outputInfo);

        // Send from main process.
        Task DownloadStarted(Guid taskIdentifier, Guid taskResultIdentifier);
        Task DownloadProgress(Guid taskIdentifier, Guid taskResultIdentifier, double percentage);
        Task DownloadConversion(Guid taskIdentifier, Guid taskResultIdentifier);
        Task DownloadResultFinished(Guid taskIdentifier, Guid taskResultIdentifier);
        Task DownloadTaskFinished(Guid taskIdentifier);
        Task DownloadInterrupted(Guid taskIdentifier);
        // Error in the main download process. Causes a whole download task to fail.
        Task DownloaderError(Guid taskIdentifier);
    }
}
