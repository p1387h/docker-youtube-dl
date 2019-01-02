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

namespace DockerYoutubeDL.Pages
{
    // Order 1001 since the basic check for antiforgery tokens has the order 1000.
    [IgnoreAntiforgeryToken(Order = 1001)]
    public class IndexModel : PageModel
    {
        public string Identifier { get; set; }

        private DownloadContext _context;

        public IndexModel(DownloadContext context)
        {
            if (context == null)
            {
                throw new ArgumentException();
            }

            _context = context;
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
                    Url = downloadInfo.Url
                };

                await _context.DownloadTask.AddAsync(downloadTask);
                await _context.SaveChangesAsync();

                result = new DownloadInfoModelResult(true);
            }
            else
            {
                result = new DownloadInfoModelResult(false);
            }

            return new JsonResult(result);
        }
    }
}
