using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadTask
    {
        public Guid Id { get; set; }
        public string Url { get; set; }
        public Guid Downloader { get; set; }
        public DateTime DateAdded { get; set; }
    }
}
