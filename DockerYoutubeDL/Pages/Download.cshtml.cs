using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

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
                    // Whole playlist.
                    if(string.IsNullOrEmpty(taskResultIdentifier))
                    {
                        result = await this.DownloadWholePlaylist(taskIdentifier);
                    }
                    // Single entry.
                    else
                    {
                        result = await this.DownloadSingleEntry(taskResultIdentifier);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error while requesting a file:");
                    result = BadRequest();
                }
            }

            return result;
        }

        private async Task<ActionResult> DownloadWholePlaylist(string taskIdentifier)
        {
            var requestedTask = _context.DownloadTask
                .Include(x => x.DownloadResult)
                .Single(x => x.Id == new Guid(taskIdentifier) && x.WasDownloaded);
            var results = requestedTask.DownloadResult
                .Where(x => x.WasDownloaded && !x.HasError);
            byte[] fileBytes;

            // Create new zip file with all the downloaded results inside it.
            using (var archiveMs = new MemoryStream())
            {
                using (var archive = new ZipArchive(archiveMs, ZipArchiveMode.Create, true))
                {
                    foreach (var result in results)
                    {
                        var fileName = this.PadFileName(result, result.Name + Path.GetExtension(result.PathToFile));
                        var entry = archive.CreateEntry(fileName);

                        using (var fileStream = System.IO.File.OpenRead(result.PathToFile))
                        {
                            using (var entryStream = entry.Open())
                            {
                                await fileStream.CopyToAsync(entryStream);
                            }
                        }
                    }
                }
                fileBytes = archiveMs.ToArray();
            }

            return File(fileBytes, System.Net.Mime.MediaTypeNames.Application.Zip, "playlist.zip");
        }

        private async Task<ActionResult> DownloadSingleEntry(string taskResultIdentifier)
        {
            var requestedResult = await _context.DownloadResult.FindAsync(new Guid(taskResultIdentifier));
            var fileBytes = System.IO.File.ReadAllBytes(requestedResult.PathToFile);
            var fileFormat = Path.GetExtension(requestedResult.PathToFile);
            var fileName = requestedResult.Name + fileFormat;

            // Playlist indices must be added infront of files.
            if (requestedResult.IsPartOfPlaylist)
            {
                fileName = this.PadFileName(requestedResult, fileName);
            }

            return File(fileBytes, System.Net.Mime.MediaTypeNames.Application.Octet, fileName);
        }

        private string PadFileName(DownloadResult result, string fileName)
        {
            // Pad the left side of the index with as many leading zeroes as needed.
            // I.e.:
            // 0-9:     0_XXXXX
            // 0-99:    00_XXXXX
            var neededZeroes = (int)Math.Ceiling(Math.Log(result.Index, 10));
            fileName = result.Index.ToString().PadLeft(neededZeroes, '0') + "_" + fileName;

            return fileName;
        }
    }
}