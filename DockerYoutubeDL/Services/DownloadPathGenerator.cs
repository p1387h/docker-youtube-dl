using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Services
{
    public class DownloadPathGenerator
    {
        private IConfiguration _config;

        public string NameDilimiter { get; private set; }

        public DownloadPathGenerator(IConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentException();
            }

            _config = config;

            this.NameDilimiter = "-----------";
        }

        public string GenerateDownloadFolderPath()
        {
            var downloadRootFolder = _config.GetValue<string>("DownloadRootFolder");
            var downloadFolderPath = Path.Combine(downloadRootFolder);

            return downloadFolderPath;
        }

        public string GenerateDownloadFolderPath(Guid downloadTaskIdentifier)
        {
            var downloadFolderPath = Path.Combine(
                this.GenerateDownloadFolderPath(), 
                downloadTaskIdentifier.ToString()
            );

            return downloadFolderPath;
        }
    }
}
