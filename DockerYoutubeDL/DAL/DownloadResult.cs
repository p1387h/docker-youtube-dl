using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadResult
    {
        public Guid Id { get; set; }
        public string PathToFile { get; set; }

        public Guid IdentifierDownloader { get; set; }
        public Guid IdentifierDownloadTask { get; set; }
    }
}
