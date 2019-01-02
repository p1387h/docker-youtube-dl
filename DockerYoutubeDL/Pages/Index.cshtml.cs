using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DockerYoutubeDL.Pages
{
    public class IndexModel : PageModel
    {
        public string Identifier { get; set; }

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
    }
}
