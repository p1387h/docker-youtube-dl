using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DockerYoutubeDL.DAL;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using DockerYoutubeDL.SignalR;
using DockerYoutubeDL.Services;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;
using Hangfire;
using Hangfire.MemoryStorage;

namespace DockerYoutubeDL
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie();

            // In memory db: 
            // root needed since the provider in the custom factory differs from the one used here!
            var root = new InMemoryDatabaseRoot();
            services.AddSingleton<InMemoryDatabaseRoot>(root);
            services.AddDbContext<DownloadContext>(options => options.UseInMemoryDatabase("internalDownloadDb", root));
            // Allows for the injection of a factory that can be used to generate a db context.
            services.AddSingleton<IDesignTimeDbContextFactory<DownloadContext>, DownloadContextFactory>();

            // Component for generating the paths of the download folders:
            services.AddTransient<DownloadPathGenerator>();

            // Component for notifying the clients:
            services.AddTransient<NotificationService>();

            // SignalR components:
            services.AddSignalR();

            // Hangfire:
            services.AddTransient<InfoService>();
            services.AddTransient<DownloadService>();
            services.AddTransient<HangfireExecutionService>();
            services.AddHangfire(x => x.UseMemoryStorage());

            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // Enable changing the base path to custom value. I.e.:
            // localhost/API => localhost/customBasePath/API
            app.UsePathBase(this.Configuration.GetValue<string>("BasePath"));

            app.UseHangfireServer(new BackgroundJobServerOptions()
            {
                // Allow only a single download to be active at once.
                WorkerCount = 1
            });
            app.UseStaticFiles();

            app.UseSignalR(options => options.MapHub<UpdateHub>("/ws"));
            app.UseMvc();
        }
    }
}
