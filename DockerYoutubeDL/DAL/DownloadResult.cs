using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadResult
    {
        public Guid Id { get; set; }
        public DateTime DateDownload { get; set; }
        public bool WasDownloaded { get; set; }
        public string PathToFile { get; set; }

        public DownloadTask DownloadTask { get; set; }
    }
}
