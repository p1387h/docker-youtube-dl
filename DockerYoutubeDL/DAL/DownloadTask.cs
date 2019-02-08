using DockerYoutubeDL.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadTask
    {
        public Guid Id { get; set; }
        public string Url { get; set; }
        public DateTime DateAdded { get; set; }
        public AudioFormat AudioFormat { get; set; }
        public VideoFormat VideoFormat { get; set; }
        public VideoQuality VideoQuality { get; set; }

        // Flags indicating the state of the task.
        public bool WasDownloaded { get; set; }
        public bool HadDownloaderError { get; set; }

        public ICollection<DownloadResult> DownloadResult { get; set; }

        // Reference to scheduled background jobs.
        public ICollection<HangfireInformation> HangfireInformation { get; set; }

        public DownloadTask()
        {
            this.DownloadResult = new Collection<DownloadResult>();
            this.HangfireInformation = new Collection<HangfireInformation>();
        }
    }
}
