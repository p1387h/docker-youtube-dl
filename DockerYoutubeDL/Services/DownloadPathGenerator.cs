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

        public DownloadPathGenerator(IConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentException();
            }

            _config = config;
        }

        public string GenerateDownloadFolderPath(Guid downloaderIdentifier)
        {
            var downloadRootFolder = _config.GetValue<string>("DownloadRootFolder");
            var downloadFolderPath = Path.Combine(
                downloadRootFolder,
                downloaderIdentifier.ToString()
            );

            return downloadFolderPath;
        }

        public string GenerateDownloadFolderPath(Guid downloaderIdentifier, Guid downloadTaskIdentifier)
        {
            var downloadFolderPath = Path.Combine(
                this.GenerateDownloadFolderPath(downloaderIdentifier), 
                downloadTaskIdentifier.ToString()
            );

            return downloadFolderPath;
        }
    }
}
