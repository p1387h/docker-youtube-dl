﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DockerYoutubeDL.DAL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace DockerYoutubeDL.Pages
{
    public class DownloadModel : PageModel
    {
        private DownloadContext _context;
        private ILogger _logger;

        public DownloadModel(DownloadContext context, ILogger<DownloadModel> logger)
        {
            if (context == null || logger == null)
            {
                throw new ArgumentException();
            }

            _context = context;
            _logger = logger;
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
                    var path = requestedResult.PathToFile;
                    // Crude, hacky way, but it works since the top directory of all downloads of the user 
                    // is named after his as well as the task's guid.
                    var isInUserDir = path.Contains(HttpContext.User.Identity.Name) && path.Contains(taskIdentifier);

                    if(isInUserDir)
                    {
                        var fileName = Path.GetFileName(requestedResult.PathToFile);
                        var fileBytes = System.IO.File.ReadAllBytes(requestedResult.PathToFile);

                        result = File(fileBytes, System.Net.Mime.MediaTypeNames.Application.Octet, fileName);
                    }
                    else
                    {
                        throw new Exception("Requested path is outside of the user's access.");
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
    }
}