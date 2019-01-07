using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Models
{
    public class YoutubeDlOutputInfo
    {
        public Guid DownloadTaskIdentifier { get; set; }
        public Guid DownloadResultIdentifier { get; set; }
        public Guid DownloaderIdentifier { get; set; }
        public int Index { get; set; }

        public string VideoIdentifier { get; set; }
        public string Url { get; set; }
        public string Name { get; set; }
        public bool IsPartOfPlaylist { get; set; }

        public string Message { get; set; }
        public bool HasError { get; set; }
    }
}
