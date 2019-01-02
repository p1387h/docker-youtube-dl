using DockerYoutubeDL.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.Services
{
    public class DownloadContextFactory : IDesignTimeDbContextFactory<DownloadContext>
    {
        private InMemoryDatabaseRoot _root;

        public DownloadContextFactory(InMemoryDatabaseRoot root)
        {
            if (root == null)
            {
                throw new ArgumentException();
            }

            _root = root;
        }

        public DownloadContext CreateDbContext(string[] args)
        {
            // The used database must be the same as the one defined in the Startup.cs class.
            var optionsBuilder = new DbContextOptionsBuilder<DownloadContext>();
            optionsBuilder.UseInMemoryDatabase("internalDownloadDb", _root);

            return new DownloadContext(optionsBuilder.Options);
        }
    }
}
