﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadResult
    {
        public Guid Id { get; set; }
        public string PathToFile { get; set; }
        public bool WasDownloaded { get; set; }
        public int Index { get; set; }

        public string VideoIdentifier { get; set; }
        public string Url { get; set; }
        public string Name { get; set; }
        public bool IsPartOfPlaylist { get; set; }

        public string Message { get; set; }
        public bool HasError { get; set; }

        public DownloadTask DownloadTask { get; set; }
    }
}
