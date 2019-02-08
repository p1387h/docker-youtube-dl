using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class HangfireInformation
    {
        public Guid Id { get; set; }
        public string JobId { get; set; }
        public HangfireExecutionType HangfireExecutionType { get; set; }

        public DownloadTask DownloadTask { get; set; }
    }
}
