using DockerYoutubeDL.DAL;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Models
{
    public class DownloadInfoModel
    {
        [Url]
        [Required]
        public string Url { get; set; }

        public AudioFormat AudioFormat { get; set; }

        public VideoFormat VideoFormat { get; set; }
    }
}
