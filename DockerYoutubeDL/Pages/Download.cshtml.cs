using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace DockerYoutubeDL.Pages
{
    public class DownloadModel : PageModel
    {
        private DownloadContext _context;
        private ILogger _logger;
        private DownloadPathGenerator _pathGenerator;

        public DownloadModel(DownloadContext context, ILogger<DownloadModel> logger, DownloadPathGenerator pathGenerator)
        {
            if (context == null || logger == null || pathGenerator == null)
            {
                throw new ArgumentException();
            }

            _context = context;
            _logger = logger;
            _pathGenerator = pathGenerator;
        }

        public async Task<ActionResult> OnGet([FromQuery] string taskIdentifier, [FromQuery] string taskResultIdentifier)
        {
            ActionResult result;

            if (string.IsNullOrEmpty(taskIdentifier))
            {
                result = BadRequest();
            }
            else
            {
                try
                {
                    var requestedResult = await _context.DownloadResult.FindAsync(new Guid(taskResultIdentifier));
                    var fileBytes = System.IO.File.ReadAllBytes(requestedResult.PathToFile);
                    var fileFormat = Path.GetExtension(requestedResult.PathToFile);
                    var fileName = requestedResult.Name + fileFormat;

                    // Playlist indices must be added infront of files.
                    if(requestedResult.IsPartOfPlaylist)
                    {
                        // Pad the left side of the index with as many leading zeroes as needed.
                        var neededZeroes = (int)Math.Ceiling(Math.Log(requestedResult.Index, 10));
                        fileName = requestedResult.Index.ToString().PadLeft(neededZeroes, '0') + "_" + fileName;
                    }

                    result = File(fileBytes, System.Net.Mime.MediaTypeNames.Application.Octet, fileName);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error while requesting a file:");
                    result = BadRequest();
                }
            }

            return result;
        }
    }
}