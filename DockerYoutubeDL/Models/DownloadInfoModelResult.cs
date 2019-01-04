﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Models
{
    public class DownloadInfoModelResult
    {
        public bool Success { get; set; }
        public Guid TaskIdentifier { get; set; }
        public string Url { get; set; }

        public DownloadInfoModelResult(bool success, Guid downloadTaskId, string url)
        {
            this.Success = success;
            this.TaskIdentifier = downloadTaskId;
            this.Url = url;
        }
    }
}
