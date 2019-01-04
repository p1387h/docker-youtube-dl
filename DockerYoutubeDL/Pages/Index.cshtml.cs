using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DockerYoutubeDL.DAL;
using DockerYoutubeDL.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace DockerYoutubeDL.Pages
{
    // Order 1001 since the basic check for antiforgery tokens has the order 1000.
    [IgnoreAntiforgeryToken(Order = 1001)]
    public class IndexModel : PageModel
    {
        public string Identifier { get; set; }

        private DownloadContext _context;
        private ILogger _logger;

        public IndexModel(DownloadContext context, ILogger<IndexModel> logger)
        {
            if (context == null || logger == null)
            {
                throw new ArgumentException();
            }

            _context = context;
            _logger = logger;
        }

        public void OnGet()
        {
            this.Identifier = HttpContext.User.Identity.Name;

            // User is visiting the site for the first time. Issue a cookie with an identifier.
            if (string.IsNullOrEmpty(this.Identifier))
            {
                var identity = new ClaimsIdentity(new List<Claim>()
                {
                    new Claim(ClaimTypes.Name, Guid.NewGuid().ToString())
                });
                var principal = new ClaimsPrincipal(identity);

                HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                _logger.LogDebug($"User {this.Identifier} was provided an authentication token.");
            }
        }

        public async Task<ActionResult> OnPost([FromBody] DownloadInfoModel downloadInfo)
        {
            DownloadInfoModelResult result;

            // ModelState must be valid (= url etc.) for the task to be saved in the db.
            if (ModelState.IsValid)
            {
                var downloadTask = new DownloadTask()
                {
                    Downloader = new Guid(HttpContext.User.Identity.Name),
                    Url = downloadInfo.Url,
                    DateAdded = DateTime.Now,
                    AudioFormat = downloadInfo.AudioFormat,
                    VideoFormat = downloadInfo.VideoFormat
                };

                await _context.DownloadTask.AddAsync(downloadTask);
                await _context.SaveChangesAsync();

                result = new DownloadInfoModelResult(true, downloadTask.Id, downloadTask.Url);

                _logger.LogDebug($"New DownloadTask added to the db: Downloader={downloadTask.Downloader}, Url={downloadTask.Url}, Id={downloadTask.Id}");
            }
            else
            {
                result = new DownloadInfoModelResult(false, new Guid(), null);

                _logger.LogDebug($"Download information failed to validate: {downloadInfo.Url}");
            }

            return new JsonResult(result);
        }
    }
}
